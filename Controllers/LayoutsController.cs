using LayoutParserApi.Models.Database;
using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Generation;
using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.Filters;
using LayoutParserApi.Services.Generation.Implementations;
using LayoutParserApi.Services.Generation.Interfaces;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Transformation.LowCode;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Controllers
{
    /// <summary>
    /// Operações de catálogo sobre um layout específico. Hoje só a geração de documento de
    /// exemplo (issue #355) — não reaproveita <c>DataGenerationController</c> porque aquele gira
    /// em torno de <c>ExcelDataContext</c> (planilha do analista como fonte), cenário diferente
    /// de "gerar a partir só do layout+mapper já cadastrados".
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [ServiceFilter(typeof(AuditActionFilter))]
    public class LayoutsController : ControllerBase
    {
        private readonly ICachedLayoutService _cachedLayoutService;
        private readonly MapperDatabaseService _mapperDb;
        private readonly ISyntheticDataGeneratorService _dataGenerator;
        private readonly LowCodeRunnerOptions _lowCodeOpt;
        private readonly ILogger<LayoutsController> _logger;

        public LayoutsController(
            ICachedLayoutService cachedLayoutService,
            MapperDatabaseService mapperDb,
            ISyntheticDataGeneratorService dataGenerator,
            IOptions<LowCodeRunnerOptions> lowCodeOptions,
            ILogger<LayoutsController> logger)
        {
            _cachedLayoutService = cachedLayoutService;
            _mapperDb = mapperDb;
            _dataGenerator = dataGenerator;
            _lowCodeOpt = lowCodeOptions.Value;
            _logger = logger;
        }

        /// <summary>
        /// Gera um documento de exemplo sintético a partir de um layout <c>TextPositional</c> que
        /// já tem mapper (TCL/XSL/XSLT) vinculado — pré-condição obrigatória (correção do dono no
        /// ADR docs/architecture/adr-geracao-documento-exemplo-2026-09-09.md): o exemplo serve
        /// para alimentar/testar um mapper existente, nunca "existe sozinho".
        /// </summary>
        /// <param name="layoutGuid">GUID do layout (com ou sem prefixo <c>LAY_</c>).</param>
        /// <param name="request">Quantidade de registros e semente (semente ainda não suportada — ver aviso no response).</param>
        /// <response code="200">Documento gerado, com <c>warnings</c> honestos sobre a qualidade do dado.</response>
        /// <response code="400"><c>layoutGuid</c> não corresponde a nenhum layout conhecido.</response>
        /// <response code="404">Layout existe, mas não há mapper (TCL/XSL/XSLT) vinculado — geração de exemplo requer mapeamento existente.</response>
        /// <response code="501">Layout do tipo <c>Xml</c> — cobertura fica para a issue #356 (parser de árvore ainda não existe).</response>
        // Issue #32/#355: mesma classe de privilégio de DataGenerationController (geração de dado
        // sintético é operação restrita) — admin.
        [Authorize(Roles = "admin")]
        [HttpPost("{layoutGuid}/generate-sample")]
        public async Task<IActionResult> GenerateSample(string layoutGuid, [FromBody] GenerateSampleRequest? request)
        {
            if (string.IsNullOrWhiteSpace(layoutGuid))
                return BadRequest(new { error = "layoutGuid é obrigatório" });

            request ??= new GenerateSampleRequest();
            var numberOfRecords = request.NumberOfRecords > 0 ? request.NumberOfRecords : 1;

            // ✅ 400 — layoutGuid não corresponde a nenhum layout conhecido no catálogo.
            LayoutRecord? layoutRecord;
            try
            {
                layoutRecord = await _cachedLayoutService.GetLayoutByGuidAsync(layoutGuid);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha de infraestrutura ao resolver layout {LayoutGuid} para generate-sample", layoutGuid);
                return StatusCode(500, new { error = "Falha de infraestrutura ao consultar o catálogo de layouts" });
            }

            if (layoutRecord == null || string.IsNullOrWhiteSpace(layoutRecord.DecryptedContent))
                return BadRequest(new { error = $"Layout '{layoutGuid}' não encontrado no catálogo" });

            // ⚠️ Landmine já registrada (lowcode-allowedpackageguids-empty-in-null-2026-08-15):
            // AllowedPackageGuids vazio faz a query de mapper virar IN (NULL), gerando
            // falso-negativo de "sem mapper". Logamos o alerta antes de tratar null como
            // definitivo — não bloqueia a resposta (o host pode legitimamente não ter mapper).
            if (_lowCodeOpt.AllowedPackageGuids is null || _lowCodeOpt.AllowedPackageGuids.Count == 0)
            {
                _logger.LogWarning(
                    "LowCode:AllowedPackageGuids esta vazio ao resolver mapper para generate-sample (layout={LayoutGuid}) — " +
                    "um 404 aqui pode ser falso-negativo de configuracao, nao ausencia real de mapper.",
                    layoutGuid);
            }

            // ✅ 404 — pré-condição obrigatória: layout existe, mas sem mapper vinculado.
            var mapper = await _mapperDb.GetBestMapperForLayoutGuidAsync(layoutGuid, _lowCodeOpt.ProjectId, _lowCodeOpt.AllowedPackageGuids);
            if (mapper == null)
            {
                return NotFound(new
                {
                    error = "Nenhum mapper vinculado a este layout. Geração de exemplo requer mapeamento existente " +
                             "(ver docs/architecture/adr-geracao-documento-exemplo-2026-09-09.md)."
                });
            }

            Layout layout;
            try
            {
                layout = XmlLayoutLoader.LoadLayoutFromXmlString(layoutRecord.DecryptedContent);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao parsear XML do layout {LayoutGuid} para generate-sample", layoutGuid);
                return StatusCode(500, new { error = "Falha ao interpretar o XML do layout" });
            }

            // Escopo desta issue: só TextPositional. Xml fica para a #356 (parser de árvore ainda
            // não existe no repo C# — ver ADR, Decisão 4).
            if (!string.Equals(layout.LayoutType, "TextPositional", StringComparison.OrdinalIgnoreCase))
            {
                return StatusCode(StatusCodes.Status501NotImplemented, new
                {
                    error = $"Geração de exemplo para layout tipo '{layout.LayoutType}' ainda não implementada. " +
                             "Cobertura de layout Xml é escopo da issue #356 (parser de árvore em C#, ainda não existe)."
                });
            }

            var warnings = new List<string>
            {
                "CPF/CNPJ gerados têm dígito verificador matematicamente válido, mas não correspondem a pessoa/empresa real.",
                "Campos não têm correlação semântica entre si (datas, valores, CFOP etc. são gerados independentemente) — não usar como dado fiscalmente coerente.",
                "Este exemplo não é injetado automaticamente em nenhum pipeline de aprendizado/medição — requer revisão humana antes de qualquer uso em RepairOrchestrator/RepairBatchRunner."
            };

            if (request.Seed.HasValue)
                warnings.Add("O parâmetro 'seed' foi ignorado: o gerador de dados sintéticos atual não suporta geração determinística por semente.");

            var syntheticRequest = new Models.Generation.SyntheticDataRequest
            {
                Layout = layout,
                NumberOfRecords = numberOfRecords,
                UseAI = false
            };

            var result = await _dataGenerator.GenerateSyntheticDataAsync(syntheticRequest);
            if (!result.Success)
            {
                _logger.LogWarning("Falha ao gerar exemplo sintético para layout {LayoutGuid}: {Error}", layoutGuid, result.ErrorMessage);
                return StatusCode(500, new { error = result.ErrorMessage ?? "Falha na geração do documento de exemplo" });
            }

            return Ok(new GenerateSampleResponse
            {
                GeneratedDocument = string.Join("\n", result.GeneratedLines),
                Format = "positional",
                Warnings = warnings
            });
        }
    }
}
