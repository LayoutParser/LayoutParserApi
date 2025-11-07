using Microsoft.AspNetCore.Mvc;
using LayoutParserApi.Services.Database;
using Microsoft.Extensions.Logging;

namespace LayoutParserApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MapperDatabaseController : ControllerBase
    {
        private readonly MapperDatabaseService _mapperDatabaseService;
        private readonly ILogger<MapperDatabaseController> _logger;

        public MapperDatabaseController(
            MapperDatabaseService mapperDatabaseService,
            ILogger<MapperDatabaseController> logger)
        {
            _mapperDatabaseService = mapperDatabaseService;
            _logger = logger;
        }

        /// <summary>
        /// Busca mapeadores relacionados a um layout (por InputLayoutGuid ou TargetLayoutGuid)
        /// </summary>
        [HttpGet("by-layout/{layoutGuid}")]
        public async Task<IActionResult> GetMappersByLayoutGuid(string layoutGuid)
        {
            try
            {
                _logger.LogInformation("Buscando mapeadores para layout: {LayoutGuid}", layoutGuid);

                var mappers = await _mapperDatabaseService.GetMappersByLayoutGuidAsync(layoutGuid);

                return Ok(new
                {
                    success = true,
                    layoutGuid = layoutGuid,
                    count = mappers.Count,
                    mappers = mappers.Select(m => new
                    {
                        id = m.Id,
                        mapperGuid = m.MapperGuid,
                        name = m.Name,
                        description = m.Description,
                        inputLayoutGuid = m.InputLayoutGuidFromXml ?? m.InputLayoutGuid,
                        targetLayoutGuid = m.TargetLayoutGuidFromXml ?? m.TargetLayoutGuid,
                        hasDecryptedContent = !string.IsNullOrEmpty(m.DecryptedContent),
                        lastUpdateDate = m.LastUpdateDate
                    })
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar mapeadores para layout: {LayoutGuid}", layoutGuid);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Busca todos os mapeadores
        /// </summary>
        [HttpGet("all")]
        public async Task<IActionResult> GetAllMappers()
        {
            try
            {
                _logger.LogInformation("Buscando todos os mapeadores");

                var mappers = await _mapperDatabaseService.GetAllMappersAsync();

                return Ok(new
                {
                    success = true,
                    count = mappers.Count,
                    mappers = mappers.Select(m => new
                    {
                        id = m.Id,
                        mapperGuid = m.MapperGuid,
                        name = m.Name,
                        description = m.Description,
                        inputLayoutGuid = m.InputLayoutGuidFromXml ?? m.InputLayoutGuid,
                        targetLayoutGuid = m.TargetLayoutGuidFromXml ?? m.TargetLayoutGuid,
                        hasDecryptedContent = !string.IsNullOrEmpty(m.DecryptedContent),
                        lastUpdateDate = m.LastUpdateDate
                    })
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar todos os mapeadores");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Busca mapeador por InputLayoutGuid (entrada)
        /// </summary>
        [HttpGet("by-input/{inputLayoutGuid}")]
        public async Task<IActionResult> GetMapperByInputLayoutGuid(string inputLayoutGuid)
        {
            try
            {
                _logger.LogInformation("Buscando mapeador por InputLayoutGuid: {InputLayoutGuid}", inputLayoutGuid);

                var mapper = await _mapperDatabaseService.GetMapperByInputLayoutGuidAsync(inputLayoutGuid);

                if (mapper == null)
                {
                    return NotFound(new { error = "Mapeador não encontrado" });
                }

                return Ok(new
                {
                    success = true,
                    mapper = new
                    {
                        id = mapper.Id,
                        mapperGuid = mapper.MapperGuid,
                        name = mapper.Name,
                        description = mapper.Description,
                        inputLayoutGuid = mapper.InputLayoutGuidFromXml ?? mapper.InputLayoutGuid,
                        targetLayoutGuid = mapper.TargetLayoutGuidFromXml ?? mapper.TargetLayoutGuid,
                        hasDecryptedContent = !string.IsNullOrEmpty(mapper.DecryptedContent),
                        lastUpdateDate = mapper.LastUpdateDate
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar mapeador por InputLayoutGuid: {InputLayoutGuid}", inputLayoutGuid);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Busca mapeador por TargetLayoutGuid (saída)
        /// </summary>
        [HttpGet("by-target/{targetLayoutGuid}")]
        public async Task<IActionResult> GetMapperByTargetLayoutGuid(string targetLayoutGuid)
        {
            try
            {
                _logger.LogInformation("Buscando mapeador por TargetLayoutGuid: {TargetLayoutGuid}", targetLayoutGuid);

                var mapper = await _mapperDatabaseService.GetMapperByTargetLayoutGuidAsync(targetLayoutGuid);

                if (mapper == null)
                {
                    return NotFound(new { error = "Mapeador não encontrado" });
                }

                return Ok(new
                {
                    success = true,
                    mapper = new
                    {
                        id = mapper.Id,
                        mapperGuid = mapper.MapperGuid,
                        name = mapper.Name,
                        description = mapper.Description,
                        inputLayoutGuid = mapper.InputLayoutGuidFromXml ?? mapper.InputLayoutGuid,
                        targetLayoutGuid = mapper.TargetLayoutGuidFromXml ?? mapper.TargetLayoutGuid,
                        hasDecryptedContent = !string.IsNullOrEmpty(mapper.DecryptedContent),
                        lastUpdateDate = mapper.LastUpdateDate
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar mapeador por TargetLayoutGuid: {TargetLayoutGuid}", targetLayoutGuid);
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }
}

