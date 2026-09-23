using LayoutParserApi.Services.Fiscal;

using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    /// <summary>
    /// Catálogo de exemplos reais de transformação TCL/XSL da Neogrid — corpus de referência/oráculo
    /// usado como material de apoio (ex.: comparação manual, treinamento), **não** releases compilados
    /// pelo pipeline nem dado de cliente. Deliberadamente fora do domínio <c>MappingRelease</c>/
    /// <c>MappingDraft</c> (sem <c>workspaceId</c>, sem <c>ArtifactSource</c>) para não misturar
    /// referência estática com governança de release real — ver decisão registrada em
    /// <c>.claude/agent-memory/lp-backend-dev/</c>.
    /// </summary>
    [ApiController]
    [Route("api/reference-examples")]
    public class ReferenceExamplesController : ControllerBase
    {
        private readonly IReferenceExampleCatalogService _catalogService;
        private readonly ILogger<ReferenceExamplesController> _logger;

        public ReferenceExamplesController(
            IReferenceExampleCatalogService catalogService,
            ILogger<ReferenceExamplesController> logger)
        {
            _catalogService = catalogService;
            _logger = logger;
        }

        /// <summary>Lista os exemplos disponíveis no corpus, opcionalmente filtrados por tipo de documento (ex.: <c>NFe</c>). Retorna lista vazia (nunca erro) se o corpus não estiver configurado/disponível.</summary>
        /// <param name="docType">Opcional. Tipo de documento (pasta de 1º nível do corpus; comparação sem diferenciar maiúsculas).</param>
        /// <param name="cancellationToken">Token de cancelamento.</param>
        /// <response code="200">Lista de exemplos (metadados, sem conteúdo): <c>id</c>, <c>docType</c>, <c>version</c>, <c>scenario</c>, <c>direction</c>, <c>tclFileName</c>, <c>xslFileName</c>. Vazia quando <c>ReferenceExamples:BasePath</c> não está configurado, a pasta não existe ou ocorre erro de leitura.</response>
        [HttpGet]
        public async Task<IActionResult> List([FromQuery] string? docType, CancellationToken cancellationToken)
        {
            try
            {
                var examples = await _catalogService.ListAsync(docType, cancellationToken);
                return Ok(examples);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao listar catálogo de exemplos de referência (docType={DocType})", docType);
                return Ok(Array.Empty<object>()); // degrada gracioso — corpus de referência não pode derrubar o front-end
            }
        }

        /// <summary>Retorna o conteúdo (TCL e/ou XSL) de um exemplo específico do catálogo.</summary>
        /// <param name="id">Identificador devolvido pela listagem (hash estável do caminho relativo).</param>
        /// <param name="cancellationToken">Token de cancelamento.</param>
        /// <response code="200"><c>{ id, tclContent?, xslContent? }</c> — cada conteúdo é omitido quando o exemplo não tem o arquivo correspondente.</response>
        /// <response code="404">Id não encontrado no corpus (também devolvido se a leitura do arquivo falhar).</response>
        [HttpGet("{id}")]
        public async Task<IActionResult> GetContent(string id, CancellationToken cancellationToken)
        {
            try
            {
                var content = await _catalogService.GetContentAsync(id, cancellationToken);
                if (content == null)
                    return NotFound();

                return Ok(content);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao ler conteúdo do exemplo de referência {Id}", id);
                return NotFound();
            }
        }
    }
}
