using LayoutParserApi.Models.Database;
using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.Transformation;
using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TransformationController : ControllerBase
    {
        private readonly IMapperTransformationService _transformationService;
        private readonly ICachedMapperService _cachedMapperService;
        private readonly ICachedLayoutService _cachedLayoutService;
        private readonly ILogger<TransformationController> _logger;

        public TransformationController(
            IMapperTransformationService transformationService,
            ICachedMapperService cachedMapperService,
            ICachedLayoutService cachedLayoutService,
            ILogger<TransformationController> logger)
        {
            _transformationService = transformationService;
            _cachedMapperService = cachedMapperService;
            _cachedLayoutService = cachedLayoutService;
            _logger = logger;
        }

        /// <summary>
        /// Busca layouts de destino disponíveis para transformação a partir de um layout de entrada
        /// </summary>
        [HttpGet("available-targets/{inputLayoutGuid}")]
        public async Task<IActionResult> GetAvailableTargetLayouts(string inputLayoutGuid)
        {
            try
            {
                _logger.LogInformation("Buscando layouts de destino para InputLayoutGuid: {Guid}", inputLayoutGuid);

                // Buscar mapeadores que têm este layout como entrada
                var mappers = await _cachedMapperService.GetMappersByInputLayoutGuidAsync(inputLayoutGuid);
                
                if (mappers == null || !mappers.Any())
                {
                    return Ok(new
                    {
                        success = true,
                        targets = new List<object>()
                    });
                }

                // Buscar layouts do cache
                var layoutsResponse = await _cachedLayoutService.SearchLayoutsAsync(new LayoutSearchRequest
                {
                    SearchTerm = "all"
                });

                if (!layoutsResponse.Success || layoutsResponse.Layouts == null)
                {
                    return StatusCode(500, new { error = "Não foi possível buscar layouts do cache" });
                }

                // Filtrar layouts de destino baseado nos mapeadores
                var targetLayoutGuids = mappers
                    .Select(m => m.TargetLayoutGuid ?? m.TargetLayoutGuidFromXml)
                    .Where(g => !string.IsNullOrEmpty(g))
                    .Distinct()
                    .ToList();

                var targetLayouts = layoutsResponse.Layouts
                    .Where(l => targetLayoutGuids.Contains(l.LayoutGuid.ToString() ?? ""))
                    .Select(l => new
                    {
                        id = l.Id,
                        guid = l.LayoutGuid.ToString(),
                        name = l.Name,
                        description = l.Description
                    })
                    .ToList();

                return Ok(new
                {
                    success = true,
                    targets = targetLayouts
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar layouts de destino");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Transforma texto usando mapeador (txt para xml ou xml para xml)
        /// </summary>
        [HttpPost("transform")]
        public async Task<IActionResult> Transform([FromBody] TransformRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.InputText))
                {
                    return BadRequest(new { error = "InputText é obrigatório" });
                }

                if (string.IsNullOrEmpty(request.InputLayoutGuid))
                {
                    return BadRequest(new { error = "InputLayoutGuid é obrigatório" });
                }

                if (string.IsNullOrEmpty(request.TargetLayoutGuid))
                {
                    return BadRequest(new { error = "TargetLayoutGuid é obrigatório" });
                }

                _logger.LogInformation("Iniciando transformação: InputGuid={InputGuid}, TargetGuid={TargetGuid}",
                    request.InputLayoutGuid, request.TargetLayoutGuid);

                var result = await _transformationService.TransformAsync(
                    request.InputText,
                    request.InputLayoutGuid,
                    request.TargetLayoutGuid,
                    request.MapperXml);

                if (!result.Success)
                {
                    return BadRequest(new
                    {
                        success = false,
                        errors = result.Errors,
                        warnings = result.Warnings
                    });
                }

                return Ok(new
                {
                    success = true,
                    intermediateXml = result.IntermediateXml,
                    finalXml = result.FinalXml,
                    warnings = result.Warnings
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro durante transformação");
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }

    public class TransformRequest
    {
        public string InputText { get; set; }
        public string InputLayoutGuid { get; set; }
        public string TargetLayoutGuid { get; set; }
        public string MapperXml { get; set; }
    }
}
