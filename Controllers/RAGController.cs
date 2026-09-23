using Microsoft.AspNetCore.Mvc;
using LayoutParserApi.Services.Generation.Implementations;
using LayoutParserApi.Models.RAG;

namespace LayoutParserApi.Controllers
{
    /// <summary>
    /// Gestão do corpus RAG (exemplos TCL/XSL usados como referência pelo motor de geração/Ollama)
    /// — inspeção, recarga do disco e adição manual de exemplo.
    /// </summary>
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

        /// <summary>Estatísticas do corpus RAG carregado em memória (contagem de exemplos, etc.).</summary>
        /// <response code="200">Estatísticas.</response>
        /// <response code="500">Falha não catalogada.</response>
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

        /// <summary>Força a releitura dos exemplos em disco para dentro do índice RAG em memória.</summary>
        /// <response code="200">Recarregado.</response>
        /// <response code="500">Falha ao ler os exemplos do disco.</response>
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

        /// <summary>Adiciona manualmente um exemplo (par entrada/transformação) ao corpus RAG.</summary>
        /// <response code="200">Exemplo adicionado.</response>
        /// <response code="400"><c>FileName</c>/<c>Content</c> ausente.</response>
        /// <response code="500">Falha ao gravar o exemplo.</response>
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

        /// <summary>Busca no corpus RAG os exemplos mais relevantes (similaridade) para um layout XML — usado para montar o contexto de prompt do Ollama.</summary>
        /// <param name="request"><c>LayoutXml</c> obrigatório; <c>MaxExamples</c> opcional (default 5).</param>
        /// <response code="200">Exemplos encontrados (pode ser vazio).</response>
        /// <response code="400"><c>LayoutXml</c> ausente.</response>
        /// <response code="500">Falha não catalogada.</response>
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
}