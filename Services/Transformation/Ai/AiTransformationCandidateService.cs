using System.Text;
using System.Text.Json;

using LayoutParserApi.Models.Transformation;
using LayoutParserApi.Services.XmlAnalysis;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using XslSynth.Core;

namespace LayoutParserApi.Services.Transformation.Ai
{
    /// <summary>
    /// Implementação do pathway IA de <c>execute-candidates</c> (Issue #40). Loop
    /// gerar → validar XSD → comparar com o gabarito sysmiddle → corrigir, usando o Ollama local
    /// (nunca nuvem — dado fiscal sensível, ver <c>security.md</c>).
    ///
    /// <para><b>Follow-up da divergência do desenho (§4.1):</b> a extração da classlib compartilhada
    /// <c>ai/XslSynth.Core</c> (opção "a" do desenho) foi concluída — <c>ai/XslSynth</c> (CLI) e este
    /// serviço agora referenciam o mesmo assembly. Este serviço ainda NÃO usa o
    /// <see cref="RepairOrchestrator"/> completo: aquele orquestrador parte de um <c>MapperVo</c>
    /// (regras/LinkMappings do mapeador) e sintetiza/repara XSLT — não se aplica aqui, onde o LLM
    /// gera o XML final diretamente a partir do TXT de entrada + gabarito, sem transpilar XSLT.
    /// O que É compartilhado é o diff canônico node-a-node real (<see cref="CanonicalDiffer"/>),
    /// substituindo a comparação estrutural simplificada que existia aqui antes — mesmo verificador
    /// determinístico que o <c>RepairOrchestrator</c> usa no passo 5/6 do loop, e os XPaths exatos
    /// retornados alimentam o prompt de correção do mesmo jeito que o passo 6
    /// (<c>RepairFromDiffAsync</c>) faz no CLI.</para>
    /// </summary>
    public class AiTransformationCandidateService : IAiTransformationCandidateService
    {
        private readonly ILogger<AiTransformationCandidateService> _logger;
        private readonly HttpClient _httpClient;
        private readonly OllamaOptions _ollamaOptions;
        private readonly AiTransformationCandidateOptions _options;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly AiCandidateStore _store;
        private readonly CanonicalDiffer _differ = new();

        // ✅ XsdValidationService é Scoped (dotnet-standards.md). O job roda em Task.Run
        // fire-and-forget que sobrevive ao fim do scope da request HTTP — capturar diretamente a
        // instância injetada aqui seria usar um serviço Scoped fora do seu ciclo de vida. Por isso
        // recebemos IServiceScopeFactory e abrimos um scope novo dentro do loop (RunLoopAsync).
        public AiTransformationCandidateService(
            ILogger<AiTransformationCandidateService> logger,
            HttpClient httpClient,
            IOptions<OllamaOptions> ollamaOptions,
            IOptions<AiTransformationCandidateOptions> options,
            IServiceScopeFactory scopeFactory,
            AiCandidateStore store)
        {
            _logger = logger;
            _httpClient = httpClient;
            _ollamaOptions = ollamaOptions.Value;
            _options = options.Value;
            _scopeFactory = scopeFactory;
            _store = store;
        }

        public Task EnqueueAsync(
            string userId,
            string ticket,
            string layoutName,
            Guid layoutGuid,
            string mapperGuid,
            string inputContent,
            string groundTruthXml,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(ticket))
                return Task.CompletedTask;

            if (string.IsNullOrWhiteSpace(groundTruthXml))
            {
                // Reforça 2.1 do desenho: sem gabarito sysmiddle, o pathway IA não é aplicável.
                _store.Set(userId, ticket, new AiCandidateStatus { Status = AiCandidateStatus.StatusNotApplicable });
                return Task.CompletedTask;
            }

            _store.Set(userId, ticket, new AiCandidateStatus { Status = AiCandidateStatus.StatusRunning });

            // ✅ Fire-and-forget real: NUNCA propaga exceção para o chamador (dotnet-standards.md
            // §Background work). O teto de sanidade (não é SLA de produto — §2.3/§6 do desenho)
            // evita job "running" para sempre se o Ollama travar/morrer no meio do loop.
            _ = Task.Run(async () =>
            {
                var sanityMinutes = _options.SanityTimeoutMinutes > 0 ? _options.SanityTimeoutMinutes : 45;
                using var sanityCts = new CancellationTokenSource(TimeSpan.FromMinutes(sanityMinutes));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, sanityCts.Token);

                try
                {
                    await RunLoopAsync(userId, ticket, layoutName, layoutGuid, mapperGuid, inputContent, groundTruthXml, linkedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning(
                        "Job do pathway IA excedeu o teto de sanidade de {SanityMinutes}min (ticket={Ticket}, layout={LayoutName})",
                        sanityMinutes, ticket, layoutName);
                    _store.Set(userId, ticket, new AiCandidateStatus
                    {
                        Status = AiCandidateStatus.StatusFailed,
                        Diagnostics = new AiCandidateDiagnostics { LastError = "Teto de sanidade excedido" }
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Falha não tratada no job do pathway IA (ticket={Ticket}, layout={LayoutName})", ticket, layoutName);
                    _store.Set(userId, ticket, new AiCandidateStatus
                    {
                        Status = AiCandidateStatus.StatusFailed,
                        Diagnostics = new AiCandidateDiagnostics { LastError = "Falha interna no job de geração via IA" }
                    });
                }
            }, CancellationToken.None); // O próprio Task.Run não deve morrer com a request HTTP.

            return Task.CompletedTask;
        }

        public Task<AiCandidateStatus> GetStatusAsync(string userId, string ticket, CancellationToken cancellationToken)
        {
            var status = _store.Get(userId, ticket) ?? new AiCandidateStatus { Status = AiCandidateStatus.StatusNotFound };
            return Task.FromResult(status);
        }

        /// <summary>
        /// Loop gerar → validar XSD → diff canônico (simplificado — comparação estrutural, não
        /// node-a-node como <c>CanonicalDiffer</c> de <c>ai/XslSynth</c>) → corrigir.
        /// </summary>
        private async Task RunLoopAsync(
            string userId, string ticket, string layoutName, Guid layoutGuid, string mapperGuid,
            string inputContent, string groundTruthXml, CancellationToken cancellationToken)
        {
            var maxIterations = _options.MaxIterations > 0 ? _options.MaxIterations : 3;
            string? lastCandidateXml = null;
            string? lastError = null;
            var lastDiffCount = int.MaxValue;

            for (var iteration = 1; iteration <= maxIterations; iteration++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string candidateXml;
                try
                {
                    candidateXml = await GenerateCandidateAsync(
                        layoutName, mapperGuid, inputContent, groundTruthXml, lastCandidateXml, lastError, cancellationToken);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    _logger.LogWarning(ex, "Ollama indisponível/timeout no pathway IA (ticket={Ticket}, iteração={Iteration})", ticket, iteration);
                    _store.Set(userId, ticket, new AiCandidateStatus
                    {
                        Status = AiCandidateStatus.StatusFailed,
                        Diagnostics = new AiCandidateDiagnostics { Iterations = iteration - 1, LastError = "Ollama indisponível ou excedeu o tempo limite" }
                    });
                    return;
                }

                if (string.IsNullOrWhiteSpace(candidateXml))
                {
                    lastError = "Modelo não retornou XML válido";
                    lastCandidateXml = null;
                    continue;
                }

                lastCandidateXml = candidateXml;

                var diffs = CanonicalDiff(candidateXml, groundTruthXml);
                var diffCount = diffs.Count;
                var xsdValid = await TryValidateXsdAsync(candidateXml, cancellationToken);

                if (diffCount < lastDiffCount)
                    lastDiffCount = diffCount;

                if (diffCount == 0)
                {
                    _store.Set(userId, ticket, new AiCandidateStatus
                    {
                        Status = AiCandidateStatus.StatusConverged,
                        Candidate = new TransformationCandidate
                        {
                            CandidateId = $"ia-{mapperGuid}",
                            Pathway = "ia",
                            TransformedXml = candidateXml
                        },
                        Diagnostics = new AiCandidateDiagnostics
                        {
                            Iterations = iteration,
                            RemainingDiffs = 0,
                            XsdValid = xsdValid
                        }
                    });
                    return;
                }

                // Mesmo formato do passo 6 do RepairOrchestrator (RepairFromDiffAsync): o LLM recebe
                // o XPath exato de cada divergência, não só a contagem — é isso que faz o loop
                // convergir em vez de tentar às cegas.
                lastError = FormatDiffsForPrompt(diffs);

                // Última iteração: registra "failed" com o melhor candidato encontrado nos diagnostics
                // (o candidato em si não vaza para candidates[]/recommendedCandidateId — §2.2 do desenho).
                if (iteration == maxIterations)
                {
                    _store.Set(userId, ticket, new AiCandidateStatus
                    {
                        Status = AiCandidateStatus.StatusFailed,
                        Diagnostics = new AiCandidateDiagnostics
                        {
                            Iterations = iteration,
                            RemainingDiffs = diffCount,
                            XsdValid = xsdValid,
                            LastError = $"Não convergiu em {maxIterations} iteração(ões): {lastError}"
                        }
                    });
                }
            }
        }

        private async Task<string?> GenerateCandidateAsync(
            string layoutName, string mapperGuid, string inputContent, string groundTruthXml,
            string? previousCandidateXml, string? previousError, CancellationToken cancellationToken)
        {
            var prompt = BuildPrompt(layoutName, mapperGuid, inputContent, groundTruthXml, previousCandidateXml, previousError);

            var payload = new
            {
                model = _ollamaOptions.Model,
                prompt,
                stream = false,
                options = new { temperature = 0.0 }
            };

            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync($"{_ollamaOptions.Url.TrimEnd('/')}/api/generate", content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(response);
                _logger.LogWarning("Ollama respondeu {StatusCode} ao gerar candidato IA: {Body}", response.StatusCode, body);
                return null;
            }

            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(raw);
            var modelText = doc.RootElement.TryGetProperty("response", out var r) ? r.GetString() ?? "" : "";

            return ExtractXml(modelText);
        }

        private static string BuildPrompt(
            string layoutName, string mapperGuid, string inputContent, string groundTruthXml,
            string? previousCandidateXml, string? previousError)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Você é um especialista em transformação de documentos fiscais (NFe/CTe) do");
            sb.AppendLine("ecossistema Sysmiddle. Gere o XML final para o layout/mapeador abaixo, seguindo");
            sb.AppendLine("EXATAMENTE a estrutura e as regras de transformação usadas pelo pathway sysmiddle");
            sb.AppendLine("(gabarito). Responda SOMENTE com o XML final, sem markdown, sem explicações.");
            sb.AppendLine();
            sb.AppendLine($"LAYOUT: {layoutName}");
            sb.AppendLine($"MAPEADOR: {mapperGuid}");
            sb.AppendLine();
            sb.AppendLine("ENTRADA (documento original):");
            sb.AppendLine(Truncate(inputContent, 4000));
            sb.AppendLine();
            sb.AppendLine("GABARITO (saída correta do pathway sysmiddle para este layout+mapeador):");
            sb.AppendLine(Truncate(groundTruthXml, 6000));

            if (!string.IsNullOrWhiteSpace(previousCandidateXml))
            {
                sb.AppendLine();
                sb.AppendLine("SUA TENTATIVA ANTERIOR (ainda divergente do gabarito):");
                sb.AppendLine(Truncate(previousCandidateXml, 4000));
            }

            if (!string.IsNullOrWhiteSpace(previousError))
            {
                sb.AppendLine();
                sb.AppendLine($"MOTIVO DA DIVERGÊNCIA: {previousError}");
                sb.AppendLine("Corrija a tentativa anterior para eliminar essa divergência.");
            }

            return sb.ToString();
        }

        private static string Truncate(string value, int maxLength)
            => value.Length <= maxLength ? value : value.Substring(0, maxLength);

        /// <summary>Extrai o primeiro bloco XML plausível da resposta do modelo (tolera cerca de markdown).</summary>
        private static string? ExtractXml(string modelText)
        {
            if (string.IsNullOrWhiteSpace(modelText))
                return null;

            var text = modelText.Trim();
            text = text.Replace("```xml", "").Replace("```", "").Trim();

            var start = text.IndexOf('<');
            var end = text.LastIndexOf('>');
            if (start < 0 || end < 0 || end <= start)
                return null;

            return text.Substring(start, end - start + 1);
        }

        /// <summary>
        /// Diff canônico node-a-node real, via <see cref="CanonicalDiffer"/> da classlib
        /// compartilhada <c>ai/XslSynth.Core</c> (mesmo verificador determinístico do
        /// <c>RepairOrchestrator</c>) — normaliza espaço/atributos/namespace e reporta o XPath
        /// exato de cada divergência (nome, texto, atributo, falta, sobra).
        /// </summary>
        private IReadOnlyList<NodeDiff> CanonicalDiff(string candidateXml, string groundTruthXml)
        {
            try
            {
                return _differ.Diff(groundTruthXml, candidateXml);
            }
            catch (Exception ex)
            {
                // XML inválido do candidato conta como divergência total — não trava o loop.
                _logger.LogDebug(ex, "Candidato IA não é XML bem-formado — tratado como divergência total");
                return new[] { new NodeDiff("invalid", "/", null, "XML malformado") };
            }
        }

        /// <summary>Formata os diffs canônicos (até um teto) para realimentar o prompt de correção,
        /// no mesmo espírito do passo 6 (RepairFromDiffAsync) do RepairOrchestrator do CLI.</summary>
        private static string FormatDiffsForPrompt(IReadOnlyList<NodeDiff> diffs)
        {
            const int maxDiffsInPrompt = 20;
            var shown = diffs.Take(maxDiffsInPrompt).Select(d => d.ToString());
            var suffix = diffs.Count > maxDiffsInPrompt ? $" (+{diffs.Count - maxDiffsInPrompt} outra(s))" : "";
            return $"Diff canônico contra o gabarito sysmiddle ({diffs.Count} divergência(s)):\n"
                + string.Join('\n', shown) + suffix;
        }

        private async Task<bool> TryValidateXsdAsync(string candidateXml, CancellationToken cancellationToken)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var xsdValidator = scope.ServiceProvider.GetRequiredService<XsdValidationService>();
                var result = await xsdValidator.ValidateXmlAgainstXsdAsync(candidateXml);
                return result.IsValid;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Falha ao validar XSD do candidato IA — tratado como inválido");
                return false;
            }
        }

        private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response)
        {
            try
            {
                return await response.Content.ReadAsStringAsync();
            }
            catch
            {
                return "";
            }
        }
    }
}
