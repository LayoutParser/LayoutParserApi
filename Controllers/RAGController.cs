using Microsoft.AspNetCore.Mvc;
using LayoutParserApi.Services.Generation.Implementations;

namespace LayoutParserApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class RAGController : ControllerBase
    {
        private readonly RAGService _ragService;
        private readonly ILogger<RAGController> _logger;

        public RAGController(RAGService ragService, ILogger<RAGController> logger)
        {
            _ragService = ragService;
            _logger = logger;
        }

        /// <summary>
        /// Obtém estatísticas do RAG
        /// </summary>
        [HttpGet("stats")]
        public IActionResult GetStats()
        {
            try
            {
                var stats = _ragService.GetStats();
                return Ok(stats);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao obter estatísticas do RAG");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Recarrega exemplos do disco
        /// </summary>
        [HttpPost("reload")]
        public IActionResult ReloadExamples()
        {
            try
            {
                _ragService.ReloadExamples();
                _logger.LogInformation("Exemplos RAG recarregados com sucesso");
                return Ok(new { message = "Exemplos recarregados com sucesso" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao recarregar exemplos RAG");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Adiciona um novo exemplo
        /// </summary>
        [HttpPost("add-example")]
        public IActionResult AddExample([FromBody] AddExampleRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.FileName) || string.IsNullOrEmpty(request.Content))
                {
                    return BadRequest(new { error = "FileName e Content são obrigatórios" });
                }

                _ragService.AddExample(request.FileName, request.Content);
                _logger.LogInformation("Exemplo adicionado: {FileName}", request.FileName);
                
                return Ok(new { message = $"Exemplo {request.FileName} adicionado com sucesso" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao adicionar exemplo");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Busca exemplos relevantes para um layout
        /// </summary>
        [HttpPost("find-relevant")]
        public IActionResult FindRelevantExamples([FromBody] FindRelevantRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.LayoutXml))
                {
                    return BadRequest(new { error = "LayoutXml é obrigatório" });
                }

                var examples = _ragService.FindRelevantExamples(request.LayoutXml, request.MaxExamples ?? 5);
                
                return Ok(new { 
                    examples = examples,
                    count = examples.Count,
                    layoutXml = request.LayoutXml
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar exemplos relevantes");
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }

    public class AddExampleRequest
    {
        public string FileName { get; set; } = "";
        public string Content { get; set; } = "";
    }

    public class FindRelevantRequest
    {
        public string LayoutXml { get; set; } = "";
        public int? MaxExamples { get; set; }
    }
}
