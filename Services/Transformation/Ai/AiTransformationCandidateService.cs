using System.Text;
using System.Text.Json;

using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Transformation;
using LayoutParserApi.Services.XmlAnalysis;
using LayoutParserApi.Services.XmlAnalysis.Models;

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
        private readonly IAiFallbackSuppressionGate _suppressionGate;
        private readonly CanonicalDiffer _differ = new();

        // ✅ XsdValidationService/XmlAnalysisService são Scoped (dotnet-standards.md). O job roda em
        // Task.Run fire-and-forget que sobrevive ao fim do scope da request HTTP — capturar
        // diretamente a instância injetada aqui seria usar um serviço Scoped fora do seu ciclo de
        // vida. Por isso recebemos IServiceScopeFactory e abrimos um scope novo dentro do loop
        // (RunLoopAsync/RunFallbackLoopAsync). O mesmo vale para IXslSynthesizerService (Scoped).
        public AiTransformationCandidateService(
            ILogger<AiTransformationCandidateService> logger,
            HttpClient httpClient,
            IOptions<OllamaOptions> ollamaOptions,
            IOptions<AiTransformationCandidateOptions> options,
            IServiceScopeFactory scopeFactory,
            AiCandidateStore store,
            IAiFallbackSuppressionGate suppressionGate)
        {
            _logger = logger;
            _httpClient = httpClient;
            _ollamaOptions = ollamaOptions.Value;
            _options = options.Value;
            _scopeFactory = scopeFactory;
            _store = store;
            _suppressionGate = suppressionGate;
        }

        public Task EnqueueAsync(
            string userId,
            string ticket,
            string layoutName,
            Guid layoutGuid,
            string mapperGuid,
            string inputContent,
            string? groundTruthXml,
            CancellationToken cancellationToken,
            IReadOnlyList<ParsedField>? parsedFields = null)
        {
            if (string.IsNullOrWhiteSpace(ticket))
                return Task.CompletedTask;

            // Sem gabarito: não é mais "não aplicável" por definição — é o fallback automático
            // (Estado A, docs/architecture/design-fallback-ia-automatico-2026-08-16.md). A decisão
            // de SE disparar (cooldown do gate, FailureKind do lado sysmiddle/tcl-xsl) já foi tomada
            // pelo chamador (TransformationExecutionController.TryEnqueueAiFallback) — aqui só
            // executamos o modo certo do loop.
            var hasGroundTruth = !string.IsNullOrWhiteSpace(groundTruthXml);

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
                    if (hasGroundTruth)
                        await RunLoopAsync(userId, ticket, layoutName, layoutGuid, mapperGuid, inputContent, groundTruthXml!, linkedCts.Token, parsedFields);
                    else
                        await RunFallbackLoopAsync(userId, ticket, layoutName, layoutGuid, mapperGuid, inputContent, linkedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning(
                        "Job do pathway IA excedeu o teto de sanidade de {SanityMinutes}min (ticket={Ticket}, layout={LayoutName})",
                        sanityMinutes, ticket, layoutName);
                    _store.Set(userId, ticket, new AiCandidateStatus
                    {
                        Status = AiCandidateStatus.StatusFailed,
                        Diagnostics = new AiCandidateDiagnostics { LastError = "Teto de sanidade excedido", HasGroundTruth = hasGroundTruth }
                    });
                    if (!hasGroundTruth)
                        _suppressionGate.RegisterFailure(layoutGuid, TimeSpan.FromMinutes(_options.CooldownMinutes));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Falha não tratada no job do pathway IA (ticket={Ticket}, layout={LayoutName})", ticket, layoutName);
                    _store.Set(userId, ticket, new AiCandidateStatus
                    {
                        Status = AiCandidateStatus.StatusFailed,
                        Diagnostics = new AiCandidateDiagnostics { LastError = "Falha interna no job de geração via IA", HasGroundTruth = hasGroundTruth }
                    });
                    if (!hasGroundTruth)
                        _suppressionGate.RegisterFailure(layoutGuid, TimeSpan.FromMinutes(_options.CooldownMinutes));
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
        /// Motor real: <see cref="IXslSynthesizerService"/> (RepairOrchestrator de
        /// <c>ai/XslSynth.Core</c>) sintetiza XSLT de verdade — gerar → validar (diff canônico +
        /// XSD) → corrigir, tudo dentro do orquestrador. Só se aplica quando <paramref
        /// name="inputContent"/> já é XML (o low-code intermediário) — RepairOrchestrator aplica
        /// XSLT sobre XML, não sobre TXT posicional cru. Nesse caso o candidato convergido/falho
        /// já vem pronto e <see cref="RunLoopAsync"/> não precisa do loop XML-direto antigo.
        /// Quando não se aplica (TXT cru, mapper sem MapeadorVO resolvível, etc.), degrada pro
        /// caminho legado (XML-direto via Ollama) — nunca derruba o job.
        /// </summary>
        private async Task<XslSynthesisResult?> TrySynthesizeXsltAsync(
            string layoutName, string mapperGuid, string inputContent, string groundTruthXml, int maxIterations,
            CancellationToken cancellationToken, IReadOnlyList<ParsedField>? parsedFields)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var synthesizer = scope.ServiceProvider.GetRequiredService<IXslSynthesizerService>();
                var result = await synthesizer.SynthesizeAsync(mapperGuid, inputContent, groundTruthXml, maxIterations, layoutName, cancellationToken, parsedFields);
                return result.Success ? result : null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "IXslSynthesizerService indisponível/falhou — degradando para o loop XML-direto legado (mapperGuid={MapperGuid})", mapperGuid);
                return null;
            }
        }

        /// <summary>
        /// Loop gerar → validar XSD → diff canônico (simplificado — comparação estrutural, não
        /// node-a-node como <c>CanonicalDiffer</c> de <c>ai/XslSynth</c>) → corrigir.
        /// </summary>
        private async Task RunLoopAsync(
            string userId, string ticket, string layoutName, Guid layoutGuid, string mapperGuid,
            string inputContent, string groundTruthXml, CancellationToken cancellationToken,
            IReadOnlyList<ParsedField>? parsedFields = null)
        {
            var maxIterations = _options.MaxIterations > 0 ? _options.MaxIterations : 3;

            // ── Motor novo primeiro: RepairOrchestrator sintetiza XSLT real, não XML direto ──
            var synthesis = await TrySynthesizeXsltAsync(layoutName, mapperGuid, inputContent, groundTruthXml, maxIterations, cancellationToken, parsedFields);
            if (synthesis is not null)
            {
                if (synthesis.Converged && !string.IsNullOrWhiteSpace(synthesis.FinalOutputXml))
                {
                    _store.Set(userId, ticket, new AiCandidateStatus
                    {
                        Status = AiCandidateStatus.StatusConverged,
                        Candidate = new TransformationCandidate
                        {
                            CandidateId = $"ia-{mapperGuid}",
                            Pathway = "ia",
                            TransformedXml = synthesis.FinalOutputXml,
                            GeneratedXslt = synthesis.GeneratedXslt
                        },
                        Diagnostics = new AiCandidateDiagnostics
                        {
                            Iterations = synthesis.IterationsUsed,
                            RemainingDiffs = 0,
                            XsdValid = synthesis.XsdValid
                        }
                    });
                }
                else
                {
                    _store.Set(userId, ticket, new AiCandidateStatus
                    {
                        Status = AiCandidateStatus.StatusFailed,
                        Diagnostics = new AiCandidateDiagnostics
                        {
                            Iterations = synthesis.IterationsUsed,
                            RemainingDiffs = synthesis.ValidationErrors.Count,
                            XsdValid = synthesis.XsdValid,
                            LastError = synthesis.Error ?? $"RepairOrchestrator não convergiu em {synthesis.IterationsUsed} iteração(ões)"
                        }
                    });
                }
                return;
            }

            // ── Fallback legado: XML-direto via Ollama (sem síntese de XSLT reutilizável) ──
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

        /// <summary>
        /// Loop do fallback automático (Estado A — sem gabarito sysmiddle,
        /// docs/architecture/design-fallback-ia-automatico-2026-08-16.md §6): gerar → validar
        /// XSD → validar regra de negócio (<see cref="XmlAnalysisService"/>, reaproveitado — não
        /// inventa um terceiro validador) → corrigir. Sem diff canônico (não há gabarito): o loop
        /// convergir significa "estruturalmente e semanticamente plausível", não "idêntico ao que a
        /// Sysmiddle geraria". Por isso o candidato resultante sai marcado
        /// <see cref="AiCandidateDiagnostics.HasGroundTruth"/> == false, com
        /// <see cref="AiTransformationCandidateOptions.MaxIterationsFallback"/> (2, mais
        /// conservador que o modo com gabarito) — iterações extras sem gabarito não aumentam a
        /// confiança do resultado, só o custo de Ollama.
        /// </summary>
        private async Task RunFallbackLoopAsync(
            string userId, string ticket, string layoutName, Guid layoutGuid, string mapperGuid,
            string inputContent, CancellationToken cancellationToken)
        {
            var maxIterations = _options.MaxIterationsFallback > 0 ? _options.MaxIterationsFallback : 2;
            string? lastCandidateXml = null;
            string? lastError = null;

            for (var iteration = 1; iteration <= maxIterations; iteration++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string candidateXml;
                try
                {
                    candidateXml = await GenerateFallbackCandidateAsync(
                        layoutName, mapperGuid, inputContent, lastCandidateXml, lastError, cancellationToken);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    _logger.LogWarning(ex, "Ollama indisponível/timeout no fallback IA (ticket={Ticket}, iteração={Iteration})", ticket, iteration);
                    _store.Set(userId, ticket, new AiCandidateStatus
                    {
                        Status = AiCandidateStatus.StatusFailed,
                        Diagnostics = new AiCandidateDiagnostics
                        {
                            Iterations = iteration - 1,
                            LastError = "Ollama indisponível ou excedeu o tempo limite",
                            HasGroundTruth = false
                        }
                    });
                    _suppressionGate.RegisterFailure(layoutGuid, TimeSpan.FromMinutes(_options.CooldownMinutes));
                    return;
                }

                if (string.IsNullOrWhiteSpace(candidateXml))
                {
                    lastError = "Modelo não retornou XML válido";
                    lastCandidateXml = null;
                    continue;
                }

                lastCandidateXml = candidateXml;

                var xsdValid = await TryValidateXsdAsync(candidateXml, cancellationToken);
                var businessValidation = await TryValidateBusinessRulesAsync(candidateXml, cancellationToken);

                if (xsdValid && businessValidation.Success)
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
                            XsdValid = true,
                            HasGroundTruth = false
                        }
                    });
                    // Convergiu sem gabarito: se um dia o layout tiver mapper cadastrado, a próxima
                    // tentativa de fallback não deve ficar presa num cooldown de uma falha antiga.
                    _suppressionGate.ClearCooldown(layoutGuid);
                    return;
                }

                // Realimenta o motivo concreto (XSD inválido e/ou quais regras de negócio falharam)
                // no prompt de correção — mesmo espírito do diff canônico no modo com gabarito.
                lastError = FormatFallbackErrorsForPrompt(xsdValid, businessValidation);

                if (iteration == maxIterations)
                {
                    _store.Set(userId, ticket, new AiCandidateStatus
                    {
                        Status = AiCandidateStatus.StatusFailed,
                        Diagnostics = new AiCandidateDiagnostics
                        {
                            Iterations = iteration,
                            RemainingDiffs = 0,
                            XsdValid = xsdValid,
                            HasGroundTruth = false,
                            LastError = $"Não convergiu em {maxIterations} iteração(ões) (fallback sem gabarito): {lastError}"
                        }
                    });
                    _suppressionGate.RegisterFailure(layoutGuid, TimeSpan.FromMinutes(_options.CooldownMinutes));
                }
            }
        }

        private async Task<string?> GenerateFallbackCandidateAsync(
            string layoutName, string mapperGuid, string inputContent,
            string? previousCandidateXml, string? previousError, CancellationToken cancellationToken)
        {
            var prompt = BuildFallbackPrompt(layoutName, mapperGuid, inputContent, previousCandidateXml, previousError);

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
                _logger.LogWarning("Ollama respondeu {StatusCode} ao gerar candidato do fallback IA: {Body}", response.StatusCode, body);
                return null;
            }

            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(raw);
            var modelText = doc.RootElement.TryGetProperty("response", out var r) ? r.GetString() ?? "" : "";

            return ExtractXml(modelText);
        }

        /// <summary>
        /// Prompt do fallback automático: sem gabarito sysmiddle para copiar a estrutura, o modelo
        /// precisa gerar o XML final a partir só do conhecimento de domínio (NFe/CTe SEFAZ) e do
        /// documento de entrada — por isso é mais explícito sobre o schema-alvo do que
        /// <see cref="BuildPrompt"/> (modo com gabarito).
        /// </summary>
        private static string BuildFallbackPrompt(
            string layoutName, string mapperGuid, string inputContent,
            string? previousCandidateXml, string? previousError)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Você é um especialista em transformação de documentos fiscais (NFe/CTe) do");
            sb.AppendLine("ecossistema Sysmiddle. NÃO existe um gabarito de referência para este layout —");
            sb.AppendLine("ele ainda não tem mapeador cadastrado. Gere o XML final mais plausível a partir");
            sb.AppendLine("do documento de entrada, seguindo a estrutura padrão SEFAZ (NFe/CTe, conforme o");
            sb.AppendLine("conteúdo indicar) e as convenções usuais do ecossistema Sysmiddle. Responda");
            sb.AppendLine("SOMENTE com o XML final, sem markdown, sem explicações.");
            sb.AppendLine();
            sb.AppendLine($"LAYOUT: {layoutName}");
            sb.AppendLine($"MAPEADOR (referência, sem regras cadastradas): {mapperGuid}");
            sb.AppendLine();
            sb.AppendLine("ENTRADA (documento original):");
            sb.AppendLine(Truncate(inputContent, 4000));

            if (!string.IsNullOrWhiteSpace(previousCandidateXml))
            {
                sb.AppendLine();
                sb.AppendLine("SUA TENTATIVA ANTERIOR (ainda com problema estrutural/de negócio):");
                sb.AppendLine(Truncate(previousCandidateXml, 4000));
            }

            if (!string.IsNullOrWhiteSpace(previousError))
            {
                sb.AppendLine();
                sb.AppendLine($"MOTIVO DA REJEIÇÃO: {previousError}");
                sb.AppendLine("Corrija a tentativa anterior para eliminar esse problema.");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Validação de regra de negócio do fallback (§6 do desenho) — reaproveita
        /// <see cref="XmlAnalysisService"/> (o mesmo validador de negócio já usado no pipeline de
        /// análise de XML), não inventa um terceiro verificador. Sem <c>Layout</c> resolvido aqui
        /// (o fallback roda antes de qualquer mapper existir para o layout), a validação de
        /// estrutura contra layout é pulada — só regras de negócio genéricas (<c>ValidateBusinessRules</c>
        /// internamente) se aplicam.
        /// </summary>
        private async Task<XmlAnalysisResult> TryValidateBusinessRulesAsync(string candidateXml, CancellationToken cancellationToken)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var xmlAnalysis = scope.ServiceProvider.GetRequiredService<XmlAnalysisService>();
                return await xmlAnalysis.AnalyzeXmlAsync(candidateXml, layout: null);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Falha ao validar regra de negócio do candidato do fallback IA — tratado como inválido");
                return new XmlAnalysisResult { Success = false, Errors = { "Falha interna ao validar regra de negócio" } };
            }
        }

        private static string FormatFallbackErrorsForPrompt(bool xsdValid, XmlAnalysisResult businessValidation)
        {
            const int maxErrorsInPrompt = 20;
            var parts = new List<string>();

            if (!xsdValid)
                parts.Add("XML não passou na validação estrutural contra o schema SEFAZ (XSD)");

            if (businessValidation.Errors.Count > 0)
            {
                var shown = businessValidation.Errors.Take(maxErrorsInPrompt);
                var suffix = businessValidation.Errors.Count > maxErrorsInPrompt
                    ? $" (+{businessValidation.Errors.Count - maxErrorsInPrompt} outro(s))"
                    : "";
                parts.Add($"Regra(s) de negócio violada(s): {string.Join("; ", shown)}{suffix}");
            }

            return parts.Count > 0 ? string.Join(" | ", parts) : "Falha de validação não especificada";
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
