using LayoutParserApi.Models.Entities.Identity;
using LayoutParserApi.Services.Fiscal;
using LayoutParserApi.Services.Filters;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    public sealed class CreateTestSuiteRequest
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
    }

    public sealed class AddTestSuiteFixtureRequest
    {
        public string? Name { get; set; }
        public string? InputXml { get; set; }
        public string? ExpectedXml { get; set; }
        public string? XsdVersion { get; set; }
    }

    public sealed class RunTestSuiteRequest
    {
        public Guid ReleaseId { get; set; }
    }

    /// <summary>
    /// Suíte de teste versionada do Fiscal Test Lab (issue #423) — agrupa múltiplas fixtures
    /// (pares XML input/gabarito) sob um mesmo <see cref="Models.Entities.Fiscal.MappingDraft"/> e
    /// permite rodá-las em bloco contra uma release específica, com histórico de execução persistido
    /// (comparação de regressão entre versões do mapping ao longo do tempo).
    /// </summary>
    /// <remarks>
    /// Escopo desta rodada (documentado na issue #423): CRUD de suíte/fixture + execução + histórico
    /// — NÃO inclui endpoint de comparação visual entre duas execuções (front-end,
    /// LayoutParserReact#204) nem edição/remoção de fixture (só create/list). O front consome o
    /// histórico paginado (<c>GET .../runs</c>) e monta a comparação no cliente.
    /// </remarks>
    [ApiController]
    [Route("api/workspaces/{workspaceId:guid}/mapping-drafts/{draftId:guid}/test-suites")]
    [ServiceFilter(typeof(MappingEngineGuardFilter))]
    public sealed class TestSuiteController : ControllerBase
    {
        private readonly ITestSuiteStore _suiteStore;
        private readonly IMappingDraftStore _draftStore;
        private readonly ITestSuiteRunService _runService;
        private readonly ICurrentUser _currentUser;
        private readonly ILogger<TestSuiteController> _logger;

        public TestSuiteController(
            ITestSuiteStore suiteStore,
            IMappingDraftStore draftStore,
            ITestSuiteRunService runService,
            ICurrentUser currentUser,
            ILogger<TestSuiteController> logger)
        {
            _suiteStore = suiteStore;
            _draftStore = draftStore;
            _runService = runService;
            _currentUser = currentUser;
            _logger = logger;
        }

        /// <summary>Cria uma suíte vazia — fixtures são adicionadas depois via <c>POST .../fixtures</c>.</summary>
        [HttpPost]
        [RequireWorkspaceRole(WorkspaceRole.Mapper, WorkspaceRole.FiscalAdmin, WorkspaceRole.Owner)]
        public async Task<IActionResult> CreateSuite(Guid workspaceId, Guid draftId, [FromBody] CreateTestSuiteRequest request, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            if (string.IsNullOrWhiteSpace(request.Name))
                return UnprocessableEntity(new { error = "Campo \"name\" obrigatório." });

            var draft = await _draftStore.GetDraftIfMemberAsync(draftId, userId, cancellationToken);
            if (draft == null || draft.WorkspaceId != workspaceId)
                return NotFound();

            var suite = await _suiteStore.CreateSuiteAsync(workspaceId, draftId, request.Name.Trim(), request.Description, userId, cancellationToken);
            return CreatedAtAction(nameof(GetSuite), new { workspaceId, draftId, suiteId = suite.SuiteId }, ToSuiteResponse(suite));
        }

        /// <summary>Lista as suítes do draft.</summary>
        [HttpGet]
        public async Task<IActionResult> ListSuites(Guid workspaceId, Guid draftId, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            var draft = await _draftStore.GetDraftIfMemberAsync(draftId, userId, cancellationToken);
            if (draft == null || draft.WorkspaceId != workspaceId)
                return NotFound();

            var suites = await _suiteStore.ListByDraftAsync(draftId, cancellationToken);
            return Ok(suites.Select(ToSuiteResponse));
        }

        /// <summary>Consulta uma suíte.</summary>
        [HttpGet("{suiteId:guid}")]
        public async Task<IActionResult> GetSuite(Guid workspaceId, Guid draftId, Guid suiteId, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            var suite = await _suiteStore.GetSuiteIfMemberAsync(suiteId, userId, cancellationToken);
            if (suite == null || suite.WorkspaceId != workspaceId || suite.DraftId != draftId)
                return NotFound();

            return Ok(ToSuiteResponse(suite));
        }

        /// <summary>Adiciona uma fixture (par XML input/gabarito) à suíte — mesmo par que <c>POST .../test-runs</c> aceita avulso, agora nomeado e agrupado.</summary>
        [HttpPost("{suiteId:guid}/fixtures")]
        [RequireWorkspaceRole(WorkspaceRole.Mapper, WorkspaceRole.FiscalAdmin, WorkspaceRole.Owner)]
        public async Task<IActionResult> AddFixture(Guid workspaceId, Guid draftId, Guid suiteId, [FromBody] AddTestSuiteFixtureRequest request, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            if (string.IsNullOrWhiteSpace(request.Name))
                return UnprocessableEntity(new { error = "Campo \"name\" obrigatório." });

            if (string.IsNullOrWhiteSpace(request.InputXml) || string.IsNullOrWhiteSpace(request.ExpectedXml))
                return UnprocessableEntity(new { error = "Campos \"inputXml\" e \"expectedXml\" obrigatórios." });

            var suite = await _suiteStore.GetSuiteIfMemberAsync(suiteId, userId, cancellationToken);
            if (suite == null || suite.WorkspaceId != workspaceId || suite.DraftId != draftId)
                return NotFound();

            var fixture = await _suiteStore.AddFixtureAsync(suiteId, request.Name.Trim(), request.InputXml, request.ExpectedXml, request.XsdVersion, cancellationToken);
            return CreatedAtAction(nameof(ListFixtures), new { workspaceId, draftId, suiteId }, ToFixtureResponse(fixture));
        }

        /// <summary>Lista as fixtures da suíte, na ordem de execução.</summary>
        [HttpGet("{suiteId:guid}/fixtures")]
        public async Task<IActionResult> ListFixtures(Guid workspaceId, Guid draftId, Guid suiteId, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            var suite = await _suiteStore.GetSuiteIfMemberAsync(suiteId, userId, cancellationToken);
            if (suite == null || suite.WorkspaceId != workspaceId || suite.DraftId != draftId)
                return NotFound();

            var fixtures = await _suiteStore.ListFixturesAsync(suiteId, cancellationToken);
            return Ok(fixtures.Select(ToFixtureResponse));
        }

        /// <summary>
        /// Roda TODAS as fixtures da suíte contra a release informada e persiste o resultado agregado
        /// como uma nova entrada de histórico. Síncrono (ver <see cref="TestSuiteRunService"/>) — o 200
        /// já devolve o resultado completo, sem job pollável.
        /// </summary>
        [HttpPost("{suiteId:guid}/run")]
        [RequireWorkspaceRole(WorkspaceRole.Mapper, WorkspaceRole.FiscalAdmin, WorkspaceRole.Owner)]
        public async Task<IActionResult> RunSuite(Guid workspaceId, Guid draftId, Guid suiteId, [FromBody] RunTestSuiteRequest request, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            if (request.ReleaseId == Guid.Empty)
                return UnprocessableEntity(new { error = "Campo \"releaseId\" obrigatório — referencia a release compilada a testar." });

            var correlationId = HttpContext.TraceIdentifier;
            try
            {
                var run = await _runService.RunSuiteAsync(workspaceId, draftId, suiteId, request.ReleaseId, userId, correlationId, cancellationToken);
                return Ok(ToRunResponse(run));
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Execução de suíte recusada (suite={SuiteId}, release={ReleaseId}).", suiteId, request.ReleaseId);
                return UnprocessableEntity(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao executar a suíte {SuiteId} contra a release {ReleaseId}.", suiteId, request.ReleaseId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível executar a suíte no momento." });
            }
        }

        /// <summary>Histórico de execuções da suíte, mais recente primeiro — a base para comparar regressão entre versões do mapping.</summary>
        [HttpGet("{suiteId:guid}/runs")]
        public async Task<IActionResult> ListRuns(Guid workspaceId, Guid draftId, Guid suiteId, [FromQuery] int page, [FromQuery] int pageSize, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            var suite = await _suiteStore.GetSuiteIfMemberAsync(suiteId, userId, cancellationToken);
            if (suite == null || suite.WorkspaceId != workspaceId || suite.DraftId != draftId)
                return NotFound();

            var (items, totalCount) = await _suiteStore.ListRunsAsync(suiteId, page <= 0 ? 1 : page, pageSize <= 0 ? 20 : pageSize, cancellationToken);
            return Ok(new { items = items.Select(ToRunResponse), totalCount });
        }

        private static object ToSuiteResponse(TestSuiteDetail suite) => new
        {
            suiteId = suite.SuiteId,
            workspaceId = suite.WorkspaceId,
            draftId = suite.DraftId,
            name = suite.Name,
            description = suite.Description,
            createdByUserId = suite.CreatedByUserId,
            createdAt = suite.CreatedAt,
            eTag = suite.ETag,
        };

        private static object ToFixtureResponse(TestSuiteFixtureDetail fixture) => new
        {
            fixtureId = fixture.FixtureId,
            suiteId = fixture.SuiteId,
            name = fixture.Name,
            inputXml = fixture.InputXml,
            expectedXml = fixture.ExpectedXml,
            xsdVersion = fixture.XsdVersion,
            sortOrder = fixture.SortOrder,
            createdAt = fixture.CreatedAt,
        };

        private static object ToRunResponse(TestSuiteRunDetail run) => new
        {
            runId = run.RunId,
            suiteId = run.SuiteId,
            releaseId = run.ReleaseId,
            executedByUserId = run.ExecutedByUserId,
            executedAt = run.ExecutedAt,
            totalFixtures = run.TotalFixtures,
            passed = run.Passed,
            failed = run.Failed,
            requiredGatesPassed = run.RequiredGatesPassed,
            durationMs = run.DurationMs,
            fixtureResults = run.FixtureResults,
        };
    }
}
