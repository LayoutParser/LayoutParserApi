using LayoutParserApi.Services.Transformation;
using LayoutParserApi.Services.XmlAnalysis;
using LayoutParserApi.Models;

using Microsoft.AspNetCore.Mvc;
using LayoutParserApi.Services.XmlAnalysis.Models;

namespace LayoutParserApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TransformationExecutionController : ControllerBase
    {
        private readonly ILogger<TransformationExecutionController> _logger;
        private readonly TransformationPipelineService _pipelineService;
        private readonly TransformationValidatorService _validatorService;
        private readonly TransformationLearningService _learningService;
        private readonly AutoTransformationGeneratorService _autoGenerator;

        public TransformationExecutionController(
            ILogger<TransformationExecutionController> logger,
            TransformationPipelineService pipelineService,
            TransformationValidatorService validatorService,
            TransformationLearningService learningService,
            AutoTransformationGeneratorService autoGenerator)
        {
            _logger = logger;
            _pipelineService = pipelineService;
            _validatorService = validatorService;
            _learningService = learningService;
            _autoGenerator = autoGenerator;
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
    }
}