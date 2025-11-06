using LayoutParserApi.Models.Entities;
using LayoutParserApi.Services.XmlAnalysis;
using LayoutParserApi.Services.Generation.Implementations;
using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class XmlAnalysisController : ControllerBase
    {
        private readonly XmlAnalysisService _xmlAnalysisService;
        private readonly ILogger<XmlAnalysisController> _logger;

        public XmlAnalysisController(
            XmlAnalysisService xmlAnalysisService,
            ILogger<XmlAnalysisController> logger)
        {
            _xmlAnalysisService = xmlAnalysisService;
            _logger = logger;
        }

        /// <summary>
        /// Analisa e valida um arquivo XML
        /// </summary>
        [HttpPost("analyze")]
        public async Task<IActionResult> AnalyzeXml([FromBody] XmlAnalysisRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.XmlContent))
            {
                return BadRequest("Conteúdo XML é obrigatório");
            }

            try
            {
                _logger.LogInformation("Iniciando análise XML");

                // Carregar layout se fornecido
                Layout layout = null;
                if (!string.IsNullOrEmpty(request.LayoutXml))
                {
                    using var layoutStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(request.LayoutXml));
                    layout = await XmlLayoutLoader.LoadLayoutFromXmlAsync(layoutStream);
                }

                // Analisar XML
                var result = await _xmlAnalysisService.AnalyzeXmlAsync(request.XmlContent, layout);

                return Ok(new
                {
                    success = result.Success,
                    errors = result.Errors,
                    warnings = result.Warnings,
                    totalElements = result.TotalElements,
                    totalAttributes = result.TotalAttributes,
                    depth = result.Depth,
                    validationDetails = result.ValidationDetails
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao analisar XML");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Valida XML enviado como arquivo
        /// </summary>
        [HttpPost("validate-file")]
        public async Task<IActionResult> ValidateXmlFile(IFormFile xmlFile, IFormFile layoutFile = null)
        {
            if (xmlFile == null)
                return BadRequest("Arquivo XML é obrigatório");

            if (Path.GetExtension(xmlFile.FileName).ToLower() != ".xml")
                return BadRequest("O arquivo deve ser XML");

            try
            {
                string xmlContent;
                using (var reader = new StreamReader(xmlFile.OpenReadStream()))
                {
                    xmlContent = await reader.ReadToEndAsync();
                }

                Layout layout = null;
                if (layoutFile != null)
                {
                    using var layoutStream = layoutFile.OpenReadStream();
                    layout = await XmlLayoutLoader.LoadLayoutFromXmlAsync(layoutStream);
                }

                var result = await _xmlAnalysisService.AnalyzeXmlAsync(xmlContent, layout);

                return Ok(new
                {
                    success = result.Success,
                    errors = result.Errors,
                    warnings = result.Warnings,
                    totalElements = result.TotalElements,
                    totalAttributes = result.TotalAttributes,
                    depth = result.Depth,
                    validationDetails = result.ValidationDetails
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao validar arquivo XML");
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }

    public class XmlAnalysisRequest
    {
        public string XmlContent { get; set; }
        public string LayoutXml { get; set; }
    }
}

