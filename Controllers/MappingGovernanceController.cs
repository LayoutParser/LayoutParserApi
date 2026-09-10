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
        /// <para>
        /// Filtros opcionais (issue #377), combináveis: <c>status</c> (um valor do ciclo de vida —
        /// <c>draft_compiled</c>, <c>test_passed</c>, <c>test_failed</c>, <c>in_review</c>,
        /// <c>approved</c>, <c>published</c>, <c>deprecated</c>, <c>archived</c>), <c>draftId</c> (GUID)
        /// e <c>environment</c>. Valor de <c>status</c> fora do ciclo de vida ou <c>draftId</c> que não
        /// seja GUID retornam <c>400</c>. Sem filtro, o resultado é idêntico ao comportamento anterior.
        /// </para>
        /// </summary>
        /// <remarks>
        /// RBAC: qualquer papel de membro (<c>owner</c>/<c>fiscal_admin</c>/<c>mapper</c>/
        /// <c>reviewer</c>/<c>operator</c>/<c>viewer</c>). Não-membro ou sem identidade → 404.
        /// Aceita <c>page</c>/<c>pageSize</c> e os filtros opcionais <c>status</c>/<c>draftId</c>/
        /// <c>environment</c> (issue #377).
        /// </remarks>
        /// <param name="workspaceId">Workspace dono das releases.</param>
        /// <param name="page">Página (1-based). Default 1.</param>
        /// <param name="pageSize">Tamanho da página (1..100). Default 20.</param>
        /// <param name="status">Opcional. Filtra por status do ciclo de vida da release.</param>
        /// <param name="draftId">Opcional. Filtra pelas releases geradas a partir do draft informado (GUID).</param>
        /// <param name="environment">Opcional. Filtra pelo ambiente de publicação da release.</param>
        /// <param name="cancellationToken">Token de cancelamento.</param>
        [HttpGet("~/api/workspaces/{workspaceId:guid}/mapping-releases")]
        [RequireWorkspaceRole(WorkspaceRole.Owner, WorkspaceRole.FiscalAdmin, WorkspaceRole.Mapper, WorkspaceRole.Reviewer, WorkspaceRole.Operator, WorkspaceRole.Viewer)]
        public async Task<IActionResult> List(
            Guid workspaceId,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            [FromQuery] string? status = null,
            [FromQuery] string? draftId = null,
            [FromQuery] string? environment = null,
            CancellationToken cancellationToken = default)
        {
            if (page < 1)
                return BadRequest(new { error = "\"page\" deve ser >= 1." });

            if (pageSize < 1 || pageSize > 100)
                return BadRequest(new { error = "\"pageSize\" deve estar entre 1 e 100." });

            // Valida "status" contra o ciclo de vida da release (issue #377) — valor livre não chega ao SQL.
            if (!string.IsNullOrWhiteSpace(status) && !MappingReleaseStatus.All.Contains(status))
                return BadRequest(new { error = $"\"status\" inválido. Valores aceitos: {string.Join(", ", MappingReleaseStatus.All)}." });

            // "draftId" é recebido como string para devolver 400 com mensagem PT-BR (e não o 400
            // genérico do model binder) quando não for um GUID.
            Guid? draftIdFilter = null;
            if (!string.IsNullOrWhiteSpace(draftId))
            {
                if (!Guid.TryParse(draftId, out var parsedDraftId))
                    return BadRequest(new { error = "\"draftId\" deve ser um GUID válido." });
                draftIdFilter = parsedDraftId;
            }

            var environmentFilter = string.IsNullOrWhiteSpace(environment) ? null : environment;

            var (items, totalCount) = await _releaseStore.ListByWorkspaceAsync(
                workspaceId, page, pageSize, status, draftIdFilter, environmentFilter, cancellationToken);

            return Ok(new
            {
                items = items.Select(ToReleaseResponse),
                page,
                pageSize,
                totalCount,
            });
        }

        /// <summary><c>test_passed → in_review → approved</c>. Bloqueado se a release estiver <c>test_failed</c> (ou qualquer status diferente de <c>test_passed</c>).</summary>
        /// <remarks>
        /// RBAC: exige papel <c>reviewer</c> ou <c>fiscal_admin</c> no workspace da rota. Sem
        /// membership → 404; papel insuficiente → 403. Corpo exige <c>justification</c> (422 se ausente).
        /// </remarks>
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
        /// <remarks>RBAC: exige papel <c>fiscal_admin</c> ou <c>owner</c> no workspace da rota. Sem membership → 404; papel insuficiente → 403.</remarks>
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
        /// <remarks>RBAC: exige papel <c>fiscal_admin</c> ou <c>owner</c> no workspace da rota. Sem membership → 404; papel insuficiente → 403.</remarks>
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
