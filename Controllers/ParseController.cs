using LayoutParserApi.Models.Entities;
using LayoutParserApi.Services.Filters;
using LayoutParserApi.Services.Parsing.Interfaces;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ParseController : ControllerBase
    {
        private readonly ILayoutParserService _parserService;
        private readonly ILogger<ParseController> _logger;
        private readonly ILayoutDetector _layoutDetector;

        public ParseController(ILayoutParserService parserService, ILogger<ParseController> logger, ILayoutDetector layoutDetector)
        {
            _parserService = parserService;
            _logger = logger;
            _layoutDetector = layoutDetector;
        }

        [ServiceFilter(typeof(AuditActionFilter))]
        [HttpPost("upload")]
        public async Task<IActionResult> Upload(IFormFile layoutFile, IFormFile txtFile)
        {
            if (layoutFile == null || txtFile == null)
                return BadRequest("Layout XML e arquivo TXT são obrigatórios.");

            if (Path.GetExtension(layoutFile.FileName).ToLower() != ".xml")
                return BadRequest("O arquivo de layout deve ser XML.");

            try
            {
                using var layoutStream = layoutFile.OpenReadStream();
                using var txtStream = txtFile.OpenReadStream();

                using var reader = new StreamReader(txtStream, leaveOpen: true);
                var sample = await reader.ReadToEndAsync();
                txtStream.Position = 0;
                var detectedType = _layoutDetector.DetectType(sample);

                var result = await _parserService.ParseAsync(layoutStream, txtStream);

                var layoutReestruturado = _parserService.ReestruturarLayout(result.Layout);
                var layoutReordenado = _parserService.ReordenarSequences(layoutReestruturado);

                var flattenedLayout = new Layout
                {
                    LayoutGuid = layoutReordenado.LayoutGuid,
                    LayoutType = layoutReordenado.LayoutType,
                    Name = layoutReordenado.Name,
                    Description = layoutReordenado.Description,
                    LimitOfCaracters = layoutReordenado.LimitOfCaracters,
                    Elements = layoutReordenado.Elements
                };

                if (!result.Success)
                    return BadRequest(result.ErrorMessage);

                var documentStructure = _parserService.BuildDocumentStructure(result);

                return Ok(new
                {
                    success = true,
                    detectedType,
                    layout = flattenedLayout,
                    fields = result.ParsedFields,
                    text = result.RawText,
                    summary = result.Summary,
                    documentStructure = documentStructure
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro durante o parsing do XML");
                return StatusCode(500, $"Erro interno: {ex.Message}");
            }
        }
    }
}