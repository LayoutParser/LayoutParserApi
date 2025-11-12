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

                // Buscar todos os mapeadores primeiro para debug
                var allMappers = await _cachedMapperService.GetAllMappersAsync();
                _logger.LogInformation("Total de mapeadores no cache: {Count}", allMappers?.Count ?? 0);
                
                if (allMappers != null && allMappers.Any())
                {
                    _logger.LogInformation("Primeiros 3 mapeadores - InputGuid: {M1}, {M2}, {M3}", 
                        allMappers[0].InputLayoutGuid ?? allMappers[0].InputLayoutGuidFromXml ?? "null",
                        allMappers.Count > 1 ? (allMappers[1].InputLayoutGuid ?? allMappers[1].InputLayoutGuidFromXml ?? "null") : "N/A",
                        allMappers.Count > 2 ? (allMappers[2].InputLayoutGuid ?? allMappers[2].InputLayoutGuidFromXml ?? "null") : "N/A");
                }

                // Buscar mapeadores que têm este layout como entrada
                var mappers = await _cachedMapperService.GetMappersByInputLayoutGuidAsync(inputLayoutGuid);
                
                _logger.LogInformation("Mapeadores encontrados para InputLayoutGuid {Guid}: {Count}", inputLayoutGuid, mappers?.Count ?? 0);
                
                if (mappers == null || !mappers.Any())
                {
                    _logger.LogWarning("Nenhum mapeador encontrado para InputLayoutGuid: {Guid}", inputLayoutGuid);
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
                    .Select(m => NormalizeLayoutGuid(m.TargetLayoutGuid ?? m.TargetLayoutGuidFromXml ?? ""))
                    .Where(g => !string.IsNullOrEmpty(g))
                    .Distinct()
                    .ToList();

                _logger.LogInformation("GUIDs de destino encontrados: {Guids}", string.Join(", ", targetLayoutGuids));

                var targetLayouts = layoutsResponse.Layouts
                    .Where(l =>
                    {
                        var layoutGuidStr = l.LayoutGuid.ToString() ?? "";
                        return targetLayoutGuids.Any(tg => GuidMatches(layoutGuidStr, tg));
                    })
                    .Select(l => new
                    {
                        id = l.Id,
                        guid = l.LayoutGuid.ToString() ?? "",
                        name = l.Name,
                        description = l.Description
                    })
                    .ToList();

                _logger.LogInformation("Layouts de destino encontrados: {Count}", targetLayouts.Count);

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

        private static string NormalizeLayoutGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid))
                return "";
            
            // Remover prefixo LAY_ se houver
            var normalized = guid;
            if (normalized.StartsWith("LAY_", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(4);
            
            // Remover espaços e converter para minúsculas para comparação
            return normalized.Trim().ToLowerInvariant();
        }

        private static bool GuidMatches(string guid1, string guid2)
        {
            if (string.IsNullOrEmpty(guid1) || string.IsNullOrEmpty(guid2))
                return false;
            
            var norm1 = NormalizeLayoutGuid(guid1);
            var norm2 = NormalizeLayoutGuid(guid2);
            
            // Comparação exata
            if (norm1 == norm2)
                return true;
            
            // Tentar comparar apenas a parte do GUID (sem prefixo)
            var guid1Only = ExtractGuidOnly(norm1);
            var guid2Only = ExtractGuidOnly(norm2);
            
            if (guid1Only == guid2Only && !string.IsNullOrEmpty(guid1Only))
                return true;
            
            // Comparação parcial (caso um tenha o prefixo e outro não)
            return norm1.Contains(norm2) || norm2.Contains(norm1);
        }

        private static string ExtractGuidOnly(string guid)
        {
            if (string.IsNullOrEmpty(guid))
                return "";
            
            // Tentar extrair apenas a parte do GUID (formato: xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx)
            var guidPattern = System.Text.RegularExpressions.Regex.Match(guid, @"([a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (guidPattern.Success)
                return guidPattern.Groups[1].Value.ToLowerInvariant();
            
            return guid;
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
