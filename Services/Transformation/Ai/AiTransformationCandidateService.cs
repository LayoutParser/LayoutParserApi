using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml.Linq;

using LayoutParserApi.Models.Transformation;
using LayoutParserApi.Services.Interfaces;

using XslSynth.Core;
using XslSynth.Model;
using XslSynth.Synthesis;

namespace LayoutParserApi.Services.Transformation.Ai
{
    /// <summary>
    /// Implementação do pathway IA (Issue #40). Reaproveita o loop RAG de
    /// <c>XslSynth.Core</c> (<see cref="RepairOrchestrator"/>, extraído de <c>ai/XslSynth</c> para a
    /// classlib compartilhada <c>ai/XslSynth.Core</c> — ver
    /// docs/architecture/pathway-ia-execute-candidates.md §4.1) dentro do processo da API.
    ///
    /// <para><b>Singleton</b> — precisa sobreviver ao request HTTP que disparou
    /// <see cref="EnqueueAsync"/> (fire-and-forget via <c>Task.Run</c>). Dependências Scoped
    /// (<see cref="ICachedMapperService"/>) são resolvidas via <see cref="IServiceScopeFactory"/>
    /// dentro do job, nunca guardadas como campo.</para>
    ///
    /// <para><b>Persistência mínima</b> (§4.2 do desenho): um arquivo JSON por ticket em
    /// <c>MLData/AiTransformationCandidates/{ticket}.json</c>, mesmo padrão de diretório do
    /// <c>LowCodeTransformationStore</c>. Serve de cache de leitura (<see cref="_jobs"/>) e de
    /// dataset rotulado (gabarito + XSLT convergido) para RAG/few-shot futuro.</para>
    ///
    /// <para><b>Desvio do desenho original:</b> em vez de <see cref="XsdValidator"/> (que exige um
    /// caminho de arquivo XSD fixo, como no CLI standalone), a validação usa a resolução de XSD
    /// real da API (<c>XsdValidation:BasePath</c>/<c>DocumentTypes:NFe:XsdVersion</c> do
    /// appsettings), buscando o maior <c>*.xsd</c> na pasta da versão — mesma heurística de
    /// <c>XsdValidationService.FindXsdFile</c> (privado, não reutilizável por injeção). Falha
    /// nessa resolução (pasta/arquivo ausente) não derruba o job: o loop roda até o teto de
    /// iterações e termina "failed" com diagnóstico claro (RemainingDiffs/XsdValid refletem a
    /// causa), nunca lança.</para>
    /// </summary>
    public class AiTransformationCandidateService : IAiTransformationCandidateService
    {
        // Teto técnico de sanidade (não é o timeout de PRODUTO — o dono explicitamente não quis
        // um; ver §2.3/§6 do desenho). Só evita job "running" pra sempre se o Ollama travar sem
        // nunca completar nem cancelar. Bem folgado de propósito.
        private static readonly TimeSpan JobSanityTimeout = TimeSpan.FromMinutes(45);

        private const int MaxIterations = 5;

        private readonly ILogger<AiTransformationCandidateService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _configuration;
        private readonly string _storePath;

        private readonly ConcurrentDictionary<string, AiCandidateStatus> _jobs = new();

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            // Mesmo encoder do resto do projeto — preserva XML (<, >, &) intacto no payload.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true
        };

        public AiTransformationCandidateService(
            ILogger<AiTransformationCandidateService> logger,
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _configuration = configuration;

            _storePath = configuration["ML:AiTransformationCandidatesPath"]
                ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MLData", "AiTransformationCandidates");

            try
            {
                Directory.CreateDirectory(_storePath);
            }
            catch (Exception ex)
            {
                // Degrade: sem disco, o serviço ainda funciona (cache em memória), só perde
                // persistência entre reinícios do processo.
                _logger.LogWarning(ex, "Não foi possível criar o diretório de persistência do pathway IA em {Path}", _storePath);
            }
        }

        public Task EnqueueAsync(
            string ticket,
            string layoutName,
            Guid layoutGuid,
            string mapperGuid,
            string inputContent,
            string groundTruthXml,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(ticket))
            {
                _logger.LogWarning("EnqueueAsync do pathway IA chamado sem ticket — ignorado");
                return Task.CompletedTask;
            }

            // Estado inicial visível IMEDIATAMENTE (antes do fire-and-forget arrancar), para que
            // GetStatusAsync nunca devolva "not-found" para um ticket recém-disparado.
            _jobs[ticket] = new AiCandidateStatus { Status = "running" };

            // Fire-and-forget: NUNCA lança para o chamador (dotnet-standards.md §Background work).
            // O cancellationToken do request HTTP não deve cancelar o job — o job é assíncrono e
            // sobrevive à resposta; por isso não flui `cancellationToken` para dentro do Task.Run.
            _ = Task.Run(async () =>
            {
                using var sanityCts = new CancellationTokenSource(JobSanityTimeout);
                try
                {
                    await RunJobAsync(ticket, layoutName, layoutGuid, mapperGuid, inputContent, groundTruthXml, sanityCts.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Job IA falhou de forma não tratada para ticket {Ticket}", ticket);
                    var failed = new AiCandidateStatus
                    {
                        Status = "failed",
                        Diagnostics = new AiCandidateDiagnostics { LastError = SafeMessage(ex) }
                    };
                    _jobs[ticket] = failed;
                    await TryPersistAsync(ticket, failed);
                }
            }, CancellationToken.None);

            return Task.CompletedTask;
        }

        public async Task<AiCandidateStatus> GetStatusAsync(string ticket, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(ticket))
                return new AiCandidateStatus { Status = "not-found" };

            if (_jobs.TryGetValue(ticket, out var cached))
                return cached;

            // Cache em memória não sobrevive a reinício do processo — cai pro disco.
            var fromDisk = await TryReadAsync(ticket, cancellationToken);
            if (fromDisk != null)
            {
                _jobs[ticket] = fromDisk;
                return fromDisk;
            }

            return new AiCandidateStatus { Status = "not-found" };
        }

        private async Task RunJobAsync(
            string ticket, string layoutName, Guid layoutGuid, string mapperGuid,
            string inputContent, string groundTruthXml, CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var cachedMapperService = scope.ServiceProvider.GetRequiredService<ICachedMapperService>();

            // ── Passo 1: MapperVO (já descriptografado pela API) → MapperVo do XslSynth.Core ──
            var mappers = await cachedMapperService.GetAllMappersAsync();
            var mapperEntity = mappers?.FirstOrDefault(m =>
                string.Equals(m.MapperGuid, mapperGuid, StringComparison.OrdinalIgnoreCase));

            if (mapperEntity == null || string.IsNullOrWhiteSpace(mapperEntity.DecryptedContent))
            {
                var notApplicable = new AiCandidateStatus
                {
                    Status = "not-applicable",
                    Diagnostics = new AiCandidateDiagnostics
                    {
                        LastError = $"Mapeador {mapperGuid} não encontrado ou sem conteúdo descriptografado — pathway IA não aplicável"
                    }
                };
                _jobs[ticket] = notApplicable;
                await TryPersistAsync(ticket, notApplicable);
                return;
            }

            MapperVo mapperVo;
            try
            {
                mapperVo = new MapperExtractor().Extract(XDocument.Parse(mapperEntity.DecryptedContent));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao extrair MapperVO do mapeador {MapperGuid} (ticket {Ticket})", mapperGuid, ticket);
                var failed = new AiCandidateStatus
                {
                    Status = "failed",
                    Diagnostics = new AiCandidateDiagnostics { LastError = $"MapperVO inválido: {SafeMessage(ex)}" }
                };
                _jobs[ticket] = failed;
                await TryPersistAsync(ticket, failed);
                return;
            }

            XDocument inputDoc;
            try
            {
                // Sysmiddle/loop RAG trabalham sobre XML — se o input original é TXT posicional
                // (formato mais comum de execute-candidates), não há como transpilar direto: a IA
                // depende do XML já estruturado. Nesse caso ainda não convergimos numa fonte XML de
                // entrada equivalente — "not-applicable" em vez de inventar uma.
                inputDoc = XDocument.Parse(inputContent);
            }
            catch (Exception)
            {
                var notApplicable = new AiCandidateStatus
                {
                    Status = "not-applicable",
                    Diagnostics = new AiCandidateDiagnostics
                    {
                        LastError = "InputContent não é XML — pathway IA (loop RAG sobre XSLT) requer entrada XML estruturada"
                    }
                };
                _jobs[ticket] = notApplicable;
                await TryPersistAsync(ticket, notApplicable);
                return;
            }

            var xsdPath = ResolveXsdPath();
            IXslSynthesizer synthesizer = new OllamaXslSynthesizer(msg => _logger.LogDebug("{OllamaLog}", msg));

            SynthesisReport report;
            try
            {
                var orchestrator = new RepairOrchestrator();
                report = await orchestrator.RunAsync(
                    mapperVo,
                    inputDoc,
                    groundTruthXml,
                    xsdPath ?? string.Empty,
                    synthesizer,
                    log: msg => _logger.LogDebug("{AiCandidateLog}", msg),
                    maxIterations: MaxIterations,
                    ct: ct);
            }
            catch (OperationCanceledException)
            {
                var timedOut = new AiCandidateStatus
                {
                    Status = "failed",
                    Diagnostics = new AiCandidateDiagnostics { LastError = "Teto técnico de sanidade excedido (job travado, sem SLA de produto)" }
                };
                _jobs[ticket] = timedOut;
                await TryPersistAsync(ticket, timedOut);
                return;
            }
            catch (Exception ex)
            {
                // Ollama indisponível, HTTP falhando, etc. — degrade gracioso (dotnet-standards.md
                // §Resiliência): nunca deixa o job "running" pra sempre nem propaga a exceção.
                _logger.LogWarning(ex, "Loop RAG falhou para ticket {Ticket} (mapeador {MapperGuid})", ticket, mapperGuid);
                var failed = new AiCandidateStatus
                {
                    Status = "failed",
                    Diagnostics = new AiCandidateDiagnostics { LastError = SafeMessage(ex) }
                };
                _jobs[ticket] = failed;
                await TryPersistAsync(ticket, failed);
                return;
            }

            var diagnostics = new AiCandidateDiagnostics
            {
                Iterations = report.Iterations,
                RemainingDiffs = report.FinalDiffs.Count,
                XsdValid = report.FinalXsd.IsValid,
                LastError = report.Converged ? null : "Loop RAG não convergiu dentro do teto de iterações"
            };

            var status = new AiCandidateStatus
            {
                Status = report.Converged ? "converged" : "failed",
                Diagnostics = diagnostics,
                Candidate = report.Converged
                    ? new TransformationCandidate
                    {
                        CandidateId = $"ia-{mapperGuid}",
                        Pathway = "ia",
                        TransformedXml = report.FinalOutput
                    }
                    : null
            };

            _jobs[ticket] = status;
            await TryPersistAsync(ticket, status);

            _logger.LogInformation(
                "Job IA concluído para ticket {Ticket}: {Status} ({Iterations} iterações, {RemainingDiffs} diffs restantes, XsdValid={XsdValid})",
                ticket, status.Status, diagnostics.Iterations, diagnostics.RemainingDiffs, diagnostics.XsdValid);
        }

        /// <summary>
        /// Mesma heurística de <c>XsdValidationService.FindXsdFile</c> (privado, não injetável):
        /// maior <c>*.xsd</c> dentro de <c>XsdValidation:BasePath\{XsdVersion}</c>. Escopo hoje é
        /// só NFe (Fiat) — não generaliza por tipo de documento antes de ter corpus de outro
        /// cliente (ver §6 "Escopo hoje = só Fiat" do desenho).
        /// </summary>
        private string? ResolveXsdPath()
        {
            try
            {
                var basePath = _configuration["XsdValidation:BasePath"];
                var version = _configuration["XsdValidation:DocumentTypes:NFe:XsdVersion"];
                if (string.IsNullOrWhiteSpace(basePath) || string.IsNullOrWhiteSpace(version))
                    return null;

                var versionPath = Path.Combine(basePath, version);
                if (!Directory.Exists(versionPath))
                    return null;

                return Directory.GetFiles(versionPath, "*.xsd", SearchOption.AllDirectories)
                    .OrderByDescending(f => new FileInfo(f).Length)
                    .FirstOrDefault();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao resolver caminho do XSD para o pathway IA — validação XSD será tratada como inválida");
                return null;
            }
        }

        private async Task TryPersistAsync(string ticket, AiCandidateStatus status)
        {
            try
            {
                var fileName = SanitizeTicketForFileName(ticket);
                var path = Path.Combine(_storePath, $"{fileName}.json");
                var json = JsonSerializer.Serialize(status, JsonOptions);
                await File.WriteAllTextAsync(path, json);
            }
            catch (Exception ex)
            {
                // Persistência é acelerador/dataset, não fonte da verdade obrigatória — falha aqui
                // não pode derrubar o job (o resultado já está no cache em memória).
                _logger.LogWarning(ex, "Falha ao persistir status do pathway IA para ticket {Ticket}", ticket);
            }
        }

        private async Task<AiCandidateStatus?> TryReadAsync(string ticket, CancellationToken ct)
        {
            try
            {
                var fileName = SanitizeTicketForFileName(ticket);
                var path = Path.Combine(_storePath, $"{fileName}.json");
                if (!File.Exists(path))
                    return null;

                var json = await File.ReadAllTextAsync(path, ct);
                return JsonSerializer.Deserialize<AiCandidateStatus>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao ler status persistido do pathway IA para ticket {Ticket}", ticket);
                return null;
            }
        }

        // Ticket já é validado pelo formato "{sha256}.{layoutGuid}" (LowCodeTransformationStore) —
        // aqui só garantimos que vira nome de arquivo seguro mesmo se vier de outra origem.
        private static string SanitizeTicketForFileName(string ticket)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = ticket.Where(c => !invalid.Contains(c)).ToArray();
            return new string(chars);
        }

        private static string SafeMessage(Exception ex) => ex.Message;
    }
}
