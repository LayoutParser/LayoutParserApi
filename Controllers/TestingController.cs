using LayoutParserApi.Services.Testing;

using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TestingController : ControllerBase
    {
        private readonly ILogger<TestingController> _logger;
        private readonly AutomatedTransformationTestService _testService;

        public TestingController(
            ILogger<TestingController> logger,
            AutomatedTransformationTestService testService)
        {
            _logger = logger;
            _testService = testService;
        }

        /// <summary>
        /// Executa todos os testes automatizados
        /// </summary>
        [HttpPost("run-all")]
        public async Task<IActionResult> RunAllTests()
        {
            try
            {
                _logger.LogInformation("Executando todos os testes automatizados");
                var result = await _testService.RunAllTestsAsync();
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao executar testes");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Executa testes para um layout específico
        /// </summary>
        [HttpPost("run-for-layout")]
        public async Task<IActionResult> RunTestsForLayout([FromBody] TestLayoutRequest request)
        {
            try
            {
                _logger.LogInformation("Executando testes para layout: {LayoutName}", request.LayoutName);
                var result = await _testService.RunTestsForLayoutAsync(request.LayoutName, request.ExamplesDirectory);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao executar testes para layout");
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }

    public class TestLayoutRequest
    {
        public string LayoutName { get; set; }
        public string ExamplesDirectory { get; set; }
    }
}

