using LayoutParserApi.Models.Entities;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Mvc;

using System.Text;

namespace LayoutParserApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DataGeneratorController : ControllerBase
    {
        private readonly IIADataGeneratorService _dataGenerator;
        private readonly ILayoutParserService _layoutParser;
        private readonly ILogger<DataGeneratorController> _logger;

        public DataGeneratorController(
            IIADataGeneratorService dataGenerator,
            ILayoutParserService layoutParser,
            ILogger<DataGeneratorController> logger)
        {
            _dataGenerator = dataGenerator;
            _layoutParser = layoutParser;
            _logger = logger;
        }

        [HttpPost("generate-from-layout")]
        public async Task<IActionResult> GenerateFromLayout(IFormFile layoutFile, int records = 10, string sampleData = null)
        {
            if (layoutFile == null)
                return BadRequest("Arquivo de layout é obrigatório");

            try
            {
                using var layoutStream = layoutFile.OpenReadStream();
                var layout = await LoadLayoutAsync(layoutStream);

                var result = await _dataGenerator.GenerateSyntheticDataAsync(layout, records, sampleData);

                if (!result.Success)
                    return BadRequest(result.ErrorMessage);

                return Ok(new
                {
                    success = true,
                    recordsGenerated = result.TotalRecords,
                    generationTime = result.GenerationTime,
                    data = result.GeneratedLines,
                    preview = result.GeneratedLines.Take(5)
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro na geração de dados");
                return StatusCode(500, $"Erro: {ex.Message}");
            }
        }

        [HttpPost("generate-from-sample")]
        public async Task<IActionResult> GenerateFromSample(IFormFile layoutFile, IFormFile sampleFile, int records = 10)
        {
            if (layoutFile == null || sampleFile == null)
                return BadRequest("Layout e arquivo de exemplo são obrigatórios");

            try
            {
                using var sampleStream = sampleFile.OpenReadStream();
                using var reader = new StreamReader(sampleStream);
                var sampleData = await reader.ReadToEndAsync();

                using var layoutStream = layoutFile.OpenReadStream();
                var layout = await LoadLayoutAsync(layoutStream);

                var result = await _dataGenerator.GenerateSyntheticDataAsync(layout, records, sampleData);

                return Ok(new
                {
                    success = result.Success,
                    recordsGenerated = result.TotalRecords,
                    data = result.GeneratedLines
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Erro: {ex.Message}");
            }
        }

        private async Task<Layout> LoadLayoutAsync(Stream layoutStream)
        {
            layoutStream.Position = 0;
            using var reader = new StreamReader(layoutStream, Encoding.UTF8, true);
            string xmlContent = await reader.ReadToEndAsync();

            return new Layout { Name = "Layout", Elements = new List<LineElement>() };
        }
    }
}