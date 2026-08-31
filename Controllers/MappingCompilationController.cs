using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Services.Filters;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    public sealed class CreateTestRunRequest
    {
        public Guid ReleaseId { get; set; }
        public string? InputXml { get; set; }
        public string? ExpectedXml { get; set; }
        public string? XsdVersion { get; set; }
    }

    /// <summary>
    /// Compilação determinística (<c>MappingDraftRule[] → XSLT/TCL</c>) e Fiscal Test Lab (Slice 5 —
    /// issue #231). Isolamento por workspace fail-closed (mesmo padrão dos Slices 1-4). Todas as rotas
    /// recusam <c>engine=sysmiddle</c> via <see cref="MappingEngineGuardFilter"/> — defesa em
    /// profundidade, já que o motor real vem do <c>MappingDraft</c> (validado na criação, Slice 3).
    /// </summary>
    [ApiController]
    [Route("api/workspaces/{workspaceId:guid}")]
    [ServiceFilter(typeof(MappingEngineGuardFilter))]
    public class MappingCompilationController : ControllerBase
    {
        private readonly IMappingDraftStore _draftStore;
        private readonly IMappingReleaseStore _releaseStore;
        private readonly IMappingCompileService _compileService;
        private readonly IMappingTestRunService _testRunService;
        private readonly ICurrentUser _currentUser;
        private readonly ILogger<MappingCompilationController> _logger;

        public MappingCompilationController(
            IMappingDraftStore draftStore,
            IMappingReleaseStore releaseStore,
            IMappingCompileService compileService,
            IMappingTestRunService testRunService,
            ICurrentUser currentUser,
            ILogger<MappingCompilationController> logger)
        {
            _draftStore = draftStore;
            _releaseStore = releaseStore;
            _compileService = compileService;
            _testRunService = testRunService;
            _currentUser = currentUser;
            _logger = logger;
        }

        /// <summary>Dispara o job assíncrono de compilação — nunca bloqueia esperando a transpilação.</summary>
        [HttpPost("mapping-drafts/{draftId:guid}/compile")]
        public async Task<IActionResult> Compile(Guid workspaceId, Guid draftId, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            var draft = await _draftStore.GetDraftIfMemberAsync(draftId, userId, cancellationToken);
            if (draft == null || draft.WorkspaceId != workspaceId)
                return NotFound();

            var correlationId = HttpContext.TraceIdentifier;
            Guid jobId;
            try
            {
                jobId = await _compileService.EnqueueAsync(workspaceId, draftId, userId, correlationId, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Compilação recusada para draft {DraftId}.", draftId);
                return NotFound();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao iniciar compilação do draft {DraftId}.", draftId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível iniciar a compilação no momento." });
            }

            return AcceptedAtAction(nameof(GetCompileJob), new { workspaceId, draftId, jobId }, new { jobId, status = CompileJobStatus.Queued });
        }

        /// <summary>Status observável do job de compilação — não é fire-and-forget cego.</summary>
        [HttpGet("mapping-drafts/{draftId:guid}/compile/{jobId:guid}")]
        public async Task<IActionResult> GetCompileJob(Guid workspaceId, Guid draftId, Guid jobId, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            var draft = await _draftStore.GetDraftIfMemberAsync(draftId, userId, cancellationToken);
            if (draft == null || draft.WorkspaceId != workspaceId)
                return NotFound();

            var state = await _compileService.GetStatusAsync(jobId, cancellationToken);
            if (state == null)
                return NotFound();

            return Ok(new { jobId = state.JobId, status = state.Status, releaseId = state.ReleaseId, error = state.Error, durationMs = state.DurationMs });
        }

        /// <summary>Consulta a release compilada — artefatos, diagnósticos de compilação e resultado do Fiscal Test Lab, se já executado.</summary>
        [HttpGet("mapping-drafts/{draftId:guid}/releases/{releaseId:guid}")]
        public async Task<IActionResult> GetRelease(Guid workspaceId, Guid draftId, Guid releaseId, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            var release = await _releaseStore.GetReleaseIfMemberAsync(releaseId, userId, cancellationToken);
            if (release == null || release.WorkspaceId != workspaceId || release.DraftId != draftId)
                return NotFound();

            return Ok(ToReleaseResponse(release));
        }

        /// <summary>
        /// Dispara o job assíncrono do Fiscal Test Lab contra a release compilada — nunca bloqueia
        /// esperando a execução do XSLT/diff. <c>engine=tcl</c> não tem runner determinístico neste
        /// slice: o job conclui com <c>RequiredGatesPassed=false</c> e diagnóstico explícito (nunca
        /// finge sucesso).
        /// </summary>
        [HttpPost("mapping-drafts/{draftId:guid}/test-runs")]
        public async Task<IActionResult> CreateTestRun(Guid workspaceId, Guid draftId, [FromBody] CreateTestRunRequest request, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            if (request.ReleaseId == Guid.Empty)
                return UnprocessableEntity(new { error = "Campo \"releaseId\" obrigatório — referencia a release compilada a testar." });

            if (string.IsNullOrWhiteSpace(request.InputXml) || string.IsNullOrWhiteSpace(request.ExpectedXml))
                return UnprocessableEntity(new { error = "Campos \"inputXml\" e \"expectedXml\" obrigatórios — fixture do Fiscal Test Lab." });

            var draft = await _draftStore.GetDraftIfMemberAsync(draftId, userId, cancellationToken);
            if (draft == null || draft.WorkspaceId != workspaceId)
                return NotFound();

            var release = await _releaseStore.GetReleaseIfMemberAsync(request.ReleaseId, userId, cancellationToken);
            if (release == null || release.WorkspaceId != workspaceId || release.DraftId != draftId)
                return UnprocessableEntity(new { error = "\"releaseId\" não corresponde a uma release compilada deste draft." });

            var correlationId = HttpContext.TraceIdentifier;
            Guid jobId;
            try
            {
                jobId = await _testRunService.EnqueueAsync(
                    workspaceId, draftId, request.ReleaseId, userId, request.InputXml, request.ExpectedXml, request.XsdVersion, correlationId, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Test-run recusado para release {ReleaseId}.", request.ReleaseId);
                return UnprocessableEntity(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao iniciar test-run da release {ReleaseId}.", request.ReleaseId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível iniciar o test-run no momento." });
            }

            return AcceptedAtAction(nameof(GetTestRunJob), new { workspaceId, draftId, jobId }, new { jobId, status = TestRunJobStatus.Queued });
        }

        /// <summary>Status observável do job de test-run.</summary>
        [HttpGet("mapping-drafts/{draftId:guid}/test-runs/{jobId:guid}")]
        public async Task<IActionResult> GetTestRunJob(Guid workspaceId, Guid draftId, Guid jobId, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            var draft = await _draftStore.GetDraftIfMemberAsync(draftId, userId, cancellationToken);
            if (draft == null || draft.WorkspaceId != workspaceId)
                return NotFound();

            var state = await _testRunService.GetStatusAsync(jobId, cancellationToken);
            if (state == null)
                return NotFound();

            return Ok(new
            {
                jobId = state.JobId,
                status = state.Status,
                releaseId = state.ReleaseId,
                requiredGatesPassed = state.RequiredGatesPassed,
                error = state.Error,
                durationMs = state.DurationMs,
            });
        }

        private static object ToReleaseResponse(MappingReleaseDetail release) => new
        {
            releaseId = release.ReleaseId,
            workspaceId = release.WorkspaceId,
            draftId = release.DraftId,
            engine = release.Engine,
            artifacts = release.Artifacts,
            sourceRuleIds = release.SourceRuleIds,
            compileDiagnostics = release.CompileDiagnostics,
            rulesSnapshotHash = release.RulesSnapshotHash,
            testRunSummary = release.TestRunSummary,
            status = release.Status,
            correlationId = release.CorrelationId,
            createdAt = release.CreatedAt,
            eTag = release.ETag,
        };
    }
}
