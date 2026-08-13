using System.Text;
using System.Text.Json;

using LayoutParserApi.Models.Transformation;
using LayoutParserApi.Services.XmlAnalysis;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Services.Transformation.Ai
{
    /// <summary>
    /// Implementação do pathway IA de <c>execute-candidates</c> (Issue #40). Loop
    /// gerar → validar XSD → comparar com o gabarito sysmiddle → corrigir, usando o Ollama local
    /// (nunca nuvem — dado fiscal sensível, ver <c>security.md</c>).
    ///
    /// <para><b>Divergência documentada do desenho (§4.1):</b> o desenho recomenda extrair
    /// <c>Core/</c>+<c>Synthesis/</c> de <c>ai/XslSynth</c> (RepairOrchestrator, CanonicalDiffer,
    /// XsdValidator, OllamaXslSynthesizer) para uma classlib compartilhada (opção "a"). Esta
    /// implementação segue a opção "b" ("reimplementar um subconjunto fino dentro da API") pelo
    /// prazo desta issue — o desenho já sinaliza essa opção como válida, com o trade-off de duas
    /// implementações do mesmo loop divergirem no tempo. <c>ai/XslSynth</c> continua intocado como
    /// bancada offline. Extrair a classlib compartilhada fica como próximo passo (ver memória de
    /// @lp-parser-llm).</para>
    /// </summary>
    public class AiTransformationCandidateService : IAiTransformationCandidateService
    {
        private readonly ILogger<AiTransformationCandidateService> _logger;
        private readonly HttpClient _httpClient;
        private readonly OllamaOptions _ollamaOptions;
        private readonly AiTransformationCandidateOptions _options;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly AiCandidateStore _store;

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
                _store.Set(ticket, new AiCandidateStatus { Status = AiCandidateStatus.StatusNotApplicable });
                return Task.CompletedTask;
            }

            _store.Set(ticket, new AiCandidateStatus { Status = AiCandidateStatus.StatusRunning });

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
                    await RunLoopAsync(ticket, layoutName, layoutGuid, mapperGuid, inputContent, groundTruthXml, linkedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning(
                        "Job do pathway IA excedeu o teto de sanidade de {SanityMinutes}min (ticket={Ticket}, layout={LayoutName})",
                        sanityMinutes, ticket, layoutName);
                    _store.Set(ticket, new AiCandidateStatus
                    {
                        Status = AiCandidateStatus.StatusFailed,
                        Diagnostics = new AiCandidateDiagnostics { LastError = "Teto de sanidade excedido" }
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Falha não tratada no job do pathway IA (ticket={Ticket}, layout={LayoutName})", ticket, layoutName);
                    _store.Set(ticket, new AiCandidateStatus
                    {
                        Status = AiCandidateStatus.StatusFailed,
                        Diagnostics = new AiCandidateDiagnostics { LastError = "Falha interna no job de geração via IA" }
                    });
                }
            }, CancellationToken.None); // O próprio Task.Run não deve morrer com a request HTTP.

            return Task.CompletedTask;
        }

        public Task<AiCandidateStatus> GetStatusAsync(string ticket, CancellationToken cancellationToken)
        {
            var status = _store.Get(ticket) ?? new AiCandidateStatus { Status = AiCandidateStatus.StatusNotFound };
            return Task.FromResult(status);
        }

        /// <summary>
        /// Loop gerar → validar XSD → diff canônico (simplificado — comparação estrutural, não
        /// node-a-node como <c>CanonicalDiffer</c> de <c>ai/XslSynth</c>) → corrigir.
        /// </summary>
        private async Task RunLoopAsync(
            string ticket, string layoutName, Guid layoutGuid, string mapperGuid,
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
                    _store.Set(ticket, new AiCandidateStatus
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

                var diffCount = CanonicalDiffCount(candidateXml, groundTruthXml);
                var xsdValid = await TryValidateXsdAsync(candidateXml, cancellationToken);

                if (diffCount < lastDiffCount)
                    lastDiffCount = diffCount;

                if (diffCount == 0)
                {
                    _store.Set(ticket, new AiCandidateStatus
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

                lastError = $"Diff estrutural contra o gabarito sysmiddle: {diffCount} divergência(s) restante(s)";

                // Última iteração: registra "failed" com o melhor candidato encontrado nos diagnostics
                // (o candidato em si não vaza para candidates[]/recommendedCandidateId — §2.2 do desenho).
                if (iteration == maxIterations)
                {
                    _store.Set(ticket, new AiCandidateStatus
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
        /// Diff canônico simplificado: conta divergências de nome de elemento/valor de texto entre o
        /// candidato e o gabarito, ignorando diferenças de formatação (indentação, quebras de linha).
        /// Não é o diff node-a-node completo do <c>CanonicalDiffer</c> de <c>ai/XslSynth</c> — ver
        /// divergência documentada no cabeçalho desta classe.
        /// </summary>
        private int CanonicalDiffCount(string candidateXml, string groundTruthXml)
        {
            try
            {
                var candidateDoc = System.Xml.Linq.XDocument.Parse(candidateXml);
                var groundTruthDoc = System.Xml.Linq.XDocument.Parse(groundTruthXml);

                var candidateElements = candidateDoc.Descendants()
                    .Select(e => (e.Name.LocalName, Value: (e.Value ?? "").Trim()))
                    .ToList();
                var groundTruthElements = groundTruthDoc.Descendants()
                    .Select(e => (e.Name.LocalName, Value: (e.Value ?? "").Trim()))
                    .ToList();

                if (candidateElements.Count != groundTruthElements.Count)
                    return Math.Abs(candidateElements.Count - groundTruthElements.Count) + 1;

                var diffs = 0;
                for (var i = 0; i < candidateElements.Count; i++)
                {
                    if (candidateElements[i].LocalName != groundTruthElements[i].LocalName
                        || candidateElements[i].Value != groundTruthElements[i].Value)
                        diffs++;
                }

                return diffs;
            }
            catch (Exception ex)
            {
                // XML inválido do candidato conta como divergência total — não trava o loop.
                _logger.LogDebug(ex, "Candidato IA não é XML bem-formado — tratado como divergência total");
                return int.MaxValue;
            }
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
