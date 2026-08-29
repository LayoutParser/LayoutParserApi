using System.Collections.Concurrent;
using System.Xml.Linq;

using LayoutParserApi.Services.Transformation;
using LayoutParserApi.Services.XmlAnalysis;
using LayoutParserApi.Models;
using LayoutParserApi.Services.Transformation.LowCode;
using LayoutParserApi.Services.Transformation.Ai;
using LayoutParserApi.Services.Transformation.StructuralResolution;
using LayoutParserApi.Services.Database;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using LayoutParserApi.Services.XmlAnalysis.Models;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Filters;
using LayoutParserApi.Models.Database;
using LayoutParserApi.Models.Transformation;
using LayoutParserApi.Models.Parsing;

using XslSynth.Core;

namespace LayoutParserApi.Controllers
{
    /// <summary>
    /// Pathway 2 de transformação - <b>canônico</b> (decisão de arquitetura, item 2.1 do
    /// dispatch de IA em docs/architecture/ai-roadmap-dispatch.md, 2026-07-21): é o pathway
    /// que o front-end de fato chama hoje. Novo trabalho de transformação (validação XSD,
    /// diagnóstico via Ollama, etc.) deve entrar aqui - ou na camada de serviço por trás
    /// dele (<see cref="TransformationPipelineService"/>/<see cref="TransformationValidatorService"/>),
    /// nunca no controller (ver item 2.2).
    /// Ver também <see cref="TransformationController"/> (Pathway 1 - legado).
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [ServiceFilter(typeof(AuditActionFilter))]
    public class TransformationExecutionController : ControllerBase
    {
        private readonly ILogger<TransformationExecutionController> _logger;
        private readonly TransformationPipelineService _pipelineService;
        private readonly TransformationValidatorService _validatorService;
        private readonly TransformationLearningService _learningService;
        private readonly AutoTransformationGeneratorService _autoGenerator;
        private readonly LowCodeTransformationService _lowCode;
        private readonly LowCodeAutoTransformationService _lowCodeAuto;
        private readonly ILayoutDatabaseService _layoutDb;
        private readonly LowCodeRunnerOptions _lowCodeOpt;
        private readonly IAiTransformationCandidateService _aiCandidateService;
        private readonly IAiFallbackSuppressionGate _aiFallbackGate;
        private readonly ICurrentUser _currentUser;
        private readonly MapperDatabaseService _mapperDb;
        private readonly ILayoutParserService _layoutParser;
        private readonly FieldMappingCompositionService _fieldMappingComposition;

        public TransformationExecutionController(
            ILogger<TransformationExecutionController> logger,
            TransformationPipelineService pipelineService,
            TransformationValidatorService validatorService,
            TransformationLearningService learningService,
            AutoTransformationGeneratorService autoGenerator,
            LowCodeTransformationService lowCode,
            LowCodeAutoTransformationService lowCodeAuto,
            ILayoutDatabaseService layoutDb,
            IOptions<LowCodeRunnerOptions> lowCodeOptions,
            IAiTransformationCandidateService aiCandidateService,
            IAiFallbackSuppressionGate aiFallbackGate,
            ICurrentUser currentUser,
            MapperDatabaseService mapperDb,
            ILayoutParserService layoutParser,
            FieldMappingCompositionService fieldMappingComposition)
        {
            _logger = logger;
            _pipelineService = pipelineService;
            _validatorService = validatorService;
            _learningService = learningService;
            _autoGenerator = autoGenerator;
            _lowCode = lowCode;
            _lowCodeAuto = lowCodeAuto;
            _layoutDb = layoutDb;
            _lowCodeOpt = lowCodeOptions.Value;
            _aiCandidateService = aiCandidateService;
            _aiFallbackGate = aiFallbackGate;
            _currentUser = currentUser;
            _mapperDb = mapperDb;
            _layoutParser = layoutParser;
            _fieldMappingComposition = fieldMappingComposition;
        }

        // Issue #92: chave de particionamento da AiCandidateStore. ICurrentUser.Name é null quando
        // anônimo (sem [Authorize] em algum endpoint futuro ou identidade não confiável) — a store já
        // trata esse caso como um bucket fixo próprio, nunca cai no de outro usuário real.
        private string CurrentUserId => _currentUser.Name ?? string.Empty;

        /// <summary>
        /// Executa transformação completa (TXT -> XML ou XML -> XML)
        /// </summary>
        [HttpPost("execute")]
        public async Task<IActionResult> ExecuteTransformation([FromBody] TransformationRequest request)
        {
            try
            {
                _logger.LogInformation("Executando transformação para layout: {LayoutName}", request.LayoutName);

                if (string.IsNullOrEmpty(request.InputContent))
                {
                    return BadRequest(new { error = "InputContent é obrigatório" });
                }

                if (string.IsNullOrEmpty(request.LayoutName))
                {
                    return BadRequest(new { error = "LayoutName é obrigatório" });
                }

                // Detectar tipo de entrada
                var isXmlInput = request.InputContent.TrimStart().StartsWith("<");

                TransformationPipelineResult result;

                if (isXmlInput)
                {
                    // Transformação XML -> XML
                    result = await _pipelineService.TransformXmlToXmlAsync(
                        request.InputContent,
                        request.SourceDocumentType ?? "NFe",
                        request.TargetDocumentType ?? "NFe",
                        request.LayoutName);
                }
                else
                {
                    // Transformação TXT -> XML
                    result = await _pipelineService.TransformTxtToXmlAsync(
                        request.InputContent,
                        request.LayoutName,
                        request.TargetDocumentType ?? "NFe");
                }

                if (result.Success)
                {
                    // Validar transformação se solicitado
                    if (request.Validate)
                    {
                        var validationResult = await _validatorService.ValidateTransformationAsync(
                            isXmlInput ? null : request.InputContent,
                            request.LayoutName,
                            result.TclPath,
                            result.XslPath,
                            request.ExpectedOutput);

                        return Ok(new
                        {
                            success = true,
                            transformedXml = result.TransformedXml,
                            validation = validationResult,
                            segmentMappings = result.SegmentMappings
                        });
                    }

                    return Ok(new
                    {
                        success = true,
                        transformedXml = result.TransformedXml,
                        segmentMappings = result.SegmentMappings
                    });
                }
                else
                {
                    return BadRequest(new
                    {
                        success = false,
                        errors = result.Errors,
                        warnings = result.Warnings
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao executar transformação");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Executa transformação retornando TODOS os candidatos plausíveis dos dois pathways
        /// (sysmiddle/low-code e tcl-xsl/canônico) em vez de um resultado singular. Contrato completo
        /// (casos-limite de zero candidatos, falha parcial, timeout etc.) em
        /// docs/architecture/multi-candidato-e-diagnostico-ia-contrato.md (Gap 1).
        /// Quando nenhum pathway resolve (Estado A — não encontrado, distinto de Estado B de falha
        /// de infra), o fallback automático de IA é disparado em background (loop gerar→validar→
        /// corrigir via Ollama), sujeito a cooldown de 4h por LayoutGuid; ver
        /// <see cref="GetAiCandidateStatus"/> para acompanhar o resultado.
        ///
        /// <para>Issue LayoutParserReact #86 (diagnóstico estruturado): a resposta traz, de forma
        /// ADITIVA (não quebra clientes existentes), <see cref="TransformationExecutionCandidatesResponse.PathwayDiagnostics"/>
        /// — um <see cref="Models.Transformation.PathwayDiagnostic"/> por pathway avaliado (sysmiddle/tcl-xsl/
        /// ai-fallback), com <c>status</c>/<c>code</c>/<c>message</c> — e
        /// <see cref="TransformationExecutionCandidatesResponse.CorrelationId"/>, que permite ao suporte
        /// cruzar a resposta HTTP com o log estruturado completo (não sanitizado) desta chamada. Toda
        /// <c>Message</c> nesse array já passou por <see cref="Services.Transformation.LowCode.LowCodeErrorSanitizer"/>
        /// — nunca contém caminho físico de disco ou detalhe interno cru.</para>
        /// </summary>
        // Issue #32: dispara processos externos (runner x86) e é operação privilegiada — era
        // restrita ao papel "admin". Issue #93: reabre para qualquer usuário autenticado (o
        // isolamento por dono via CurrentUserId/AiCandidateStore, já feito na issue #92, é quem
        // impede um usuário ler/afetar o ticket de outro — não mais o papel).
        [Authorize]
        [HttpPost("execute-candidates")]
        public async Task<IActionResult> ExecuteTransformationCandidates([FromBody] TransformationRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.InputContent))
                return BadRequest(new { success = false, errors = new[] { "InputContent é obrigatório" }, warnings = Array.Empty<string>() });

            if (string.IsNullOrEmpty(request.LayoutName))
                return BadRequest(new { success = false, errors = new[] { "LayoutName é obrigatório" }, warnings = Array.Empty<string>() });

            _logger.LogInformation("Executando transformação multi-candidato para layout: {LayoutName}", request.LayoutName);

            // Resolver o layout no banco: serve tanto para validar existência (400 se não encontrado)
            // e oferecer fallback de LayoutGuid ao pathway sysmiddle. O LayoutGuid enviado no request,
            // quando válido, tem precedência porque o catálogo legado pode retornar Guid.Empty.
            // Exceção aqui = falha de infra que impede sequer listar candidatos → 500 (linha
            // "Falha total de infraestrutura"
            // da tabela de decisão do contrato).
            LayoutRecord? layoutRecord;
            try
            {
                var searchResponse = await _layoutDb.SearchLayoutsAsync(new LayoutSearchRequest { SearchTerm = request.LayoutName });
                if (!searchResponse.Success)
                    throw new InvalidOperationException(searchResponse.ErrorMessage);

                layoutRecord = searchResponse.Layouts
                    .FirstOrDefault(l => string.Equals(l.Name, request.LayoutName, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha de infraestrutura ao resolver layout {LayoutName} para multi-candidato", request.LayoutName);
                return StatusCode(500, new { success = false, error = "Falha de infraestrutura ao consultar o catálogo de layouts" });
            }

            if (layoutRecord == null)
                return BadRequest(new { success = false, errors = new[] { $"Layout '{request.LayoutName}' não encontrado" }, warnings = Array.Empty<string>() });

            var warnings = new List<string>();
            var isXmlInput = request.InputContent.TrimStart().StartsWith("<");

            // Timeout do CONJUNTO (decisão de design, não 100% especificada no contrato). São dois
            // limites diferentes e o código antes confundia os dois num número só:
            //
            //   (a) quanto o TRABALHO pode plausivelmente demorar. Os candidatos sysmiddle competem
            //       pelo mesmo semáforo do runner, então rodam em ondas de MaxConcurrentRunners; o
            //       pior caso é ceil(N / MaxConcurrentRunners) ondas de RunnerTimeoutSeconds. N é
            //       capado por MultiCandidateTopN, que é o teto real de candidatos disparados.
            //   (b) quanto o CLIENTE HTTP pode esperar — CandidatesRequestTimeoutSeconds.
            //
            // A fórmula anterior (RunnerTimeoutSeconds * MaxConcurrentRunners) errava as duas: com o
            // timeout do runner corrigido para 180s ela dava 360s de espera, e CRESCIA ao se aumentar
            // MaxConcurrentRunners — mais slots deveriam reduzir a fila, não aumentar o teto.
            var budget = LowCodeCandidatesBudget.Calculate(
                _lowCodeOpt.MultiCandidateTopN,
                _lowCodeOpt.MaxConcurrentRunners,
                _lowCodeOpt.RunnerTimeoutSeconds,
                _lowCodeOpt.CandidatesRequestTimeoutSeconds);
            var overallTimeoutSeconds = budget.EffectiveSeconds;

            // ✅ O teto CANCELA o trabalho, não só a espera. Sem isso, o 504 abaixo devolvia a resposta
            // e deixava até MaxConcurrentRunners processos x86 vivos segurando os slots do semáforo —
            // que é do PROCESSO INTEIRO da API (singleton), então travaria também os uploads de outros
            // usuários por até RunnerTimeoutSeconds. Mesmo defeito já corrigido no ParseController
            // (spec §1.1); este endpoint tinha ficado para trás, e o timeout de 180s o tornou caro.
            // Cancelar não perde trabalho: o pathway sysmiddle persiste em disco dentro da própria
            // chamada e o resultado fica consultável pelo ticket.
            using var candidatesCts = new CancellationTokenSource(TimeSpan.FromSeconds(overallTimeoutSeconds));

            // failureKinds: classificação interna (§2 do design-fallback-ia-automatico) coletada na
            // ORIGEM de cada pathway — nunca inferida depois por regex sobre warning já sanitizado.
            var failureKinds = new ConcurrentBag<FailureKind>();
            var pathwayDiagnostics = new ConcurrentBag<Models.Transformation.PathwayDiagnostic>();
            var sysmiddleTask = ExecuteSysmiddleCandidatesAsync(request, layoutRecord, isXmlInput, warnings, failureKinds, pathwayDiagnostics, candidatesCts.Token);
            var tclXslTask = ExecuteTclXslCandidatesAsync(request, isXmlInput, warnings, failureKinds, pathwayDiagnostics);

            var allTask = Task.WhenAll(sysmiddleTask, tclXslTask);
            var winner = await Task.WhenAny(allTask, Task.Delay(TimeSpan.FromSeconds(overallTimeoutSeconds)));

            // ⚠️ Vencer a corrida não basta: o cancelamento é cooperativo e faz a task terminar QUASE
            // no mesmo instante do Task.Delay, devolvendo os candidatos já marcados como falha. Sem
            // checar o token, o resultado sairia às vezes como 200 com tudo falhando — que mente pior
            // que o 504, porque diz "terminou e não deu certo" quando a verdade é "não deu tempo".
            if (winner != allTask || candidatesCts.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Timeout do conjunto de candidatos (>{TimeoutSeconds}s, budget de trabalho {BudgetTrabalho}s em {Ondas} onda(s), teto de request {TetoRequest}s) para layout {LayoutName}",
                    overallTimeoutSeconds, budget.BudgetTrabalhoSeconds, budget.Ondas, budget.TetoRequestSeconds, request.LayoutName);
                return StatusCode(504, new { success = false, error = "Tempo limite excedido ao gerar candidatos de transformação" });
            }

            var candidates = new List<TransformationCandidate>();
            candidates.AddRange(await sysmiddleTask);
            candidates.AddRange(await tclXslTask);

            if (candidates.Count == 0)
                warnings.Add($"Nenhum candidato de transformação encontrado para o layout {request.LayoutName}");

            // Pathway IA (Issue #40): dispara só depois de ter gabarito sysmiddle disponível — nunca
            // como terceiro Task síncrono (ver docs/architecture/pathway-ia-execute-candidates.md §3).
            // Fire-and-forget: NUNCA atrasa nem derruba a resposta síncrona já calculada acima.
            await TryEnqueueAiCandidate(request, layoutRecord, candidates, isXmlInput, CurrentUserId);

            // Fallback automático de IA (design-fallback-ia-automatico-2026-08-16.md §1/§2): só
            // quando NENHUM candidato foi produzido pelos dois pathways síncronos E nenhum deles
            // falhou por infra (Estado B) — aí a correção é operacional, não de transformação, e a
            // IA nunca deveria tentar "recriar" um mapper que já existe e está correto.
            if (candidates.Count == 0)
                TryEnqueueAiFallback(request, layoutRecord, isXmlInput, failureKinds, warnings, pathwayDiagnostics, CurrentUserId);

            string? recommendedId = null;
            if (candidates.Count > 0)
            {
                var bestScored = candidates.Where(c => c.Score.HasValue).OrderByDescending(c => c.Score!.Value).FirstOrDefault();
                recommendedId = bestScored?.CandidateId ?? candidates[0].CandidateId;
            }

            return Ok(new TransformationExecutionCandidatesResponse
            {
                Success = true,
                Candidates = candidates,
                RecommendedCandidateId = recommendedId,
                Warnings = warnings,
                // pathwayDiagnostics (Issue #86): populado na origem por cada pathway (sysmiddle,
                // tcl-xsl, ai-fallback) — ver docs/architecture/diagnostico-issue-86-*.md §4.
                PathwayDiagnostics = pathwayDiagnostics.ToList(),
                CorrelationId = Services.Logging.CorrelationContext.CurrentId
            });
        }

        /// <summary>
        /// Pathway sysmiddle (low-code multi-candidato): reaproveita <see cref="LowCodeAutoTransformationService"/>
        /// (mesma infraestrutura usada por <c>ParseController.Upload</c>). Isolamento total: qualquer falha
        /// aqui (estrutural ou por candidato individual) vira warning, nunca deriuba o pathway tcl-xsl.
        ///
        /// <para><paramref name="cancellationToken"/> é o teto da request. Este é o único dos dois
        /// pathways que precisa dele: aqui cada candidato ocupa um slot de <c>MaxConcurrentRunners</c>
        /// e um processo x86 externo, ambos compartilhados por toda a API. Desistir sem cancelar
        /// deixaria esses recursos presos depois de a resposta já ter ido embora.</para>
        /// </summary>
        private async Task<List<TransformationCandidate>> ExecuteSysmiddleCandidatesAsync(
            TransformationRequest request, LayoutRecord layoutRecord, bool isXmlInput, List<string> warnings,
            ConcurrentBag<FailureKind> failureKinds, ConcurrentBag<Models.Transformation.PathwayDiagnostic> pathwayDiagnostics,
            CancellationToken cancellationToken)
        {
            var result = new List<TransformationCandidate>();

            // Sysmiddle/low-code espera texto posicional (TXT), não XML — não é uma falha do
            // pathway, é entrada fora de escopo (a IA não deveria disparar por causa disso).
            if (isXmlInput)
            {
                _logger.LogInformation(
                    "PathwayDiagnostic {CorrelationId}: pathway={Pathway} status={Status} code={Code} layout={LayoutName} motivo=entrada XML fora do escopo do pathway sysmiddle",
                    Services.Logging.CorrelationContext.CurrentId, "sysmiddle", "not_applicable", "not_applicable", request.LayoutName);
                pathwayDiagnostics.Add(new Models.Transformation.PathwayDiagnostic
                {
                    Pathway = "sysmiddle",
                    Status = "not_applicable",
                    Code = "not_applicable",
                    Message = "Entrada XML — pathway sysmiddle espera texto posicional (TXT)"
                });
                return result;
            }

            try
            {
                var resolvedLayoutGuid = LowCodeLayoutGuidResolver.Resolve(request.LayoutGuid, layoutRecord.LayoutGuid);
                if (resolvedLayoutGuid == null)
                {
                    var msg = $"Layout {request.LayoutName} sem LayoutGuid válido no request ou no catálogo — pathway sysmiddle não aplicável";
                    warnings.Add(msg);
                    failureKinds.Add(FailureKind.NotApplicable);
                    _logger.LogInformation(
                        "PathwayDiagnostic {CorrelationId}: pathway={Pathway} status={Status} code={Code} layout={LayoutName} fonte=request.LayoutGuid/catalogo (nenhum resolvível)",
                        Services.Logging.CorrelationContext.CurrentId, "sysmiddle", "not_applicable", "not_applicable", request.LayoutName);
                    pathwayDiagnostics.Add(new Models.Transformation.PathwayDiagnostic
                    {
                        Pathway = "sysmiddle",
                        Status = "not_applicable",
                        Code = "not_applicable",
                        Message = msg
                    });
                    return result;
                }

                // Este endpoint só possui o registro resumido do catálogo, não o LayoutVO completo.
                // Portanto não inventa MQSeries: persiste unknown/default e põe a amostra em quarentena.
                var positionalMetadata = LowCodePositionalMetadata.CreateDefault();
                var autoResult = await _lowCodeAuto.RunAsync(
                    resolvedLayoutGuid,
                    request.LayoutName,
                    request.InputContent,
                    detectedType: "unknown",
                    originalFileName: "execute-candidates",
                    positionalMetadata: positionalMetadata,
                    cancellationToken: cancellationToken);

                if (!autoResult.Applicable)
                {
                    var msgNoMapper = $"Nenhum mapeador low-code encontrado para o layout {request.LayoutName} (pathway sysmiddle)";
                    warnings.Add(msgNoMapper);
                    // Estado A (§2 do design-fallback-ia-automatico): não existe mapper cadastrado
                    // para este layout — gap real de cobertura, elegível ao fallback de IA.
                    failureKinds.Add(FailureKind.NotApplicable);
                    _logger.LogInformation(
                        "PathwayDiagnostic {CorrelationId}: pathway={Pathway} status={Status} code={Code} layout={LayoutName} layoutGuid={LayoutGuid} fonte=catalogo (consulta a mapeadores low-code sem resultado)",
                        Services.Logging.CorrelationContext.CurrentId, "sysmiddle", "not_applicable", "no_mapper", request.LayoutName, resolvedLayoutGuid);
                    pathwayDiagnostics.Add(new Models.Transformation.PathwayDiagnostic
                    {
                        Pathway = "sysmiddle",
                        Status = "not_applicable",
                        Code = "no_mapper",
                        Message = msgNoMapper
                    });
                    return result;
                }

                // ✅ Issue #141 (design §2, opção B): parse posicional do documento é feito UMA VEZ por
                // request (compartilhado entre todos os candidatos sysmiddle — mesmo documento de
                // entrada) em vez de recalculado por candidato. O mapper de cada candidato já veio
                // decifrado de volta em LowCodeCandidateResult.DecryptedMapperContent (sem 2ª consulta
                // SQL) — só falta parsear o TXT contra o Layout de origem, que RunAsync não expõe (o
                // runner .exe parseia por dentro do processo externo, não via ILayoutParserService).
                ParsingResult? sharedParsingResult = null;
                if (!string.IsNullOrWhiteSpace(layoutRecord.DecryptedContent))
                {
                    try
                    {
                        using var layoutStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(layoutRecord.DecryptedContent));
                        using var txtStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(request.InputContent));
                        sharedParsingResult = await _layoutParser.ParseAsync(layoutStream, txtStream);
                        if (!sharedParsingResult.Success || sharedParsingResult.Layout == null)
                        {
                            _logger.LogWarning(
                                "fieldMappings (issue #141): parse posicional compartilhado falhou para layout {LayoutName} — candidatos sysmiddle seguem sem fieldMappings. Erro={ErrorMessage}",
                                request.LayoutName, sharedParsingResult.ErrorMessage);
                            sharedParsingResult = null;
                        }
                    }
                    catch (Exception parseEx)
                    {
                        // Nunca deixa a composição de fieldMappings afetar o XML já produzido pelo runner.
                        _logger.LogWarning(parseEx,
                            "fieldMappings (issue #141): exceção no parse posicional compartilhado para layout {LayoutName} — candidatos sysmiddle seguem sem fieldMappings",
                            request.LayoutName);
                        sharedParsingResult = null;
                    }
                }

                var anyCandidateFailed = false;
                string lastCandidateFailureMessage = null;
                foreach (var c in autoResult.Candidates)
                {
                    if (c.Success && !string.IsNullOrEmpty(c.OutputXml))
                    {
                        // Issue #138 (Fase 0): resolução estrutural de SectionMappings a partir do
                        // MapeadorVO já decifrado deste candidato — nunca lança (degrada para [] com
                        // xmlNamespaces=null; ver SysmiddleSectionMappingResolver).
                        var (sectionMappings, xmlNamespaces) = SysmiddleSectionMappingResolver.Resolve(
                            c.DecryptedMapperContent, c.OutputXml,
                            msg => _logger.LogDebug("{Message} (mapper={MapperGuid})", msg, c.MapperGuid));

                        result.Add(new TransformationCandidate
                        {
                            CandidateId = $"sysmiddle-{c.MapperGuid}",
                            Pathway = "sysmiddle",
                            TransformedXml = c.OutputXml,
                            FieldMappings = TryComposeFieldMappings(sharedParsingResult, c, request.LayoutName, warnings),
                            SectionMappings = sectionMappings,
                            XmlNamespaces = xmlNamespaces
                        });
                    }
                    else
                    {
                        // Falha isolada de UM candidato — não entra no array (nunca item com XML nulo),
                        // vira warning (ver tabela de decisão do contrato). Estado B (§2 do design):
                        // o mapper EXISTE (Applicable==true) mas a execução falhou — é infra/config
                        // (runner, timeout, .exe ausente), não gap de cobertura. Nunca dispara IA.
                        var sanitizedCandidateError = LowCodeErrorSanitizer.ForWire(c.ErrorMessage ?? "erro desconhecido");
                        anyCandidateFailed = true;
                        lastCandidateFailureMessage = sanitizedCandidateError;
                        warnings.Add($"Candidato {c.MapperGuid} (pathway sysmiddle) falhou: {sanitizedCandidateError}");
                        failureKinds.Add(FailureKind.ExecutionInfraError);
                    }
                }

                if (result.Count > 0)
                {
                    _logger.LogInformation(
                        "PathwayDiagnostic {CorrelationId}: pathway={Pathway} status={Status} layout={LayoutName} layoutGuid={LayoutGuid} candidatos={CandidateCount} fonte=mapeadores low-code do catalogo",
                        Services.Logging.CorrelationContext.CurrentId, "sysmiddle", "candidate_generated", request.LayoutName, resolvedLayoutGuid, result.Count);
                    pathwayDiagnostics.Add(new Models.Transformation.PathwayDiagnostic
                    {
                        Pathway = "sysmiddle",
                        Status = "candidate_generated",
                        Code = null,
                        Message = $"{result.Count} candidato(s) sysmiddle gerado(s)"
                    });
                }
                else if (anyCandidateFailed)
                {
                    // autoResult.Applicable == true (mapper existe) mas TODOS os candidatos
                    // falharam na execução — infra/runner, não gap de cobertura (§4.3 "runner_unavailable").
                    _logger.LogWarning(
                        "PathwayDiagnostic {CorrelationId}: pathway={Pathway} status={Status} code={Code} layout={LayoutName} layoutGuid={LayoutGuid} fonte=execução do runner (mapper existe, execução falhou)",
                        Services.Logging.CorrelationContext.CurrentId, "sysmiddle", "failed", "runner_unavailable", request.LayoutName, resolvedLayoutGuid);
                    pathwayDiagnostics.Add(new Models.Transformation.PathwayDiagnostic
                    {
                        Pathway = "sysmiddle",
                        Status = "failed",
                        Code = "runner_unavailable",
                        Message = lastCandidateFailureMessage ?? "Todos os candidatos sysmiddle falharam na execução"
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Falha estrutural no pathway sysmiddle ao gerar candidatos para layout {LayoutName}. PathwayDiagnostic {CorrelationId}: pathway={Pathway} status={Status} code={Code}",
                    request.LayoutName, Services.Logging.CorrelationContext.CurrentId, "sysmiddle", "failed", "execution_error");
                // Saneado: exceção de I/O deste pathway carrega caminho de disco do servidor e este
                // warning sai no payload 200 (mesmo defeito do §3.1 da spec, outro ponto de saída).
                var sanitizedEx = LowCodeErrorSanitizer.ForWire(ex);
                warnings.Add($"Pathway sysmiddle falhou: {sanitizedEx}");
                // Falha estrutural (exceção) é sempre infra, não "não modelado" — nunca dispara IA.
                failureKinds.Add(FailureKind.ExecutionInfraError);
                pathwayDiagnostics.Add(new Models.Transformation.PathwayDiagnostic
                {
                    Pathway = "sysmiddle",
                    Status = "failed",
                    Code = "execution_error",
                    Message = sanitizedEx
                });
            }

            return result;
        }

        /// <summary>
        /// Compõe <c>fieldMappings</c> (issue #141) para UM candidato sysmiddle bem-sucedido, sobre o
        /// <paramref name="sharedParsingResult"/> já calculado uma vez por request e o mapper decifrado
        /// que o próprio candidato já carrega (<see cref="LowCodeCandidateResult.DecryptedMapperContent"/>
        /// — nenhuma consulta SQL nova). Nunca lança: qualquer falha (parse indisponível, mapper
        /// ilegível, exceção do motor de composição) vira <c>null</c> + warning, e o candidato mantém
        /// o <c>TransformedXml</c> já produzido pelo runner (design §2, "isolamento total").
        /// </summary>
        private IReadOnlyList<XslSynth.Model.FieldToXmlMapping>? TryComposeFieldMappings(
            ParsingResult? sharedParsingResult, LowCodeCandidateResult candidate, string layoutName, List<string> warnings)
        {
            if (sharedParsingResult == null || sharedParsingResult.Layout == null)
                return null; // já logado como warning no ponto em que o parse compartilhado falhou.

            if (string.IsNullOrWhiteSpace(candidate.DecryptedMapperContent))
                return null; // candidato sem mapper decifrado disponível (ex.: falha antes da resolução do mapper).

            try
            {
                var mapperVo = new RealMapperParser().Parse(XDocument.Parse(candidate.DecryptedMapperContent));
                return _fieldMappingComposition.Compose(
                    sharedParsingResult.Layout, sharedParsingResult.ParsedFields, mapperVo, sharedParsingResult.LineInfos);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "fieldMappings (issue #141): falha ao compor mapeamentos estruturais para candidato mapper={MapperGuid} do layout {LayoutName}",
                    candidate.MapperGuid, layoutName);
                warnings.Add($"Candidato {candidate.MapperGuid} (pathway sysmiddle): falha ao compor fieldMappings — ver log do servidor");
                return null;
            }
        }

        /// <summary>
        /// Dispara o job assíncrono do pathway IA (Issue #40) quando há gabarito sysmiddle
        /// disponível — o dono do projeto fechou que a IA "sempre trabalha" nessa condição,
        /// não é um fallback condicionado ao tcl-xsl. Nunca lança: qualquer falha aqui vira
        /// warning e não afeta o array <c>candidates[]</c> já calculado.
        /// </summary>
        private async Task TryEnqueueAiCandidate(
            TransformationRequest request, LayoutRecord layoutRecord, List<TransformationCandidate> candidates, bool isXmlInput,
            string userId)
        {
            var plan = AiCandidateDispatchPlan.TryBuild(
                request.LayoutGuid, layoutRecord.LayoutGuid, request.InputContent, isXmlInput, candidates);
            if (plan == null)
                return; // sem gabarito sysmiddle bem-sucedido ou sem LayoutGuid resolvível: IA não aplicável (§2.1/§3.2 do desenho).

            // ✅ Issue #140/decisão 2026-08-29 (docs/architecture/decisao-pendente-input-xml-
            // repairorchestrator-2026-08-29.md): o RepairOrchestrator (motor novo de
            // AiTransformationCandidateService) exige o resultado do parse posicional REAL
            // (ParsedField) para montar o XML de entrada via ParsedFieldRootTreeBuilder — TXT cru
            // não é XML e nunca vai ser aceito por XDocument.Parse. Parse próprio (não reaproveita
            // o sharedParsingResult de ExecuteSysmiddleCandidatesAsync — escopo local ao método,
            // reestruturar o retorno dele para isso não vale o acoplamento). Nunca lança: falha
            // aqui apenas degrada o motor novo para o loop legado XML-direto (parsedFields=null).
            IReadOnlyList<Models.Entities.ParsedField>? parsedFields = null;
            if (!string.IsNullOrWhiteSpace(layoutRecord.DecryptedContent) && !isXmlInput)
            {
                try
                {
                    using var layoutStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(layoutRecord.DecryptedContent));
                    using var txtStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(request.InputContent));
                    var parseResult = await _layoutParser.ParseAsync(layoutStream, txtStream);
                    if (parseResult.Success && parseResult.ParsedFields is { Count: > 0 })
                        parsedFields = parseResult.ParsedFields;
                }
                catch (Exception parseEx)
                {
                    _logger.LogDebug(parseEx,
                        "Pathway IA: parse posicional para ParsedFieldRootTreeBuilder falhou — motor novo degrada para o loop legado (layout={LayoutName})",
                        request.LayoutName);
                }
            }

            try
            {
                // ✅ Não usa a request.HttpContext.RequestAborted — o job sobrevive ao fim da request
                // (dotnet-standards.md §Background work). CancellationToken.None + teto de sanidade
                // interno do serviço (AiTransformationCandidateOptions.SanityTimeoutMinutes).
                // userId (issue #92): particiona o ticket na AiCandidateStore — só quem disparou o job
                // consegue consultá-lo depois em ia-status.
                _ = _aiCandidateService.EnqueueAsync(
                    userId,
                    plan.Ticket,
                    request.LayoutName,
                    plan.LayoutGuid,
                    plan.MapperGuid,
                    request.InputContent,
                    plan.GroundTruthXml,
                    CancellationToken.None,
                    parsedFields);
            }
            catch (Exception ex)
            {
                // EnqueueAsync não deveria lançar (contrato do serviço), mas isolamento total aqui
                // também — nunca derrubar a resposta síncrona de execute-candidates por causa da IA.
                _logger.LogWarning(ex, "Falha ao disparar o pathway IA para layout {LayoutName}", request.LayoutName);
            }
        }

        /// <summary>
        /// Fallback automático de IA — Estado A (docs/architecture/design-fallback-ia-automatico-2026-08-16.md
        /// §1/§2/§5). Só chega aqui quando <c>candidates.Count == 0</c>. Dispara <see cref="IAiTransformationCandidateService.EnqueueAsync"/>
        /// no modo SEM gabarito (<c>groundTruthXml: null</c>) se, e somente se, nenhum dos pathways
        /// síncronos reportou <see cref="FailureKind.ExecutionInfraError"/> (Estado B — correção é
        /// operacional, a IA não deveria tentar recriar um mapper que já existe). Consulta o
        /// <see cref="IAiFallbackSuppressionGate"/> antes de disparar para não repetir uma chamada
        /// cara ao Ollama para um layout que já falhou recentemente. Nunca lança: qualquer falha aqui
        /// vira warning e não afeta a resposta síncrona já calculada.
        /// </summary>
        private void TryEnqueueAiFallback(
            TransformationRequest request, LayoutRecord layoutRecord, bool isXmlInput,
            ConcurrentBag<FailureKind> failureKinds, List<string> warnings,
            ConcurrentBag<Models.Transformation.PathwayDiagnostic> pathwayDiagnostics, string userId)
        {
            try
            {
                if (failureKinds.Any(k => k == FailureKind.ExecutionInfraError))
                {
                    // Estado B: já existe o warning de infra específico emitido pelo pathway que
                    // falhou (e já virou pathwayDiagnostics próprio de sysmiddle/tcl-xsl) — nada a
                    // acrescentar aqui, só não disparar a IA (§2 do desenho). Não emite um 3º
                    // diagnóstico "ai-fallback: not_applicable" para não duplicar sinal — o front já
                    // tem os itens failed de quem realmente quebrou.
                    return;
                }

                var resolvedLayoutGuidText = LowCodeLayoutGuidResolver.Resolve(request.LayoutGuid, layoutRecord.LayoutGuid);
                if (resolvedLayoutGuidText == null || !Guid.TryParse(resolvedLayoutGuidText, out var resolvedLayoutGuid))
                {
                    var msg = $"Layout {request.LayoutName} sem LayoutGuid válido — fallback de IA não aplicável";
                    warnings.Add(msg);
                    _logger.LogInformation(
                        "PathwayDiagnostic {CorrelationId}: pathway={Pathway} status={Status} code={Code} layout={LayoutName} fonte=request.LayoutGuid/catalogo (nenhum resolvível)",
                        Services.Logging.CorrelationContext.CurrentId, "ai-fallback", "not_applicable", "not_applicable", request.LayoutName);
                    pathwayDiagnostics.Add(new Models.Transformation.PathwayDiagnostic
                    {
                        Pathway = "ai-fallback",
                        Status = "not_applicable",
                        Code = "not_applicable",
                        Message = msg
                    });
                    return;
                }

                if (_aiFallbackGate.IsInCooldown(resolvedLayoutGuid, out var retryAt))
                {
                    var msg = $"Pathway IA fallback suprimido para este layout até {retryAt:HH:mm} (já tentado sem sucesso)";
                    warnings.Add(msg);
                    _logger.LogInformation(
                        "PathwayDiagnostic {CorrelationId}: pathway={Pathway} status={Status} code={Code} layout={LayoutName} layoutGuid={LayoutGuid} fonte=IAiFallbackSuppressionGate (cooldown ativo até {RetryAt})",
                        Services.Logging.CorrelationContext.CurrentId, "ai-fallback", "not_applicable", "not_applicable", request.LayoutName, resolvedLayoutGuid, retryAt);
                    pathwayDiagnostics.Add(new Models.Transformation.PathwayDiagnostic
                    {
                        Pathway = "ai-fallback",
                        Status = "not_applicable",
                        Code = "not_applicable",
                        Message = msg
                    });
                    return;
                }

                var ticket = LowCodeTransformationStore.BuildTicketFromContent(request.InputContent, resolvedLayoutGuidText);
                if (ticket == null)
                {
                    var msg = $"Layout {request.LayoutName}: não foi possível compor o ticket do fallback de IA";
                    warnings.Add(msg);
                    _logger.LogWarning(
                        "PathwayDiagnostic {CorrelationId}: pathway={Pathway} status={Status} code={Code} layout={LayoutName} layoutGuid={LayoutGuid} fonte=LowCodeTransformationStore.BuildTicketFromContent (retornou null)",
                        Services.Logging.CorrelationContext.CurrentId, "ai-fallback", "failed", "configuration_error", request.LayoutName, resolvedLayoutGuid);
                    pathwayDiagnostics.Add(new Models.Transformation.PathwayDiagnostic
                    {
                        Pathway = "ai-fallback",
                        Status = "failed",
                        Code = "configuration_error",
                        Message = msg
                    });
                    return;
                }

                // mapperGuid: não há candidato sysmiddle bem-sucedido no Estado A (por definição), então
                // não existe um MapperGuid real a associar — usa o próprio LayoutGuid como identificador
                // estável do job (mesma convenção de particionamento por layout do gate de supressão).
                _ = _aiCandidateService.EnqueueAsync(
                    userId,
                    ticket,
                    request.LayoutName,
                    resolvedLayoutGuid,
                    mapperGuid: resolvedLayoutGuidText,
                    request.InputContent,
                    groundTruthXml: null,
                    CancellationToken.None);

                var enqueuedMsg = $"Nenhum candidato de transformação encontrado — fallback automático de IA enfileirado (ticket {ticket}), consulte GET execute-candidates/{ticket}/ia-status";
                warnings.Add(enqueuedMsg);
                // "candidate_generated" no sentido de que o pathway produziu um item consultável
                // (ticket assíncrono) — não um XML pronto, mas o front tem o que fazer com ele
                // (§4.2 do desenho: "inclui o ticket assíncrono do fallback de IA, que 'gera' no
                // sentido de estar em processamento").
                _logger.LogInformation(
                    "PathwayDiagnostic {CorrelationId}: pathway={Pathway} status={Status} layout={LayoutName} layoutGuid={LayoutGuid} ticket={Ticket} fonte=IAiTransformationCandidateService.EnqueueAsync (sem gabarito)",
                    Services.Logging.CorrelationContext.CurrentId, "ai-fallback", "candidate_generated", request.LayoutName, resolvedLayoutGuid, ticket);
                pathwayDiagnostics.Add(new Models.Transformation.PathwayDiagnostic
                {
                    Pathway = "ai-fallback",
                    Status = "candidate_generated",
                    Code = null,
                    Message = enqueuedMsg
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Falha ao disparar o fallback automático de IA para layout {LayoutName}. PathwayDiagnostic {CorrelationId}: pathway={Pathway} status={Status} code={Code}",
                    request.LayoutName, Services.Logging.CorrelationContext.CurrentId, "ai-fallback", "failed", "execution_error");
                pathwayDiagnostics.Add(new Models.Transformation.PathwayDiagnostic
                {
                    Pathway = "ai-fallback",
                    Status = "failed",
                    Code = "execution_error",
                    Message = LowCodeErrorSanitizer.ForWire(ex)
                });
            }
        }

        /// <summary>
        /// Consulta o status do job assíncrono do pathway IA (Issue #40). Mesma política de
        /// autorização de <see cref="ExecuteTransformationCandidates"/> (Issue #32) — endpoint
        /// novo, mesmo custo/sensibilidade de disparar processos/objetos caros.
        /// </summary>
        /// <remarks>
        /// Issue #92: a consulta é isolada por usuário — <c>ticket</c> de outro usuário devolve 404,
        /// nunca 403. 403 confirmaria "o ticket existe, mas não é seu" (enumeração); 404 se comporta
        /// exatamente como um ticket inexistente/expirado, que é o mesmo caso hoje. Único gate de
        /// papel era o <c>[Authorize(Roles = "admin")]</c> — a issue #93 abriu o endpoint além de
        /// admin (<c>[Authorize]</c> simples) porque o isolamento por dono já estava pronto.
        /// Candidatos originados do fallback automático de IA (Estado A) trazem
        /// <c>HasGroundTruth=false</c>: não há gabarito/histórico de validação para o layout, então
        /// o resultado é uma sugestão que exige revisão humana antes de ir para produção.
        /// </remarks>
        [Authorize]
        [HttpGet("execute-candidates/{ticket}/ia-status")]
        public async Task<IActionResult> GetAiCandidateStatus(string ticket, CancellationToken cancellationToken)
        {
            var status = await _aiCandidateService.GetStatusAsync(CurrentUserId, ticket, cancellationToken);
            if (status.Status == AiCandidateStatus.StatusNotFound)
                return NotFound();

            return Ok(status);
        }

        /// <summary>
        /// Pathway tcl-xsl (canônico): reaproveita <see cref="TransformationPipelineService"/>, mesma lógica
        /// já usada pelo endpoint <c>execute</c>. Hoje produz no máximo 1 candidato (o pipeline não tem
        /// noção de múltiplos TCL/XSL candidatos para o mesmo layout).
        /// </summary>
        private async Task<List<TransformationCandidate>> ExecuteTclXslCandidatesAsync(
            TransformationRequest request, bool isXmlInput, List<string> warnings, ConcurrentBag<FailureKind> failureKinds,
            ConcurrentBag<Models.Transformation.PathwayDiagnostic> pathwayDiagnostics)
        {
            var result = new List<TransformationCandidate>();

            try
            {
                var pipelineResult = isXmlInput
                    ? await _pipelineService.TransformXmlToXmlAsync(
                        request.InputContent,
                        request.SourceDocumentType ?? "NFe",
                        request.TargetDocumentType ?? "NFe",
                        request.LayoutName)
                    : await _pipelineService.TransformTxtToXmlAsync(
                        request.InputContent,
                        request.LayoutName,
                        request.TargetDocumentType ?? "NFe");

                if (!pipelineResult.Success || string.IsNullOrEmpty(pipelineResult.TransformedXml))
                {
                    // Saneado (§5 do diagnóstico-issue-86): pipelineResult.Errors pode carregar
                    // caminho de disco cru (IOException/XmlException internos do pipeline).
                    var sanitizedTclXslError = LowCodeErrorSanitizer.ForWire(string.Join("; ", pipelineResult.Errors));
                    warnings.Add($"Candidato tcl-xsl falhou: {sanitizedTclXslError}");
                    // "Sem heurística aplicável" para este layout — Estado A (§2 do design).
                    failureKinds.Add(FailureKind.NotApplicable);

                    // Issue #86 §2.4: distingue "arquivo MAP não encontrado" de "arquivo XSL não
                    // encontrado" pelo ErrorCode populado na origem (TransformationPipelineService),
                    // não por regex sobre a mensagem já sanitizada.
                    var code = pipelineResult.ErrorCode switch
                    {
                        "map_not_found" => "map_not_found",
                        "xsl_not_found" => "xsl_not_found",
                        _ => "map_not_found" // fallback conservador: maioria dos casos "não aplicável" hoje é ausência de MAP
                    };
                    _logger.LogWarning(
                        "PathwayDiagnostic {CorrelationId}: pathway={Pathway} status={Status} code={Code} layout={LayoutName} fonte=TransformationPipelineService.ErrorCode={ErrorCode}",
                        Services.Logging.CorrelationContext.CurrentId, "tcl-xsl", "failed", code, request.LayoutName, pipelineResult.ErrorCode);
                    pathwayDiagnostics.Add(new Models.Transformation.PathwayDiagnostic
                    {
                        Pathway = "tcl-xsl",
                        Status = "failed",
                        Code = code,
                        Message = sanitizedTclXslError
                    });
                    return result;
                }

                object? validation = null;
                if (request.Validate)
                {
                    try
                    {
                        validation = await _validatorService.ValidateTransformationAsync(
                            isXmlInput ? null : request.InputContent,
                            request.LayoutName,
                            pipelineResult.TclPath,
                            pipelineResult.XslPath,
                            request.ExpectedOutput);
                    }
                    catch (Exception ex)
                    {
                        // Falha de validação não invalida o candidato em si (o XML transformado existe) —
                        // só fica sem o campo Validation preenchido.
                        _logger.LogWarning(ex, "Falha ao validar candidato tcl-xsl para layout {LayoutName}", request.LayoutName);
                        // Saneado (§5 do diagnóstico-issue-86): mesmo padrão do sysmiddle (linha ~385).
                        warnings.Add($"Validação do candidato tcl-xsl falhou: {LowCodeErrorSanitizer.ForWire(ex)}");
                    }
                }

                result.Add(new TransformationCandidate
                {
                    CandidateId = "tclxsl-1",
                    Pathway = "tcl-xsl",
                    TransformedXml = pipelineResult.TransformedXml,
                    SegmentMappings = pipelineResult.SegmentMappings?.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                    Validation = validation,
                    // Issue #138 (Fase 0): pathway tcl-xsl NÃO suporta rastreabilidade de linha/seção
                    // ainda — SectionMappings=null por definição (semântica obrigatória do contrato).
                    // O SegmentMappings existente acima é um artefato PRÉVIO e DIFERENTE: só existe
                    // para entrada MQSeries, é indexado por número de linha (não GUID/estrutura) e
                    // carrega um XmlElementPath fixo ("NFe/infNFe") hardcoded em MqSeriesToXmlTransformer
                    // — não é XPath resolvido estruturalmente, não atende ao contrato de #138. Virar
                    // SectionMappings real para tcl-xsl exigiria expor a mesma resolução estrutural que
                    // o pathway sysmiddle já tem (GUID→XPath via RealMapperParser) dentro do
                    // TransformationPipelineService — fora do escopo desta fase.
                    SectionMappings = null
                });

                _logger.LogInformation(
                    "PathwayDiagnostic {CorrelationId}: pathway={Pathway} status={Status} layout={LayoutName} tclPath={TclPath} xslPath={XslPath} fonte=TransformationPipelineService",
                    Services.Logging.CorrelationContext.CurrentId, "tcl-xsl", "candidate_generated", request.LayoutName,
                    System.IO.Path.GetFileName(pipelineResult.TclPath), System.IO.Path.GetFileName(pipelineResult.XslPath));
                pathwayDiagnostics.Add(new Models.Transformation.PathwayDiagnostic
                {
                    Pathway = "tcl-xsl",
                    Status = "candidate_generated",
                    Code = null,
                    Message = "Candidato tcl-xsl gerado com sucesso"
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Falha estrutural no pathway tcl-xsl ao gerar candidato para layout {LayoutName}. PathwayDiagnostic {CorrelationId}: pathway={Pathway} status={Status} code={Code}",
                    request.LayoutName, Services.Logging.CorrelationContext.CurrentId, "tcl-xsl", "failed", "execution_error");
                // Saneado (§5 do diagnóstico-issue-86): mesmo padrão do sysmiddle (linha ~385).
                var sanitizedTclXslEx = LowCodeErrorSanitizer.ForWire(ex);
                warnings.Add($"Pathway tcl-xsl falhou: {sanitizedTclXslEx}");
                // Exceção estrutural é infra, não "não modelado" — nunca dispara IA.
                failureKinds.Add(FailureKind.ExecutionInfraError);
                pathwayDiagnostics.Add(new Models.Transformation.PathwayDiagnostic
                {
                    Pathway = "tcl-xsl",
                    Status = "failed",
                    Code = "execution_error",
                    Message = sanitizedTclXslEx
                });
            }

            return result;
        }

        /// <summary>
        /// Valida transformação existente
        /// </summary>
        [HttpPost("validate")]
        public async Task<IActionResult> ValidateTransformation([FromBody] ValidationRequest request)
        {
            try
            {
                _logger.LogInformation("Validando transformação para layout: {LayoutName}", request.LayoutName);

                var validationResult = await _validatorService.ValidateTransformationAsync(
                    request.InputTxt,
                    request.LayoutName,
                    request.TclPath,
                    request.XslPath,
                    request.ExpectedOutputXml);

                return Ok(validationResult);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao validar transformação");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Executa aprendizado a partir de exemplos
        /// </summary>
        [HttpPost("learn-from-examples")]
        public async Task<IActionResult> LearnFromExamples([FromBody] LearnFromExamplesRequest request)
        {
            try
            {
                _logger.LogInformation("Iniciando aprendizado a partir de exemplos para layout: {LayoutName}", request.LayoutName);

                object learningResult = new { success = false };

                if (request.TclExamples != null && request.TclExamples.Any())
                {
                    var tclResult = await _learningService.LearnTclPatternsAsync(
                        request.LayoutName,
                        request.TclExamples);

                    learningResult = new { success = tclResult.Success, patterns = tclResult.PatternsLearned };
                }

                if (request.XslExamples != null && request.XslExamples.Any())
                {
                    var xslResult = await _learningService.LearnXslPatternsAsync(
                        request.LayoutName,
                        request.XslExamples);

                    learningResult = new { success = xslResult.Success, patterns = xslResult.PatternsLearned };
                }

                return Ok(learningResult);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao executar aprendizado");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Executa teste automatizado de transformação
        /// </summary>
        [HttpPost("run-test")]
        public async Task<IActionResult> RunTransformationTest([FromBody] TransformationTestRequest request)
        {
            try
            {
                _logger.LogInformation("Executando teste de transformação para layout: {LayoutName}", request.LayoutName);

                // Executar transformação
                var transformationResult = await _pipelineService.TransformTxtToXmlAsync(
                    request.InputTxt,
                    request.LayoutName,
                    request.TargetDocumentType ?? "NFe");

                if (!transformationResult.Success)
                {
                    return BadRequest(new
                    {
                        success = false,
                        testPassed = false,
                        errors = transformationResult.Errors
                    });
                }

                // Validar resultado
                var validationResult = await _validatorService.ValidateTransformationAsync(
                    request.InputTxt,
                    request.LayoutName,
                    transformationResult.TclPath,
                    transformationResult.XslPath,
                    request.ExpectedOutputXml);

                var testPassed = validationResult.Success &&
                                validationResult.ValidationSteps.All(s => s.Success);

                return Ok(new
                {
                    success = true,
                    testPassed = testPassed,
                    transformedXml = transformationResult.TransformedXml,
                    validation = validationResult,
                    segmentMappings = transformationResult.SegmentMappings
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao executar teste de transformação");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Executa transformação usando o motor low-code (SysMiddle) via runner x86.
        /// </summary>
        // Issue #32: idem execute-candidates — era restrito ao papel "admin". Issue #93: mesma
        // reabertura para qualquer usuário autenticado.
        [Authorize]
        [HttpPost("execute-lowcode")]
        public async Task<IActionResult> ExecuteLowCode([FromBody] LowCodeTransformationRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.InputContent))
                    return BadRequest(new { error = "InputContent é obrigatório" });

                if (string.IsNullOrWhiteSpace(request.MapperId) && string.IsNullOrWhiteSpace(request.MapperName))
                    return BadRequest(new { error = "MapperId ou MapperName é obrigatório" });

                var transformed = await _lowCode.TransformAsync(
                    request.InputContent,
                    mapperId: request.MapperId,
                    mapperName: request.MapperName,
                    fileName: request.FileName,
                    package: request.Package,
                    globalFolder: request.GlobalFolder,
                    sysmiddleDir: request.SysmiddleDir);

                return Ok(new { success = true, transformedXml = transformed });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao executar transformação low-code");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Issue #140 (itens 2/6-9 da divisão de trabalho, design em
        /// docs/architecture/design-resolucao-estrutural-txt-xml-issue-140.md §8): endpoint
        /// dedicado que conecta o motor de resolução estrutural TXT↔XML já implementado (item 1/3/4/5,
        /// <c>ai/XslSynth.Contracts/Core/StructuralResolution/</c>) ao pipeline real — parse posicional
        /// real (<see cref="ILayoutParserService"/>, fonte de <c>ParsedField.Occurrence</c> real) +
        /// mapper real decifrado (<see cref="MapperDatabaseService"/> + <c>RealMapperParser</c>, Parser
        /// B canônico da #139) + catálogo XML de destino cacheado (NF-e via XSD).
        ///
        /// <para>Deliberadamente SEPARADO do contrato de <c>execute-candidates</c>
        /// (<see cref="TransformationExecutionCandidatesResponse"/>): a decisão de expor
        /// <c>FieldToXmlMapping[]</c> como recurso de primeira classe dentro daquele contrato é da
        /// issue #141, não desta — aqui só a infraestrutura de composição é ligada ponta a ponta.</para>
        ///
        /// <para>Resiliência: qualquer falha (layout/mapper não encontrado, XSD indisponível, parse
        /// malformado) vira 200 com <c>fieldMappings: []</c> + warning, nunca deriuba com 500 — o
        /// motor de resolução estrutural é best-effort por natureza (design §5).</para>
        /// </summary>
        [Authorize]
        [HttpPost("field-mappings")]
        public async Task<IActionResult> GetFieldMappings([FromBody] FieldMappingsRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.LayoutName) || string.IsNullOrWhiteSpace(request.InputContent))
                return BadRequest(new { success = false, error = "LayoutName e InputContent são obrigatórios" });

            var warnings = new List<string>();

            LayoutRecord? layoutRecord;
            try
            {
                var searchResponse = await _layoutDb.SearchLayoutsAsync(new LayoutSearchRequest { SearchTerm = request.LayoutName });
                if (!searchResponse.Success)
                    throw new InvalidOperationException(searchResponse.ErrorMessage);

                layoutRecord = searchResponse.Layouts
                    .FirstOrDefault(l => string.Equals(l.Name, request.LayoutName, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha de infraestrutura ao resolver layout {LayoutName} para field-mappings", request.LayoutName);
                return StatusCode(500, new { success = false, error = "Falha de infraestrutura ao consultar o catálogo de layouts" });
            }

            if (layoutRecord == null || string.IsNullOrWhiteSpace(layoutRecord.DecryptedContent))
                return BadRequest(new { success = false, error = $"Layout '{request.LayoutName}' não encontrado ou sem conteúdo decifrado" });

            var resolvedLayoutGuid = LowCodeLayoutGuidResolver.Resolve(request.LayoutGuid, layoutRecord.LayoutGuid);
            if (resolvedLayoutGuid == null)
                return BadRequest(new { success = false, error = $"Layout {request.LayoutName} sem LayoutGuid válido no request ou no catálogo" });

            try
            {
                // 1) Parse posicional real do documento de entrada — fonte de Layout (crosswalk
                //    GUID/nome de origem) e ParsedField (Occurrence físico real, nunca sintético).
                using var layoutStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(layoutRecord.DecryptedContent));
                using var txtStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(request.InputContent));
                var parsingResult = await _layoutParser.ParseAsync(layoutStream, txtStream);

                if (!parsingResult.Success || parsingResult.Layout == null)
                {
                    warnings.Add($"Parse posicional falhou para layout {request.LayoutName}: {parsingResult.ErrorMessage}");
                    return Ok(new { success = true, fieldMappings = Array.Empty<object>(), warnings });
                }

                // 2) Mapper real (Parser B canônico #139) — mesma seleção/priorização já usada pelo
                //    pathway sysmiddle de execute-candidates.
                var ranked = await _mapperDb.GetRankedMapperCandidatesForLayoutGuidAsync(
                    resolvedLayoutGuid, _lowCodeOpt.ProjectId, _lowCodeOpt.AllowedPackageGuids);
                var mapperRecord = ranked.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.DecryptedContent));

                if (mapperRecord == null)
                {
                    warnings.Add($"Nenhum mapeador decifrável encontrado para o layout {request.LayoutName}");
                    return Ok(new { success = true, fieldMappings = Array.Empty<object>(), warnings });
                }

                var mapperVo = new RealMapperParser().Parse(XDocument.Parse(mapperRecord.DecryptedContent));

                // 3) Composição: motor de resolução estrutural (itens 1/3/4/5, já implementado) sobre
                //    dados 100% reais — nenhuma coordenada sintética.
                var fieldMappings = _fieldMappingComposition.Compose(parsingResult.Layout, parsingResult.ParsedFields, mapperVo, parsingResult.LineInfos);

                return Ok(new
                {
                    success = true,
                    mapperGuid = mapperRecord.MapperGuid,
                    fieldMappings,
                    warnings
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao compor field mappings para layout {LayoutName}", request.LayoutName);
                warnings.Add("Falha ao compor mapeamentos estruturais — ver log do servidor");
                return Ok(new { success = true, fieldMappings = Array.Empty<object>(), warnings });
            }
        }
    }

    /// <summary>Request do endpoint /field-mappings (issue #140). Mesma convenção de LayoutGuid
    /// opcional já usada por <see cref="TransformationRequest"/> (precedência sobre o catálogo).</summary>
    public class FieldMappingsRequest
    {
        public string LayoutName { get; set; } = "";
        public string InputContent { get; set; } = "";
        public string? LayoutGuid { get; set; }
    }

    public class LowCodeTransformationRequest
    {
        public string InputContent { get; set; } = "";
        public string? MapperId { get; set; }
        public string? MapperName { get; set; }
        public string? FileName { get; set; }

        // Overrides opcionais (caso não queira depender do appsettings)
        public string? Package { get; set; }
        public string? GlobalFolder { get; set; }
        public string? SysmiddleDir { get; set; }
    }
}
