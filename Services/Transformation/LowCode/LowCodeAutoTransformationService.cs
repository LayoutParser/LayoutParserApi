using System.Security.Cryptography;
using System.Text;
using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Transformation;
using LayoutParserApi.Services.Database;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Services.Transformation.LowCode
{
    /// <summary>
    /// Executa transformação low-code automaticamente (sem necessidade de chamada manual)
    /// usando o layoutGuid selecionado no front para identificar o MapperGuid no banco.
    /// </summary>
    public class LowCodeAutoTransformationService
    {
        private readonly ILogger<LowCodeAutoTransformationService> _logger;
        // ✅ Singleton não pode injetar serviço Scoped direto (quebra a validação de DI em Development).
        // Usamos IServiceScopeFactory e resolvemos o MapperDatabaseService dentro do escopo do background.
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly LowCodeTransformationService _lowCode;
        private readonly LowCodeRunnerOptions _opt;
        private readonly string _storePath;

        public LowCodeAutoTransformationService(
            ILogger<LowCodeAutoTransformationService> logger,
            IServiceScopeFactory scopeFactory,
            LowCodeTransformationService lowCode,
            IConfiguration configuration,
            IOptions<LowCodeRunnerOptions> options)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _lowCode = lowCode;
            _opt = options.Value;
            _storePath = configuration["ML:LowCodeTransformationsPath"]
                ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MLData", "LowCodeTransformations");
            Directory.CreateDirectory(_storePath);
        }

        public Task RunInBackgroundAsync(string layoutGuid, string layoutName, string txtContent, string detectedType, string originalFileName)
        {
            // fire-and-forget (chamador não quer/precisa do resultado — usar RunAsync quando precisar)
            return Task.Run(async () =>
            {
                try
                {
                    await TransformAndPersistAsync(layoutGuid, layoutName, txtContent, detectedType, originalFileName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Falha no auto-transform low-code");
                }
            });
        }

        /// <summary>
        /// Mesma operação de <see cref="RunInBackgroundAsync"/>, mas retornando o resultado (ao invés de
        /// fire-and-forget) para permitir entrega SÍNCRONA com teto de tempo por quem chama (ex.:
        /// <c>ParseController</c>, que aguarda até <c>LowCode:SyncDeliveryTimeoutSeconds</c> e cai para
        /// processamento em background se estourar — a persistência em disco acontece de qualquer forma,
        /// dentro desta mesma chamada, então o trabalho não se perde mesmo se o chamador parar de esperar.
        /// Falhas estruturais (ex.: banco fora do ar ao buscar candidatos) propagam como exceção — cabe ao
        /// chamador decidir como degradar (nunca deve virar 500 do endpoint principal de parse).
        /// </summary>
        public Task<LowCodeAutoTransformResult> RunAsync(string layoutGuid, string layoutName, string txtContent, string detectedType, string originalFileName)
            => TransformAndPersistAsync(layoutGuid, layoutName, txtContent, detectedType, originalFileName);

        private async Task<LowCodeAutoTransformResult> TransformAndPersistAsync(string layoutGuid, string layoutName, string txtContent, string detectedType, string originalFileName)
        {
            if (string.IsNullOrWhiteSpace(layoutGuid) || string.IsNullOrWhiteSpace(txtContent))
                return new LowCodeAutoTransformResult { Applicable = false };

            // Selecionar candidatos (filtrado por ProjectId e AllowedPackageGuids)
            // ✅ Escopo próprio para o serviço Scoped dentro do fire-and-forget
            using var scope = _scopeFactory.CreateScope();
            var mapperDb = scope.ServiceProvider.GetRequiredService<MapperDatabaseService>();

            // ✅ Seleção multi-candidato: busca TODOS os candidatos plausíveis (mesma prioridade de
            // sempre — input match > target match > mais recente), já deduplicados por MapperGuid.
            var ranked = await mapperDb.GetRankedMapperCandidatesForLayoutGuidAsync(
                layoutGuid,
                _opt.ProjectId,
                _opt.AllowedPackageGuids);

            if (ranked.Count == 0 || string.IsNullOrWhiteSpace(ranked[0].MapperGuid))
            {
                _logger.LogWarning("Nenhum mapper encontrado para layoutGuid={LayoutGuid} nos pacotes permitidos", layoutGuid);
                return new LowCodeAutoTransformResult { Applicable = false };
            }

            // N==1: comportamento EXATAMENTE igual ao de sempre (sem overhead, sem mudança de shape
            // dos artefatos persistidos). N>1 depois de deduplicar por MapperGuid = candidatos
            // genuinamente distintos (não apenas desempate de recência do mesmo mapper).
            if (ranked.Count == 1)
            {
                var mapper = ranked[0];
                try
                {
                    var single = await TransformSingleAndPersistAsync(mapper, layoutGuid, layoutName, txtContent, detectedType, originalFileName);
                    return new LowCodeAutoTransformResult
                    {
                        Applicable = true,
                        MultiCandidate = false,
                        Candidates = new List<LowCodeCandidateResult> { single }
                    };
                }
                catch (Exception ex)
                {
                    // ✅ Mesmo tratamento de falha isolada por candidato do caminho multi (não propaga
                    // pro chamador síncrono como exceção não tratada) — mas SEM alterar a semântica de
                    // persistência em disco de sempre: se a transformação falhar aqui (ex.: timeout do
                    // runner), nada é persistido, exatamente como antes desta mudança (comportamento
                    // inalterado do fire-and-forget, só passou a também ser observável por RunAsync).
                    _logger.LogWarning(ex,
                        "Falha na transformação low-code (candidato único) mapper={MapperGuid} ({MapperName}) para layout={LayoutName} ({LayoutGuid})",
                        mapper.MapperGuid, mapper.Name, layoutName, layoutGuid);

                    return new LowCodeAutoTransformResult
                    {
                        Applicable = true,
                        MultiCandidate = false,
                        Candidates = new List<LowCodeCandidateResult>
                        {
                            new LowCodeCandidateResult
                            {
                                MapperGuid = mapper.MapperGuid,
                                MapperName = mapper.Name,
                                TargetLayoutGuid = mapper.TargetLayoutGuidFromXml ?? mapper.TargetLayoutGuid,
                                PackageGuid = mapper.PackageGuid,
                                Success = false,
                                ErrorMessage = ex.Message
                            }
                        }
                    };
                }
            }

            var topN = ranked.Take(Math.Max(1, _opt.MultiCandidateTopN)).ToList();
            _logger.LogInformation(
                "AutoTransform low-code: {Count} candidatos genuinamente plausíveis para layout={LayoutName} ({LayoutGuid}) — capado em top-{TopN}",
                topN.Count, layoutName, layoutGuid, _opt.MultiCandidateTopN);

            var candidates = await TransformMultiCandidateAndPersistAsync(topN, layoutGuid, layoutName, txtContent, detectedType, originalFileName);
            return new LowCodeAutoTransformResult { Applicable = true, MultiCandidate = true, Candidates = candidates };
        }

        /// <summary>
        /// Caminho de hoje (N==1), inalterado na persistência: 1 mapper, 1 transformação, 1 conjunto de
        /// artefatos. Passou a retornar o <see cref="LowCodeCandidateResult"/> correspondente (além de
        /// persistir) para permitir entrega síncrona via <see cref="RunAsync"/> — se a transformação
        /// falhar, a exceção propaga (igual sempre fez) e quem persiste o tratamento é o chamador.
        /// </summary>
        private async Task<LowCodeCandidateResult> TransformSingleAndPersistAsync(Mapper mapper, string layoutGuid, string layoutName, string txtContent, string detectedType, string originalFileName)
        {
            _logger.LogInformation("AutoTransform low-code: layout={LayoutName} ({LayoutGuid}) mapper={MapperName} ({MapperGuid})",
                layoutName, layoutGuid, mapper.Name, mapper.MapperGuid);

            // Executar low-code
            var lowCodeXml = await _lowCode.TransformAsync(
                txtContent,
                mapperId: mapper.MapperGuid,
                mapperName: null,
                fileName: originalFileName);

            // Persistir para aprendizado contínuo
            var sha = ComputeSha256(txtContent);
            var dateFolder = DateTime.UtcNow.ToString("yyyyMMdd");
            var folder = Path.Combine(_storePath, dateFolder);
            Directory.CreateDirectory(folder);

            var baseName = $"{sha}_{DateTime.UtcNow:HHmmss}";
            var metaPath = Path.Combine(folder, $"{baseName}.meta.json");
            var inPath = Path.Combine(folder, $"{baseName}.input.txt");
            var outPath = Path.Combine(folder, $"{baseName}.lowcode.xml");

            await File.WriteAllTextAsync(inPath, txtContent, Encoding.UTF8);
            await File.WriteAllTextAsync(outPath, lowCodeXml ?? "", Encoding.UTF8);

            var meta = new
            {
                createdAtUtc = DateTime.UtcNow,
                layoutGuid,
                layoutName,
                detectedType,
                originalFileName,
                mapperGuid = mapper.MapperGuid,
                mapperName = mapper.Name,
                packageGuid = mapper.PackageGuid,
                projectId = mapper.ProjectId,
                sha256 = sha,
                inputLength = txtContent.Length,
                outputLength = (lowCodeXml ?? "").Length
            };
            var json = System.Text.Json.JsonSerializer.Serialize(meta, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(metaPath, json, Encoding.UTF8);

            return new LowCodeCandidateResult
            {
                MapperGuid = mapper.MapperGuid,
                MapperName = mapper.Name,
                TargetLayoutGuid = mapper.TargetLayoutGuidFromXml ?? mapper.TargetLayoutGuid,
                PackageGuid = mapper.PackageGuid,
                Success = true,
                OutputXml = lowCodeXml
            };
        }

        /// <summary>
        /// Caminho multi-candidato (N&gt;1 mapeadores genuinamente plausíveis): roda a transformação
        /// low-code contra CADA candidato em paralelo (Task.WhenAll) e persiste todos os N resultados,
        /// cada um tagueado com MapperGuid/Name, TargetLayoutGuid e indicador de sucesso/erro.
        /// Resiliência: uma falha de candidato individual é capturada e não derruba os demais.
        /// Retorna a lista de resultados (além de persistir) para permitir entrega síncrona via <see cref="RunAsync"/>.
        /// </summary>
        private async Task<List<LowCodeCandidateResult>> TransformMultiCandidateAndPersistAsync(List<Mapper> candidates, string layoutGuid, string layoutName, string txtContent, string detectedType, string originalFileName)
        {
            var tasks = candidates.Select(async mapper =>
            {
                try
                {
                    var xml = await _lowCode.TransformAsync(
                        txtContent,
                        mapperId: mapper.MapperGuid,
                        mapperName: null,
                        fileName: originalFileName);

                    return new LowCodeCandidateResult
                    {
                        MapperGuid = mapper.MapperGuid,
                        MapperName = mapper.Name,
                        TargetLayoutGuid = mapper.TargetLayoutGuidFromXml ?? mapper.TargetLayoutGuid,
                        PackageGuid = mapper.PackageGuid,
                        Success = true,
                        OutputXml = xml
                    };
                }
                catch (Exception ex)
                {
                    // ✅ Falha de UM candidato não derruba os demais — capturada aqui, dentro da própria
                    // task, para que Task.WhenAll não propague a exceção e aborte o restante.
                    _logger.LogWarning(ex,
                        "Falha na transformação low-code do candidato mapper={MapperGuid} ({MapperName}) para layout={LayoutName} ({LayoutGuid})",
                        mapper.MapperGuid, mapper.Name, layoutName, layoutGuid);

                    return new LowCodeCandidateResult
                    {
                        MapperGuid = mapper.MapperGuid,
                        MapperName = mapper.Name,
                        TargetLayoutGuid = mapper.TargetLayoutGuidFromXml ?? mapper.TargetLayoutGuid,
                        PackageGuid = mapper.PackageGuid,
                        Success = false,
                        ErrorMessage = ex.Message
                    };
                }
            });

            var results = await Task.WhenAll(tasks);

            _logger.LogInformation(
                "AutoTransform low-code multi-candidato: layout={LayoutName} ({LayoutGuid}) {SuccessCount}/{Total} candidatos OK",
                layoutName, layoutGuid, results.Count(r => r.Success), results.Length);

            // Persistir para aprendizado contínuo: 1 input compartilhado + 1 meta.json com o array de
            // candidatos + 1 arquivo de saída por candidato bem-sucedido.
            var sha = ComputeSha256(txtContent);
            var dateFolder = DateTime.UtcNow.ToString("yyyyMMdd");
            var folder = Path.Combine(_storePath, dateFolder);
            Directory.CreateDirectory(folder);

            var baseName = $"{sha}_{DateTime.UtcNow:HHmmss}";
            var metaPath = Path.Combine(folder, $"{baseName}.meta.json");
            var inPath = Path.Combine(folder, $"{baseName}.input.txt");

            await File.WriteAllTextAsync(inPath, txtContent, Encoding.UTF8);

            var candidateMeta = new List<object>();
            for (int i = 0; i < results.Length; i++)
            {
                var r = results[i];
                string? outputFile = null;
                if (r.Success)
                {
                    outputFile = $"{baseName}.cand{i}_{SanitizeForFileName(r.MapperGuid)}.lowcode.xml";
                    await File.WriteAllTextAsync(Path.Combine(folder, outputFile), r.OutputXml ?? "", Encoding.UTF8);
                }

                candidateMeta.Add(new
                {
                    mapperGuid = r.MapperGuid,
                    mapperName = r.MapperName,
                    targetLayoutGuid = r.TargetLayoutGuid,
                    packageGuid = r.PackageGuid,
                    success = r.Success,
                    outputFile,
                    outputLength = r.Success ? (r.OutputXml ?? "").Length : 0,
                    errorMessage = r.Success ? null : r.ErrorMessage
                });
            }

            var meta = new
            {
                createdAtUtc = DateTime.UtcNow,
                layoutGuid,
                layoutName,
                detectedType,
                originalFileName,
                sha256 = sha,
                inputLength = txtContent.Length,
                multiCandidate = true,
                candidateCount = results.Length,
                successCount = results.Count(r => r.Success),
                candidates = candidateMeta
            };
            var json = System.Text.Json.JsonSerializer.Serialize(meta, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(metaPath, json, Encoding.UTF8);

            return results.ToList();
        }

        private static string SanitizeForFileName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "unknown";
            var invalid = Path.GetInvalidFileNameChars();
            return new string(s.Where(c => !invalid.Contains(c)).ToArray());
        }

        private static string ComputeSha256(string input)
        {
            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(input ?? "");
            var hash = sha.ComputeHash(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}


