using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Models.Entities.Identity;
using LayoutParserApi.Services.Filters;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    public sealed class ApproveReleaseRequest
    {
        public string? Justification { get; set; }
    }

    public sealed class PublishReleaseRequest
    {
        public string? Environment { get; set; }
    }

    /// <summary>
    /// Governança/publicação de <see cref="MappingRelease"/> — Slice 7 (issue #94, design
    /// <c>design-slice7-governanca-piloto-fiat-2026-09-01.md</c>). Último slice da fundação: promove
    /// uma release <c>test_passed</c> a <c>approved</c>/<c>published</c> com RBAC mínimo por papel de
    /// workspace, com trilha de auditoria completa em <see cref="MappingTransition"/>.
    /// </summary>
    [ApiController]
    [Route("api/workspaces/{workspaceId:guid}/mapping-releases/{releaseId:guid}")]
    public class MappingGovernanceController : ControllerBase
    {
        private readonly IMappingReleaseStore _releaseStore;
        private readonly ICurrentUser _currentUser;
        private readonly ILogger<MappingGovernanceController> _logger;

        public MappingGovernanceController(
            IMappingReleaseStore releaseStore,
            ICurrentUser currentUser,
            ILogger<MappingGovernanceController> logger)
        {
            _releaseStore = releaseStore;
            _currentUser = currentUser;
            _logger = logger;
        }

        /// <summary>
        /// Lista releases do workspace, paginado (issue #198 do front — não havia NENHUM endpoint de
        /// descoberta: os 3 endpoints de governança abaixo exigem <c>releaseId</c> já conhecido).
        /// Rota própria (sem <c>{releaseId}</c>) via <c>~/</c> porque a rota base do controller já fixa
        /// esse segmento. Qualquer papel do workspace pode ler — só as mutações (approve/publish/
        /// rollback) exigem papel elevado.
        /// </summary>
        [HttpGet("~/api/workspaces/{workspaceId:guid}/mapping-releases")]
        [RequireWorkspaceRole(WorkspaceRole.Owner, WorkspaceRole.FiscalAdmin, WorkspaceRole.Mapper, WorkspaceRole.Reviewer, WorkspaceRole.Operator, WorkspaceRole.Viewer)]
        public async Task<IActionResult> List(Guid workspaceId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
        {
            if (page < 1)
                return BadRequest(new { error = "\"page\" deve ser >= 1." });

            if (pageSize < 1 || pageSize > 100)
                return BadRequest(new { error = "\"pageSize\" deve estar entre 1 e 100." });

            var (items, totalCount) = await _releaseStore.ListByWorkspaceAsync(workspaceId, page, pageSize, cancellationToken);

            return Ok(new
            {
                items = items.Select(ToReleaseResponse),
                page,
                pageSize,
                totalCount,
            });
        }

        /// <summary><c>test_passed → in_review → approved</c>. Bloqueado se a release estiver <c>test_failed</c> (ou qualquer status diferente de <c>test_passed</c>).</summary>
        [HttpPost("approve")]
        [RequireWorkspaceRole(WorkspaceRole.Reviewer, WorkspaceRole.FiscalAdmin)]
        public async Task<IActionResult> Approve(Guid workspaceId, Guid releaseId, [FromBody] ApproveReleaseRequest request, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            if (string.IsNullOrWhiteSpace(request.Justification))
                return UnprocessableEntity(new { error = "Campo \"justification\" é obrigatório para aprovar uma release." });

            var release = await _releaseStore.GetReleaseIfMemberAsync(releaseId, userId, cancellationToken);
            if (release == null || release.WorkspaceId != workspaceId)
                return NotFound();

            try
            {
                var approved = await _releaseStore.ApproveAsync(releaseId, userId, request.Justification, cancellationToken);
                return Ok(ToReleaseResponse(approved));
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Aprovação recusada para release {ReleaseId}.", releaseId);
                return UnprocessableEntity(new { error = ex.Message });
            }
        }

        /// <summary><c>approved → published</c>. Congela os artefatos — edição posterior exige nova revisão (novo <see cref="MappingRelease"/>).</summary>
        [HttpPost("publish")]
        [RequireWorkspaceRole(WorkspaceRole.FiscalAdmin, WorkspaceRole.Owner)]
        public async Task<IActionResult> Publish(Guid workspaceId, Guid releaseId, [FromBody] PublishReleaseRequest? request, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            var environment = string.IsNullOrWhiteSpace(request?.Environment) ? "production" : request.Environment;

            var release = await _releaseStore.GetReleaseIfMemberAsync(releaseId, userId, cancellationToken);
            if (release == null || release.WorkspaceId != workspaceId)
                return NotFound();

            try
            {
                var published = await _releaseStore.PublishAsync(releaseId, userId, environment, cancellationToken);
                return Ok(ToReleaseResponse(published));
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Publicação recusada para release {ReleaseId}.", releaseId);
                return UnprocessableEntity(new { error = ex.Message });
            }
        }

        /// <summary>Reverte a release publicada para a publicação anterior. Idempotente — repetir a chamada é no-op.</summary>
        [HttpPost("rollback")]
        [RequireWorkspaceRole(WorkspaceRole.FiscalAdmin, WorkspaceRole.Owner)]
        public async Task<IActionResult> Rollback(Guid workspaceId, Guid releaseId, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            var release = await _releaseStore.GetReleaseIfMemberAsync(releaseId, userId, cancellationToken);
            if (release == null || release.WorkspaceId != workspaceId)
                return NotFound();

            try
            {
                var rolledBack = await _releaseStore.RollbackAsync(releaseId, userId, cancellationToken);
                return Ok(ToReleaseResponse(rolledBack));
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Rollback recusado para release {ReleaseId}.", releaseId);
                return UnprocessableEntity(new { error = ex.Message });
            }
        }

        private static object ToReleaseResponse(MappingReleaseDetail release) => new
        {
            releaseId = release.ReleaseId,
            workspaceId = release.WorkspaceId,
            draftId = release.DraftId,
            engine = release.Engine,
            status = release.Status,
            environment = release.Environment,
            approvedByUserId = release.ApprovedByUserId,
            approvedAt = release.ApprovedAt,
            approvalJustification = release.ApprovalJustification,
            publishedByUserId = release.PublishedByUserId,
            publishedAt = release.PublishedAt,
            previousPublishedReleaseId = release.PreviousPublishedReleaseId,
            correlationId = release.CorrelationId,
            eTag = release.ETag,
        };
    }
}
