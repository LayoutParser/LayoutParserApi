using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using LayoutParserApi.Services.Learning;
using System.Threading.Tasks;

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
        /// Aprende a partir de todos os exemplos TCL e XSL disponíveis
        /// </summary>
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
