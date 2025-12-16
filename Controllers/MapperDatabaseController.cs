using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MapperDatabaseController : ControllerBase
    {
        private readonly MapperDatabaseService _mapperDatabaseService;
        private readonly ICachedMapperService _cachedMapperService;
        private readonly ILogger<MapperDatabaseController> _logger;

        public MapperDatabaseController(
            MapperDatabaseService mapperDatabaseService,
            ICachedMapperService cachedMapperService,
            ILogger<MapperDatabaseController> logger)
        {
            _mapperDatabaseService = mapperDatabaseService;
            _cachedMapperService = cachedMapperService;
            _logger = logger;
        }

        /// <summary>
        /// Busca mapeadores para um layout específico
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
        public async Task<IActionResult> GetAllMappers([FromQuery] bool includeDecryptedContent = false)
        {
            try
            {
                _logger.LogInformation("Buscando todos os mapeadores (includeDecryptedContent: {Include})", includeDecryptedContent);

                var mappers = await _cachedMapperService.GetAllMappersAsync();

                var result = mappers.Select(m => new
                {
                    id = m.Id,
                    mapperGuid = m.MapperGuid,
                    packageGuid = m.PackageGuid,
                    name = m.Name,
                    description = m.Description,
                    isXPathMapper = m.IsXPathMapper,
                    inputLayoutGuid = m.InputLayoutGuidFromXml ?? m.InputLayoutGuid,
                    targetLayoutGuid = m.TargetLayoutGuidFromXml ?? m.TargetLayoutGuid,
                    valueContent = m.ValueContent, // Conteúdo criptografado
                    decryptedContent = includeDecryptedContent ? m.DecryptedContent : null, // Conteúdo descriptografado (apenas se solicitado)
                    inputLayoutGuidFromXml = m.InputLayoutGuidFromXml,
                    targetLayoutGuidFromXml = m.TargetLayoutGuidFromXml,
                    projectId = m.ProjectId,
                    lastUpdateDate = m.LastUpdateDate,
                    hasDecryptedContent = !string.IsNullOrEmpty(m.DecryptedContent)
                }).ToList();

                return Ok(new
                {
                    success = true,
                    count = result.Count,
                    mappers = result
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar todos os mapeadores");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Exporta um mapeador específico como JSON completo (incluindo DecryptedContent)
        /// </summary>
        [HttpGet("export/{id}")]
        public async Task<IActionResult> ExportMapper(int id)
        {
            try
            {
                _logger.LogInformation("Exportando mapeador ID: {Id}", id);

                var allMappers = await _cachedMapperService.GetAllMappersAsync();
                var mapper = allMappers?.FirstOrDefault(m => m.Id == id);

                if (mapper == null)
                {
                    return NotFound(new { error = "Mapeador não encontrado" });
                }

                // Retornar o mapeador completo com DecryptedContent
                var result = new
                {
                    id = mapper.Id,
                    mapperGuid = mapper.MapperGuid,
                    packageGuid = mapper.PackageGuid,
                    name = mapper.Name,
                    description = mapper.Description,
                    isXPathMapper = mapper.IsXPathMapper,
                    inputLayoutGuid = mapper.InputLayoutGuid,
                    targetLayoutGuid = mapper.TargetLayoutGuid,
                    valueContent = mapper.ValueContent,
                    decryptedContent = mapper.DecryptedContent, // XML completo descriptografado
                    inputLayoutGuidFromXml = mapper.InputLayoutGuidFromXml,
                    targetLayoutGuidFromXml = mapper.TargetLayoutGuidFromXml,
                    projectId = mapper.ProjectId,
                    lastUpdateDate = mapper.LastUpdateDate
                };

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao exportar mapeador ID: {Id}", id);
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

                var mappers = await _cachedMapperService.GetMappersByInputLayoutGuidAsync(inputLayoutGuid);

                if (mappers == null || !mappers.Any())
                {
                    return NotFound(new { error = "Mapeador não encontrado" });
                }

                var mapper = mappers.First();

                return Ok(new
                {
                    success = true,
                    id = mapper.Id,
                    mapperGuid = mapper.MapperGuid,
                    name = mapper.Name,
                    description = mapper.Description,
                    inputLayoutGuid = mapper.InputLayoutGuidFromXml ?? mapper.InputLayoutGuid,
                    targetLayoutGuid = mapper.TargetLayoutGuidFromXml ?? mapper.TargetLayoutGuid,
                    hasDecryptedContent = !string.IsNullOrEmpty(mapper.DecryptedContent),
                    lastUpdateDate = mapper.LastUpdateDate
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar mapeador por InputLayoutGuid: {InputLayoutGuid}", inputLayoutGuid);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Atualiza o cache de mapeadores com dados do banco
        /// </summary>
        [HttpPost("refresh-cache")]
        public async Task<IActionResult> RefreshCache()
        {
            try
            {
                _logger.LogInformation("Iniciando atualização do cache de mapeadores");

                await _cachedMapperService.RefreshCacheFromDatabaseAsync();

                return Ok(new
                {
                    success = true,
                    message = "Cache de mapeadores atualizado com sucesso",
                    timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao atualizar cache de mapeadores");
                return StatusCode(500, new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }
    }
}
