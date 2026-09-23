using Microsoft.AspNetCore.Mvc;
using LayoutParserApi.Models.Database;
using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.Interfaces;

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
        /// Busca layouts no catálogo (cache Redis com fallback ao SQL) por termo livre.
        /// </summary>
        /// <response code="200">Lista de layouts encontrados (pode ser vazia).</response>
        /// <response code="400">Falha ao buscar (ver <c>error</c>).</response>
        /// <response code="500">Falha não catalogada.</response>
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
        /// Busca um layout específico pelo ID interno do catálogo.
        /// </summary>
        /// <param name="id">ID numérico do layout (não confundir com <c>LayoutGuid</c>).</param>
        /// <response code="200">Layout encontrado.</response>
        /// <response code="404">Nenhum layout com este ID.</response>
        /// <response code="500">Falha não catalogada.</response>
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
        /// Lista até 300 layouts sem filtro — atalho para "todos os layouts do catálogo"
        /// (nome do endpoint é histórico; não filtra por MQSeries/NFe apesar do nome).
        /// </summary>
        /// <response code="200">Lista de layouts (até 300).</response>
        /// <response code="400">Falha ao buscar.</response>
        /// <response code="500">Falha não catalogada.</response>
        [HttpGet("mqseries-nfe")]
        public async Task<IActionResult> GetAllLayouts()
        {
            try
            {
                _logger.LogInformation("Buscando todos os layouts");

                var request = new LayoutSearchRequest
                {
                    SearchTerm = "", // String vazia = buscar todos os layouts (sem filtro WHERE)
                    MaxResults = 300 // TOP (300) conforme especificado
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
        /// Força a releitura do SQL Server e repovoa o cache Redis de layouts (invalida o cache atual).
        /// </summary>
        /// <response code="200">Cache atualizado.</response>
        /// <response code="500">Falha ao acessar SQL/Redis.</response>
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
        /// Limpa o cache Redis de layouts (sem repopular — próxima leitura cai para o SQL).
        /// </summary>
        /// <response code="200">Cache limpo.</response>
        /// <response code="500">Falha ao acessar Redis.</response>
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
        /// Diagnóstico: descriptografa um conteúdo Sysmiddle (Base64 em JSON) via <c>LayoutParserDecrypt.exe</c>, sem persistir nada.
        /// </summary>
        /// <response code="200">Descriptografado com sucesso.</response>
        /// <response code="400"><c>EncryptedContent</c> ausente.</response>
        /// <response code="500">Falha não catalogada.</response>
        /// <response code="503">O <c>.exe</c> de descriptografia está indisponível/falhou/deu timeout — nunca retorna a cifra como se fosse texto claro.</response>
        [HttpPost("test-decryption")]
        public async Task<IActionResult> TestDecryption([FromBody] TestDecryptionRequest request)
        {
            try
            {
                _logger.LogInformation("Testando descriptografia de conteúdo");

                if (string.IsNullOrEmpty(request.EncryptedContent))
                {
                    return BadRequest(new { error = "Conteúdo criptografado é obrigatório" });
                }

                var decryptedContent = await _decryptionService.DecryptContentAsync(request.EncryptedContent);

                return Ok(new
                {
                    success = true,
                    originalSize = request.EncryptedContent.Length,
                    decryptedSize = decryptedContent.Length,
                    decryptedContent = decryptedContent,
                    timestamp = DateTime.UtcNow
                });
            }
            catch (DecryptionException ex)
            {
                // ✅ P1.1: a descriptografia NÃO ocorreu (executável ausente/falhou/timeout) →
                // 503, nunca 200 com a cifra ecoada como se fosse texto claro.
                _logger.LogError(ex, "Descriptografia nao ocorreu no teste de descriptografia");
                return StatusCode(503, new
                {
                    success = false,
                    error = "Descriptografia indisponivel: " + ex.Message
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
        /// Mesmo diagnóstico de <see cref="TestDecryption"/>, mas recebendo o Base64 puro no corpo
        /// (<c>text/plain</c>/<c>application/octet-stream</c>) em vez de envelope JSON.
        /// </summary>
        /// <response code="200">Descriptografado com sucesso.</response>
        /// <response code="400">Corpo vazio.</response>
        /// <response code="500">Falha não catalogada.</response>
        /// <response code="503">Descriptografia indisponível — mesmo motivo de <see cref="TestDecryption"/>.</response>
        [HttpPost("test-decryption-raw")]
        [Consumes("text/plain", "application/octet-stream")]
        public async Task<IActionResult> TestDecryptionRaw([FromBody] string encryptedContent)
        {
            try
            {
                _logger.LogInformation("Testando descriptografia (raw)");

                if (string.IsNullOrWhiteSpace(encryptedContent))
                {
                    return BadRequest(new { error = "Conteúdo criptografado é obrigatório" });
                }

                var trimmed = encryptedContent.Trim();
                var decryptedContent = await _decryptionService.DecryptContentAsync(trimmed);

                return Ok(new
                {
                    success = true,
                    originalSize = trimmed.Length,
                    decryptedSize = decryptedContent.Length,
                    decryptedContent = decryptedContent,
                    timestamp = DateTime.UtcNow
                });
            }
            catch (DecryptionException ex)
            {
                // ✅ P1.1: descriptografia não ocorreu → 503, nunca 200 com a cifra ecoada.
                _logger.LogError(ex, "Descriptografia nao ocorreu no teste de descriptografia (raw)");
                return StatusCode(503, new
                {
                    success = false,
                    error = "Descriptografia indisponivel: " + ex.Message
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

    /// <summary>Requisição de diagnóstico de descriptografia.</summary>
    public class TestDecryptionRequest
    {
        /// <summary>Conteúdo cifrado Sysmiddle, em Base64.</summary>
        public string EncryptedContent { get; set; } = "";
    }
}
