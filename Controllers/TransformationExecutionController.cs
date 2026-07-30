using LayoutParserApi.Services.Transformation;
using LayoutParserApi.Services.XmlAnalysis;
using LayoutParserApi.Models;
using LayoutParserApi.Services.Transformation.LowCode;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using LayoutParserApi.Services.XmlAnalysis.Models;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Models.Database;
using LayoutParserApi.Models.Transformation;

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

        public TransformationExecutionController(
            ILogger<TransformationExecutionController> logger,
            TransformationPipelineService pipelineService,
            TransformationValidatorService validatorService,
            TransformationLearningService learningService,
            AutoTransformationGeneratorService autoGenerator,
            LowCodeTransformationService lowCode,
            LowCodeAutoTransformationService lowCodeAuto,
            ILayoutDatabaseService layoutDb,
            IOptions<LowCodeRunnerOptions> lowCodeOptions)
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
        }

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
        /// </summary>
        [HttpPost("execute-candidates")]
        public async Task<IActionResult> ExecuteTransformationCandidates([FromBody] TransformationRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.InputContent))
                return BadRequest(new { success = false, errors = new[] { "InputContent é obrigatório" }, warnings = Array.Empty<string>() });

            if (string.IsNullOrEmpty(request.LayoutName))
                return BadRequest(new { success = false, errors = new[] { "LayoutName é obrigatório" }, warnings = Array.Empty<string>() });

            _logger.LogInformation("Executando transformação multi-candidato para layout: {LayoutName}", request.LayoutName);

            // Resolver o layout no banco: serve tanto para validar existência (400 se não encontrado)
            // quanto para obter o LayoutGuid necessário ao pathway sysmiddle. Exceção aqui = falha de
            // infra que impede sequer listar candidatos → 500 (linha "Falha total de infraestrutura"
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

            var sysmiddleTask = ExecuteSysmiddleCandidatesAsync(request, layoutRecord, isXmlInput, warnings);
            var tclXslTask = ExecuteTclXslCandidatesAsync(request, isXmlInput, warnings);

            // Timeout do CONJUNTO: decisão de design (não 100% especificada no contrato) — como os
            // candidatos sysmiddle competem pelo mesmo semáforo do runner (LowCode:MaxConcurrentRunners),
            // o pior caso plausível para o conjunto inteiro é esperar por um slot livre e então rodar;
            // daí o budget derivado de RunnerTimeoutSeconds * MaxConcurrentRunners.
            var overallTimeoutSeconds = Math.Max(1, _lowCodeOpt.RunnerTimeoutSeconds) * Math.Max(1, _lowCodeOpt.MaxConcurrentRunners);
            var allTask = Task.WhenAll(sysmiddleTask, tclXslTask);
            var winner = await Task.WhenAny(allTask, Task.Delay(TimeSpan.FromSeconds(overallTimeoutSeconds)));

            if (winner != allTask)
            {
                _logger.LogWarning("Timeout do conjunto de candidatos (>{TimeoutSeconds}s) para layout {LayoutName}", overallTimeoutSeconds, request.LayoutName);
                return StatusCode(504, new { success = false, error = "Tempo limite excedido ao gerar candidatos de transformação" });
            }

            var candidates = new List<TransformationCandidate>();
            candidates.AddRange(await sysmiddleTask);
            candidates.AddRange(await tclXslTask);

            if (candidates.Count == 0)
                warnings.Add($"Nenhum candidato de transformação encontrado para o layout {request.LayoutName}");

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
                Warnings = warnings
            });
        }

        /// <summary>
        /// Pathway sysmiddle (low-code multi-candidato): reaproveita <see cref="LowCodeAutoTransformationService"/>
        /// (mesma infraestrutura usada por <c>ParseController.Upload</c>). Isolamento total: qualquer falha
        /// aqui (estrutural ou por candidato individual) vira warning, nunca deriuba o pathway tcl-xsl.
        /// </summary>
        private async Task<List<TransformationCandidate>> ExecuteSysmiddleCandidatesAsync(
            TransformationRequest request, LayoutRecord layoutRecord, bool isXmlInput, List<string> warnings)
        {
            var result = new List<TransformationCandidate>();

            // Sysmiddle/low-code espera texto posicional (TXT), não XML — mesma premissa do
            // ParseController.Upload (gate por detectedType != "xml").
            if (isXmlInput)
                return result;

            try
            {
                if (layoutRecord.LayoutGuid == Guid.Empty)
                {
                    warnings.Add($"Layout {request.LayoutName} sem LayoutGuid válido — pathway sysmiddle não aplicável");
                    return result;
                }

                var autoResult = await _lowCodeAuto.RunAsync(
                    layoutRecord.LayoutGuid.ToString(),
                    request.LayoutName,
                    request.InputContent,
                    detectedType: "mqseries",
                    originalFileName: "execute-candidates");

                if (!autoResult.Applicable)
                {
                    warnings.Add($"Nenhum mapeador low-code encontrado para o layout {request.LayoutName} (pathway sysmiddle)");
                    return result;
                }

                foreach (var c in autoResult.Candidates)
                {
                    if (c.Success && !string.IsNullOrEmpty(c.OutputXml))
                    {
                        result.Add(new TransformationCandidate
                        {
                            CandidateId = $"sysmiddle-{c.MapperGuid}",
                            Pathway = "sysmiddle",
                            TransformedXml = c.OutputXml
                        });
                    }
                    else
                    {
                        // Falha isolada de UM candidato — não entra no array (nunca item com XML nulo),
                        // vira warning (ver tabela de decisão do contrato).
                        warnings.Add($"Candidato {c.MapperGuid} (pathway sysmiddle) falhou: {c.ErrorMessage ?? "erro desconhecido"}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha estrutural no pathway sysmiddle ao gerar candidatos para layout {LayoutName}", request.LayoutName);
                warnings.Add($"Pathway sysmiddle falhou: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Pathway tcl-xsl (canônico): reaproveita <see cref="TransformationPipelineService"/>, mesma lógica
        /// já usada pelo endpoint <c>execute</c>. Hoje produz no máximo 1 candidato (o pipeline não tem
        /// noção de múltiplos TCL/XSL candidatos para o mesmo layout).
        /// </summary>
        private async Task<List<TransformationCandidate>> ExecuteTclXslCandidatesAsync(
            TransformationRequest request, bool isXmlInput, List<string> warnings)
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
                    warnings.Add($"Candidato tcl-xsl falhou: {string.Join("; ", pipelineResult.Errors)}");
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
                        warnings.Add($"Validação do candidato tcl-xsl falhou: {ex.Message}");
                    }
                }

                result.Add(new TransformationCandidate
                {
                    CandidateId = "tclxsl-1",
                    Pathway = "tcl-xsl",
                    TransformedXml = pipelineResult.TransformedXml,
                    SegmentMappings = pipelineResult.SegmentMappings?.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                    Validation = validation
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha estrutural no pathway tcl-xsl ao gerar candidato para layout {LayoutName}", request.LayoutName);
                warnings.Add($"Pathway tcl-xsl falhou: {ex.Message}");
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