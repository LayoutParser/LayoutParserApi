using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml.Linq;

using LayoutParserApi.Services.Interfaces;

using Microsoft.Extensions.Options;

using XslSynth.Core;
using XslSynth.Model;
using XslSynth.Synthesis;

namespace LayoutParserApi.Services.Transformation.Ai
{
    /// <summary>
    /// Implementação real de <see cref="IGeneratedMapperArtifactService"/> — issue #438. Reaproveita
    /// o loop determinístico já usado pelo CLI <c>ai/XslSynth</c> (<see cref="LinkMappingTranspiler"/>,
    /// <see cref="DslBlockInterpreter"/>/<see cref="DslRuleTranslator"/>, <see cref="CandidateBuilder"/>,
    /// <see cref="CoverageValidator"/>, <see cref="ProvenancePublisher"/>) rodando IN-PROCESS na API,
    /// em vez de duplicar essa sequência (Protocolo IDS — REUSAR). Ollama é OPCIONAL/best-effort
    /// (mesmo padrão de <see cref="RepairOrchestratorXslSynthesizerService"/>): sem ele, o candidato
    /// ainda sai — via <see cref="DslBlockInterpreter"/> (multi-saída determinístico) e o fallback
    /// 1-saída de <see cref="DslRuleTranslator"/>.
    ///
    /// <para><b>Divergência deliberada do ADR (documentada em memória de sessão, não é omissão):</b>
    /// o catálogo GUID→XPath do layout de destino (<c>GuidXPathCatalog</c>, usado por
    /// <see cref="LayoutTreeService"/>/A3) NÃO é resolvido aqui — os LinkMappings ficam com destino
    /// só até a folha (símbolo), não o caminho completo. Resolver isso exigiria a mesma resolução de
    /// layout de destino que <c>LayoutTreeService</c> já faz; fora do escopo mínimo deste MVP (a
    /// geração lazy ainda funciona, só com o mesmo limite honesto já documentado no CLI standalone).</para>
    /// </summary>
    public sealed class GeneratedMapperArtifactService : IGeneratedMapperArtifactService
    {
        /// <summary>ADR §2/§5 — rótulo obrigatório: cobertura é contra o DSL declarado, nunca execução real.</summary>
        public const string ValidationBasisDeclaredDsl = "declared_dsl";

        private readonly ICachedMapperService _mapperService;
        private readonly IGeneratedMapperArtifactStore _store;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<GeneratedMapperArtifactService> _logger;
        private readonly OllamaOptions _ollamaOptions;
        private readonly RealMapperParser _realParser = new();
        private readonly MapperExtractor _sampleExtractor = new();

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            // Mesmo motivo do resto do projeto (dotnet-standards.md): preserva XPath/regex legíveis.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public GeneratedMapperArtifactService(
            ICachedMapperService mapperService,
            IGeneratedMapperArtifactStore store,
            IServiceScopeFactory scopeFactory,
            ILogger<GeneratedMapperArtifactService> logger,
            IOptions<XmlAnalysis.OllamaOptions> ollamaOptions)
        {
            _mapperService = mapperService;
            _store = store;
            _scopeFactory = scopeFactory;
            _logger = logger;
            _ollamaOptions = new OllamaOptions(ollamaOptions.Value.Url, ollamaOptions.Value.Model);
        }

        public async Task<GeneratedMapperArtifactResponse?> GetOrTriggerAsync(
            string mapperGuid, string correlationId, CancellationToken cancellationToken)
        {
            var safeMapperGuid = Services.Logging.LogMessageSanitizer.Sanitize(mapperGuid);

            var mapper = await ResolveMapperAsync(mapperGuid, cancellationToken);
            if (mapper is null)
                return null;

            var mapperVo = ParseMapperVo(mapper.DecryptedContent!, mapperGuid);
            if (mapperVo is null)
            {
                _logger.LogWarning("MapeadorVO do mapper {MapperGuid} não é XML bem-formado — geração automática não pode prosseguir.", safeMapperGuid);
                return new GeneratedMapperArtifactResponse(mapperGuid, GeneratedMapperArtifactStatus.None, null, null, null, null, null);
            }

            var currentHash = ComputeMapperVoHash(mapperVo);
            var existing = await _store.GetAsync(mapperGuid, cancellationToken);

            // "stale" (ADR §5/§4): existia candidato "ready", mas o hash do MapperVo mudou desde a
            // última geração — o mapper foi editado no catálogo. Recalculado em LEITURA (não
            // persistido como estado à parte), mesmo espírito do gatilho de revalidação do ADR §4,
            // só que sem o job periódico (fora deste MVP) — aqui a checagem acontece a cada GET.
            var effectiveStatus = existing switch
            {
                null => GeneratedMapperArtifactStatus.None,
                { Status: GeneratedMapperArtifactStatus.Generating } => GeneratedMapperArtifactStatus.Generating,
                { Status: GeneratedMapperArtifactStatus.Ready } when existing.MapperVoHash != currentHash => GeneratedMapperArtifactStatus.Stale,
                _ => existing.Status,
            };

            if (effectiveStatus is GeneratedMapperArtifactStatus.None or GeneratedMapperArtifactStatus.Stale)
            {
                var began = await _store.TryBeginGeneratingAsync(mapperGuid, correlationId, cancellationToken);
                if (began)
                {
                    _logger.LogInformation(
                        "Disparando geração automática lazy de TCL/XSL/XSLT para o mapper {MapperGuid} (status anterior={StatusAnterior}, correlationId={CorrelationId})",
                        safeMapperGuid, effectiveStatus, correlationId);
                    TriggerBackgroundGeneration(mapperGuid, mapper.Name, correlationId);
                }
                else
                {
                    _logger.LogInformation(
                        "Geração automática do mapper {MapperGuid} já está em andamento (outra chamada concorrente venceu a corrida) — não disparando de novo.",
                        safeMapperGuid);
                }

                return new GeneratedMapperArtifactResponse(
                    mapperGuid, GeneratedMapperArtifactStatus.Generating, null, null, null, null, correlationId);
            }

            if (effectiveStatus == GeneratedMapperArtifactStatus.Generating)
                return new GeneratedMapperArtifactResponse(mapperGuid, GeneratedMapperArtifactStatus.Generating, null, null, null, null, existing?.CorrelationId);

            // Ready.
            return new GeneratedMapperArtifactResponse(
                mapperGuid, GeneratedMapperArtifactStatus.Ready, existing!.Content, existing.CoverageJson,
                existing.ValidationBasis, existing.GeneratedAt, existing.CorrelationId);
        }

        /// <summary>
        /// Fire-and-forget (dotnet-standards.md): sobrevive ao fim da request HTTP via
        /// <see cref="IServiceScopeFactory"/> (os serviços usados aqui são Scoped) — mesmo padrão de
        /// <c>TransformationExecutionController.TryPersistFieldCorrectionContext</c>.
        /// </summary>
        private void TriggerBackgroundGeneration(string mapperGuid, string? mapperName, string correlationId)
        {
            var safeMapperGuid = Services.Logging.LogMessageSanitizer.Sanitize(mapperGuid);
            _ = Task.Run(async () =>
            {
                using var scope = _scopeFactory.CreateScope();
                var scopedMapperService = scope.ServiceProvider.GetRequiredService<ICachedMapperService>();
                var scopedStore = scope.ServiceProvider.GetRequiredService<IGeneratedMapperArtifactStore>();
                var scopedLogger = scope.ServiceProvider.GetRequiredService<ILogger<GeneratedMapperArtifactService>>();

                try
                {
                    var mapper = await ResolveMapperAsync(scopedMapperService, mapperGuid, CancellationToken.None);
                    if (mapper is null)
                    {
                        scopedLogger.LogWarning("Mapper {MapperGuid} desapareceu do catálogo entre o disparo e a execução da geração automática — abortando.", safeMapperGuid);
                        await scopedStore.FailAsync(mapperGuid, CancellationToken.None);
                        return;
                    }

                    var mapperVo = ParseMapperVo(mapper.DecryptedContent!, mapperGuid);
                    if (mapperVo is null)
                    {
                        await scopedStore.FailAsync(mapperGuid, CancellationToken.None);
                        return;
                    }

                    var (content, coverageJson) = await SynthesizeAsync(mapperVo, scopedLogger, safeMapperGuid);
                    var mapperVoHash = ComputeMapperVoHash(mapperVo);

                    await scopedStore.CompleteAsync(
                        mapperGuid, content, coverageJson, ValidationBasisDeclaredDsl, mapperVoHash, correlationId, CancellationToken.None);

                    scopedLogger.LogInformation(
                        "Geração automática concluída para o mapper {MapperGuid} (correlationId={CorrelationId})",
                        safeMapperGuid, correlationId);
                }
                catch (Exception ex)
                {
                    // Resiliência (dotnet-standards.md): Ollama/Sysmiddle/SQL podem falhar — nunca
                    // propaga exceção não tratada no fire-and-forget; reverte para "none" (permite retry).
                    scopedLogger.LogError(ex, "Falha na geração automática de TCL/XSL/XSLT para o mapper {MapperGuid} — revertendo para 'none' (nova tentativa no próximo GET).", safeMapperGuid);
                    await scopedStore.FailAsync(mapperGuid, CancellationToken.None);
                }
            }, CancellationToken.None);
        }

        /// <summary>
        /// Mesma sequência de <c>ai/XslSynth/Program.cs</c> (passos 1-4 + publicação A6), sem
        /// gabarito de runtime (ADR §2 — bloqueio de licença FiatMQ segue de pé): LinkMappings →
        /// folhas por código; Rules → DSL interpretada/traduzida (Ollama best-effort); candidato
        /// único; cobertura contra o próprio <see cref="MapperVo"/>; publicação sem XComment de debug.
        /// </summary>
        private async Task<(string Content, string CoverageJson)> SynthesizeAsync(
            MapperVo mapper, ILogger logger, string safeMapperGuid)
        {
            var links = new LinkMappingTranspiler { TargetCatalog = null }.Transpile(mapper);

            OllamaClient? ollama = null;
            try
            {
                var client = new OllamaClient(
                    msg => logger.LogDebug("[Ollama] {Message}", msg),
                    model: _ollamaOptions.Model,
                    url: _ollamaOptions.Url);
                if (await client.IsReachableAsync())
                    ollama = client;
                else
                    logger.LogInformation("Ollama indisponível para o mapper {MapperGuid} — geração cai para o fallback determinístico (sem IA).", safeMapperGuid);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Falha ao checar disponibilidade do Ollama — geração cai para o fallback determinístico (mapper {MapperGuid}).", safeMapperGuid);
            }

            var interpreter = new DslBlockInterpreter();
            var translator = new DslRuleTranslator(ollama, msg => logger.LogDebug("{Message}", msg));
            var translations = new List<RuleTranslation>(mapper.Rules.Count);
            foreach (var rule in mapper.Rules)
            {
                var emissions = interpreter.Interpret(rule);
                if (emissions.Count > 0)
                {
                    translations.AddRange(emissions);
                    continue;
                }

                translations.Add(await translator.TranslateAsync(rule));
            }

            var rootName = DetermineRoot(mapper);
            var (candidate, _) = new CandidateBuilder().Build(rootName, links.Leaves, translations);

            var coverage = new CoverageValidator().Validate(candidate, mapper);
            var publish = ProvenancePublisher.Publish(mapper, targetCatalog: null, translations, candidate);

            var coverageDto = new
            {
                compiles = coverage.Compiles,
                compileError = coverage.CompileError,
                linksCovered = coverage.LinksCovered,
                linksTotal = coverage.LinksTotal,
                linkPct = coverage.LinkPct,
                rulesCovered = coverage.RulesCovered,
                rulesTotal = coverage.RulesTotal,
                rulePct = coverage.RulePct,
                provenanceEntries = publish.Sidecar.Total,
                linkMappingsSemFolha = publish.Sidecar.LinkMappingsSemFolha.Count,
            };

            return (publish.CandidatoPublicavel.ToString(), JsonSerializer.Serialize(coverageDto, JsonOpts));
        }

        // Mesma heurística de ai/XslSynth/Program.cs::DetermineRoot — raiz de saída é o primeiro
        // segmento mais frequente entre os paths T. das regras.
        private static string DetermineRoot(MapperVo mapper)
        {
            var root = mapper.Rules
                .Select(r => r.TargetPath)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => Xslt.Segments(p!).FirstOrDefault())
                .Where(s => !string.IsNullOrEmpty(s))
                .GroupBy(s => s)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key!)
                .FirstOrDefault();
            return root ?? "nfeProc";
        }

        /// <summary>
        /// Hash determinístico sobre o DSL NORMALIZADO (LinkMappings + Rules), não sobre o XML
        /// criptografado bruto (ADR §4 — evita falso-positivo de "mapper mudou" por diferença de
        /// formatação irrelevante). Usado para detectar <c>stale</c>.
        /// </summary>
        public static string ComputeMapperVoHash(MapperVo mapper)
        {
            var sb = new StringBuilder();
            foreach (var link in mapper.LinkMappings.OrderBy(l => l.Sequence).ThenBy(l => l.Name, StringComparer.Ordinal))
            {
                sb.Append("L|").Append(link.Name).Append('|').Append(link.ElementGuid).Append('|')
                  .Append(link.InputGuid).Append('|').Append(link.TargetGuid).Append('|')
                  .Append(link.TargetLeafName).Append('\n');
            }
            foreach (var rule in mapper.Rules.OrderBy(r => r.Sequence).ThenBy(r => r.Name, StringComparer.Ordinal))
            {
                sb.Append("R|").Append(rule.Name).Append('|').Append(rule.TargetPath).Append('|')
                  .Append(rule.ContentValue).Append('\n');
            }

            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
            return Convert.ToHexString(bytes);
        }

        private Task<LayoutParserApi.Models.Entities.Mapper?> ResolveMapperAsync(string mapperGuid, CancellationToken cancellationToken)
            => ResolveMapperAsync(_mapperService, mapperGuid, cancellationToken);

        private static async Task<LayoutParserApi.Models.Entities.Mapper?> ResolveMapperAsync(
            ICachedMapperService mapperService, string mapperGuid, CancellationToken cancellationToken)
        {
            var allMappers = await mapperService.GetAllMappersAsync();
            var mapper = allMappers.FirstOrDefault(m => string.Equals(m.MapperGuid, mapperGuid, StringComparison.OrdinalIgnoreCase));
            return mapper is null || string.IsNullOrWhiteSpace(mapper.DecryptedContent) ? null : mapper;
        }

        /// <summary>Mesmo padrão de <c>RepairOrchestratorXslSynthesizerService.ParseMapperVo</c>:
        /// tenta o parser real (Sysmiddle), cai para o extrator de amostra (formato MVP).</summary>
        private MapperVo? ParseMapperVo(string decryptedContent, string mapperGuid)
        {
            try
            {
                var doc = XDocument.Parse(decryptedContent);
                try
                {
                    return _realParser.Parse(doc);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "RealMapperParser não reconheceu o MapeadorVO — tentando MapperExtractor (formato sample)");
                    return _sampleExtractor.Extract(doc);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MapeadorVO do mapper {MapperGuid} não é XML bem-formado", Services.Logging.LogMessageSanitizer.Sanitize(mapperGuid));
                return null;
            }
        }

        private sealed record OllamaOptions(string Url, string Model);
    }
}
