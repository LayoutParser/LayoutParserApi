using Microsoft.AspNetCore.Mvc;
using LayoutParserApi.Models.Database;
using LayoutParserApi.Services.Database;

namespace LayoutParserApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class LayoutDatabaseController : ControllerBase
    {
        private readonly ICachedLayoutService _cachedLayoutService;
        private readonly IDecryptionService _decryptionService;
        private readonly ILogger<LayoutDatabaseController> _logger;

        public LayoutDatabaseController(
            ICachedLayoutService cachedLayoutService,
            IDecryptionService decryptionService,
            ILogger<LayoutDatabaseController> logger)
        {
            _cachedLayoutService = cachedLayoutService;
            _decryptionService = decryptionService;
            _logger = logger;
        }

        /// <summary>
        /// Busca layouts no banco de dados
        /// </summary>
        [HttpPost("search")]
        public async Task<IActionResult> SearchLayouts([FromBody] LayoutSearchRequest request)
        {
            try
            {
                _logger.LogInformation("Buscando layouts com termo: {SearchTerm}", request.SearchTerm);

                var response = await _cachedLayoutService.SearchLayoutsAsync(request);
                
                if (response.Success)
                {
                    return Ok(response);
                }
                else
                {
                    return BadRequest(new { error = response.ErrorMessage });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar layouts");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Busca layout específico por ID
        /// </summary>
        [HttpGet("{id}")]
        public async Task<IActionResult> GetLayoutById(int id)
        {
            try
            {
                _logger.LogInformation("Buscando layout por ID: {Id}", id);

                var layout = await _cachedLayoutService.GetLayoutByIdAsync(id);
                
                if (layout != null)
                {
                    return Ok(layout);
                }
                else
                {
                    return NotFound(new { error = "Layout não encontrado" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar layout por ID: {Id}", id);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Busca todos os layouts (endpoint simplificado)
        /// </summary>
        [HttpGet("mqseries-nfe")]
        public async Task<IActionResult> GetAllLayouts()
        {
            try
            {
                _logger.LogInformation("Buscando todos os layouts");

                var request = new LayoutSearchRequest
                {
                    SearchTerm = "", // String vazia = buscar todos os layouts (sem filtro WHERE)
                    MaxResults = 1000
                };

                var response = await _cachedLayoutService.SearchLayoutsAsync(request);
                
                if (response.Success)
                {
                    return Ok(response);
                }
                else
                {
                    return BadRequest(new { error = response.ErrorMessage });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar layouts");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Atualiza o cache com dados do banco
        /// </summary>
        [HttpPost("refresh-cache")]
        public async Task<IActionResult> RefreshCache()
        {
            try
            {
                _logger.LogInformation("Iniciando atualização do cache");

                await _cachedLayoutService.RefreshCacheFromDatabaseAsync();

                return Ok(new { 
                    success = true, 
                    message = "Cache atualizado com sucesso",
                    timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao atualizar cache");
                return StatusCode(500, new { 
                    success = false, 
                    error = ex.Message 
                });
            }
        }

        /// <summary>
        /// Limpa o cache Redis
        /// </summary>
        [HttpPost("clear-cache")]
        public async Task<IActionResult> ClearCache()
        {
            try
            {
                _logger.LogInformation("Limpando cache Redis");

                await _cachedLayoutService.ClearCacheAsync();

                return Ok(new { 
                    success = true, 
                    message = "Cache limpo com sucesso",
                    timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao limpar cache");
                return StatusCode(500, new { 
                    success = false, 
                    error = ex.Message 
                });
            }
        }

        /// <summary>
        /// Testa a descriptografia de conteúdo
        /// </summary>
        [HttpPost("test-decryption")]
        public IActionResult TestDecryption([FromBody] TestDecryptionRequest request)
        {
            try
            {
                _logger.LogInformation("Testando descriptografia de conteúdo");

                if (string.IsNullOrEmpty(request.EncryptedContent))
                {
                    return BadRequest(new { error = "Conteúdo criptografado é obrigatório" });
                }

                var decryptedContent = _decryptionService.DecryptContent(request.EncryptedContent);

                return Ok(new
                {
                    success = true,
                    originalSize = request.EncryptedContent.Length,
                    decryptedSize = decryptedContent.Length,
                    decryptedContent = decryptedContent,
                    timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao testar descriptografia");
                return StatusCode(500, new { 
                    success = false, 
                    error = ex.Message 
                });
            }
        }

        /// <summary>
        /// Testa a descriptografia recebendo o Base64 como texto puro (text/plain)
        /// </summary>
        [HttpPost("test-decryption-raw")]
        [Consumes("text/plain", "application/octet-stream")]
        public IActionResult TestDecryptionRaw([FromBody] string encryptedContent)
        {
            try
            {
                _logger.LogInformation("Testando descriptografia (raw)");

                if (string.IsNullOrWhiteSpace(encryptedContent))
                {
                    return BadRequest(new { error = "Conteúdo criptografado é obrigatório" });
                }

                var trimmed = encryptedContent.Trim();
                var decryptedContent = _decryptionService.DecryptContent(trimmed);

                return Ok(new
                {
                    success = true,
                    originalSize = trimmed.Length,
                    decryptedSize = decryptedContent.Length,
                    decryptedContent = decryptedContent,
                    timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao testar descriptografia (raw)");
                return StatusCode(500, new { 
                    success = false, 
                    error = ex.Message 
                });
            }
        }
    }

    public class TestDecryptionRequest
    {
        public string EncryptedContent { get; set; } = "";
    }
}
