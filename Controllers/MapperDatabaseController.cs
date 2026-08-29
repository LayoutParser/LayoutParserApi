using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Authorization;
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
        /// Lista os mapeadores Sysmiddle registrados para um layout de origem — não retorna o
        /// XML descriptografado completo (ver <see cref="ExportMapper"/> para isso).
        /// </summary>
        /// <param name="layoutGuid">GUID do layout de origem.</param>
        /// <response code="200">Lista de mapeadores (pode ser vazia).</response>
        /// <response code="500">Falha não catalogada.</response>
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
        /// Lista todos os mapeadores em cache — incluindo o conteúdo cifrado sempre e o
        /// descriptografado só sob pedido (custo de descriptografia por item).
        /// </summary>
        /// <param name="includeDecryptedContent">Se true, inclui <c>decryptedContent</c> (XML pleno) por mapeador.</param>
        /// <response code="200">Lista de mapeadores.</response>
        /// <response code="500">Falha não catalogada.</response>
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
        /// Exporta um mapeador específico como JSON completo, sempre com <c>decryptedContent</c>
        /// (XML Sysmiddle pleno) — endpoint de inspeção/debug, não usado no fluxo de transformação.
        /// </summary>
        /// <param name="id">ID interno (não confundir com <c>mapperGuid</c>).</param>
        /// <response code="200">Mapeador completo.</response>
        /// <response code="404">ID inexistente.</response>
        /// <response code="500">Falha não catalogada.</response>
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
        /// Busca o primeiro mapeador cujo layout de <b>entrada</b> (não destino) casa com o GUID informado.
        /// </summary>
        /// <param name="inputLayoutGuid">GUID do layout de entrada do mapeador.</param>
        /// <response code="200">Mapeador encontrado.</response>
        /// <response code="404">Nenhum mapeador com este InputLayoutGuid.</response>
        /// <response code="500">Falha não catalogada.</response>
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
        /// Força a releitura do SQL Server e repovoa o cache Redis de mapeadores.
        /// </summary>
        /// <response code="200">Cache atualizado.</response>
        /// <response code="500">Falha ao acessar SQL/Redis.</response>
        // Issue #32: rebuild de cache é operação operacional, não de negócio — restrita ao
        // papel "operador" (mais permissivo que "admin", coerente com a tabela de decisão).
        [Authorize(Roles = "operador")]
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
