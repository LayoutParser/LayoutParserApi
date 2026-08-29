using LayoutParserApi.Services.Learning;

using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class LearningController : ControllerBase
    {
        private readonly ILogger<LearningController> _logger;
        private readonly ExampleLearningService _exampleLearningService;

        public LearningController(
            ILogger<LearningController> logger,
            ExampleLearningService exampleLearningService)
        {
            _logger = logger;
            _exampleLearningService = exampleLearningService;
        }

        /// <summary>
        /// Varre todos os exemplos TCL/XSL disponíveis no disco (pasta de aprendizado) e alimenta
        /// o motor de padrões — versão em lote de <c>TransformationExecutionController.LearnFromExamples</c> (que recebe exemplos inline no request).
        /// </summary>
        /// <response code="200">Resultado do aprendizado em lote.</response>
        /// <response code="500">Falha ao processar os exemplos.</response>
        [HttpPost("learn-from-examples")]
        public async Task<IActionResult> LearnFromExamples()
        {
            try
            {
                _logger.LogInformation("Iniciando aprendizado a partir de exemplos");
                var result = await _exampleLearningService.LearnFromAllExamplesAsync();
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao executar aprendizado");
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }
}
