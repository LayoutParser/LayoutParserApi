using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using LayoutParserApi.Services.XmlAnalysis;
using LayoutParserApi.Scripts;
using System.Threading.Tasks;
using System.IO;
using System.Text;

namespace LayoutParserApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TransformationController : ControllerBase
    {
        private readonly ILogger<TransformationController> _logger;
        private readonly TclGeneratorService _tclGenerator;
        private readonly XslGeneratorService _xslGenerator;

        public TransformationController(
            ILogger<TransformationController> logger,
            TclGeneratorService tclGenerator,
            XslGeneratorService xslGenerator)
        {
            _logger = logger;
            _tclGenerator = tclGenerator;
            _xslGenerator = xslGenerator;
        }

        /// <summary>
        /// Gera arquivo TCL a partir de layout XML MQSeries
        /// </summary>
        [HttpPost("generate-tcl")]
        public async Task<IActionResult> GenerateTcl([FromBody] GenerateTclRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.LayoutXmlPath))
                {
                    return BadRequest("Caminho do layout XML é obrigatório");
                }

                _logger.LogInformation("Gerando TCL a partir do layout: {Path}", request.LayoutXmlPath);

                // Usar método estático do script
                var tclContent = await GenerateTclAndXsl.GenerateTclFromLayout(request.LayoutXmlPath);

                // Salvar se outputPath fornecido
                if (!string.IsNullOrEmpty(request.OutputPath))
                {
                    await System.IO.File.WriteAllTextAsync(request.OutputPath, tclContent, Encoding.UTF8);
                    _logger.LogInformation("Arquivo TCL salvo em: {Path}", request.OutputPath);
                }

                return Ok(new
                {
                    success = true,
                    tclContent = tclContent,
                    outputPath = request.OutputPath
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao gerar TCL");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Gera arquivo XSL a partir do MAP de transformação
        /// </summary>
        [HttpPost("generate-xsl")]
        public async Task<IActionResult> GenerateXsl([FromBody] GenerateXslRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.MapXmlPath))
                {
                    return BadRequest("Caminho do MAP XML é obrigatório");
                }

                _logger.LogInformation("Gerando XSL a partir do MAP: {Path}", request.MapXmlPath);

                // Usar método estático do script
                var xslContent = await GenerateTclAndXsl.GenerateXslFromMap(request.MapXmlPath);

                // Salvar se outputPath fornecido
                if (!string.IsNullOrEmpty(request.OutputPath))
                {
                    await System.IO.File.WriteAllTextAsync(request.OutputPath, xslContent, Encoding.UTF8);
                    _logger.LogInformation("Arquivo XSL salvo em: {Path}", request.OutputPath);
                }

                return Ok(new
                {
                    success = true,
                    xslContent = xslContent,
                    outputPath = request.OutputPath
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao gerar XSL");
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }

    public class GenerateTclRequest
    {
        public string LayoutXmlPath { get; set; }
        public string OutputPath { get; set; }
    }

    public class GenerateXslRequest
    {
        public string MapXmlPath { get; set; }
        public string OutputPath { get; set; }
    }
}
