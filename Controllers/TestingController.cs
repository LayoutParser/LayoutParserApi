using LayoutParserApi.Services.Testing;

using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    /// <summary>
    /// Suíte de testes automatizados de transformação: roda os exemplos de referência (TXT +
    /// XML esperado) contra o pipeline TCL/XSL e reporta pass/fail — usado em CI/QA manual.
    /// </summary>
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

        /// <summary>Roda a suíte completa (todos os layouts com exemplos disponíveis).</summary>
        /// <response code="200">Relatório com resultados por layout.</response>
        /// <response code="500">Falha estrutural ao rodar a suíte.</response>
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

        /// <summary>Roda os testes de apenas um layout, opcionalmente de um diretório de exemplos alternativo.</summary>
        /// <param name="request"><c>LayoutName</c> obrigatório; <c>ExamplesDirectory</c> opcional (default: pasta padrão de exemplos).</param>
        /// <response code="200">Relatório do layout.</response>
        /// <response code="500">Falha ao rodar os testes.</response>
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

    /// <summary>Requisição de teste automatizado para um único layout.</summary>
    public class TestLayoutRequest
    {
        public string LayoutName { get; set; }
        /// <summary>Diretório alternativo de exemplos (TXT + XML esperado). Se nulo, usa o diretório padrão do serviço.</summary>
        public string ExamplesDirectory { get; set; }
    }
}

