using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Services.Filters;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    public sealed class CreateDraftRequest
    {
        public Guid RevisionId { get; set; }
        public string? Engine { get; set; }
    }

    public sealed class UpdateRuleRequest
    {
        public string? Status { get; set; }
        public string? Justification { get; set; }
        public List<string>? SourceRefs { get; set; }
        public List<string>? TargetRefs { get; set; }
        public string? Operation { get; set; }
        public string? Answer { get; set; }
    }

    /// <summary>
    /// <c>MappingDraft</c> human-in-the-loop (Slice 3 — issue #230). IA propõe regras estruturadas,
    /// nunca código executável; humano aceita/edita/rejeita/responde. Isolamento por workspace
    /// fail-closed (mesmo padrão do Slice 1/2). Todas as rotas recusam <c>engine=sysmiddle</c> via
    /// <see cref="MappingEngineGuardFilter"/>.
    /// </summary>
    [ApiController]
    [Route("api/workspaces/{workspaceId:guid}")]
    [ServiceFilter(typeof(MappingEngineGuardFilter))]
    public class MappingDraftsController : ControllerBase
    {
        private static readonly IReadOnlyCollection<string> AllowedEngines = new[] { "tcl", "xslt" };

        private readonly IMappingDraftStore _store;
        private readonly IMappingSuggestionService _suggestionService;
        private readonly IIdentityWorkspaceService _identityWorkspaceService;
        private readonly ICurrentUser _currentUser;
        private readonly ILogger<MappingDraftsController> _logger;

        public MappingDraftsController(
            IMappingDraftStore store,
            IMappingSuggestionService suggestionService,
            IIdentityWorkspaceService identityWorkspaceService,
            ICurrentUser currentUser,
            ILogger<MappingDraftsController> logger)
        {
            _store = store;
            _suggestionService = suggestionService;
            _identityWorkspaceService = identityWorkspaceService;
            _currentUser = currentUser;
            _logger = logger;
        }

        /// <summary>Cria um Draft a partir de uma revisão EXATA de um pacote (Slice 2) — nunca "a mais recente" implícita.</summary>
        [HttpPost("mapping-packages/{packageId:guid}/drafts")]
        public async Task<IActionResult> CreateDraft(Guid workspaceId, Guid packageId, [FromBody] CreateDraftRequest request, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            // Motor default quando ausente: rejeitar ambiguidade (design §4) — nunca assumir tcl/xslt silenciosamente.
            if (string.IsNullOrWhiteSpace(request.Engine) || !AllowedEngines.Contains(request.Engine, StringComparer.OrdinalIgnoreCase))
                return UnprocessableEntity(new { error = $"Campo \"engine\" obrigatório, um de: {string.Join(", ", AllowedEngines)}." });

            if (request.RevisionId == Guid.Empty)
                return UnprocessableEntity(new { error = "Campo \"revisionId\" obrigatório." });

            WorkspaceSummary? membership;
            try
            {
                membership = await _identityWorkspaceService.GetWorkspaceForMemberAsync(workspaceId, userId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao verificar membership do workspace {WorkspaceId} para criação de draft.", workspaceId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível verificar o workspace no momento." });
            }

            if (membership == null)
                return NotFound();

            bool revisionBelongs;
            try
            {
                revisionBelongs = await _store.RevisionBelongsToPackageAsync(packageId, request.RevisionId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao validar revisão {RevisionId} do pacote {PackageId}.", request.RevisionId, packageId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível validar a revisão no momento." });
            }

            if (!revisionBelongs)
                return NotFound();

            MappingDraftDetail draft;
            try
            {
                draft = await _store.CreateDraftAsync(workspaceId, packageId, request.RevisionId, userId, request.Engine.ToLowerInvariant(), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao criar draft (workspace={WorkspaceId}, package={PackageId}).", workspaceId, packageId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível criar o draft no momento." });
            }

            return CreatedAtAction(nameof(GetDraft), new { workspaceId, draftId = draft.DraftId }, ToDraftResponse(draft));
        }

        /// <summary>Consulta o draft + regras atuais — só se o usuário for membro do workspace dono.</summary>
        [HttpGet("mapping-drafts/{draftId:guid}")]
        public async Task<IActionResult> GetDraft(Guid workspaceId, Guid draftId, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            MappingDraftDetail? draft;
            try
            {
                draft = await _store.GetDraftIfMemberAsync(draftId, userId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao consultar draft {DraftId}.", draftId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível consultar o draft no momento." });
            }

            if (draft == null || draft.WorkspaceId != workspaceId)
                return NotFound();

            return Ok(ToDraftResponse(draft));
        }

        /// <summary>Dispara o job assíncrono de sugestão de regras via IA — nunca bloqueia esperando a IA.</summary>
        [HttpPost("mapping-drafts/{draftId:guid}/suggestions")]
        public async Task<IActionResult> CreateSuggestionJob(Guid workspaceId, Guid draftId, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            MappingDraftDetail? draft;
            try
            {
                draft = await _store.GetDraftIfMemberAsync(draftId, userId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao consultar draft {DraftId} para disparo de sugestão.", draftId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível iniciar a sugestão no momento." });
            }

            if (draft == null || draft.WorkspaceId != workspaceId)
                return NotFound();

            var jobId = await _suggestionService.EnqueueAsync(draftId, workspaceId, draft.RevisionId, draft.Engine, cancellationToken);

            return AcceptedAtAction(nameof(GetSuggestionJob), new { workspaceId, draftId, jobId }, new { jobId, status = SuggestionJobStatus.Queued });
        }

        /// <summary>Status observável do job — não é fire-and-forget cego (spec §8: "observáveis").</summary>
        [HttpGet("mapping-drafts/{draftId:guid}/suggestions/{jobId:guid}")]
        public async Task<IActionResult> GetSuggestionJob(Guid workspaceId, Guid draftId, Guid jobId, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            // Confirma isolamento por workspace via o draft antes de expor o status do job.
            var draft = await _store.GetDraftIfMemberAsync(draftId, userId, cancellationToken);
            if (draft == null || draft.WorkspaceId != workspaceId)
                return NotFound();

            var state = await _suggestionService.GetStatusAsync(jobId, cancellationToken);
            if (state == null)
                return NotFound();

            return Ok(new { jobId = state.JobId, status = state.Status, rulesCreated = state.RulesCreated, error = state.Error });
        }

        /// <summary>Cancelamento cooperativo do job de sugestão.</summary>
        [HttpDelete("mapping-drafts/{draftId:guid}/suggestions/{jobId:guid}")]
        public async Task<IActionResult> CancelSuggestionJob(Guid workspaceId, Guid draftId, Guid jobId, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            var draft = await _store.GetDraftIfMemberAsync(draftId, userId, cancellationToken);
            if (draft == null || draft.WorkspaceId != workspaceId)
                return NotFound();

            var canceled = await _suggestionService.CancelAsync(jobId, cancellationToken);
            if (!canceled)
                return NotFound();

            return Accepted();
        }

        /// <summary>
        /// Aceita/edita/rejeita/responde uma regra. Exige <c>If-Match</c> (428 sem header, 412 se
        /// divergente do <c>ROWVERSION</c> atual) — concorrência otimista greenfield (design §3).
        /// </summary>
        [HttpPatch("mapping-drafts/{draftId:guid}/rules/{ruleId:guid}")]
        public async Task<IActionResult> UpdateRule(Guid workspaceId, Guid draftId, Guid ruleId, [FromBody] UpdateRuleRequest request, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            if (!Request.Headers.TryGetValue("If-Match", out var ifMatchValues) || string.IsNullOrWhiteSpace(ifMatchValues.ToString()))
                return StatusCode(StatusCodes.Status428PreconditionRequired, new { error = "Header If-Match é obrigatório para editar uma regra." });

            byte[] expectedRowVersion;
            try
            {
                expectedRowVersion = Convert.FromBase64String(ifMatchValues.ToString().Trim('"'));
            }
            catch (FormatException)
            {
                return BadRequest(new { error = "Header If-Match inválido (esperado base64 do ETag)." });
            }

            var newStatus = ResolveNewStatus(request);
            if (newStatus == null || !MappingDraftRuleStatus.IsValid(newStatus))
                return UnprocessableEntity(new { error = "Campo \"status\" obrigatório: accepted, edited ou rejected (ou envie \"answer\" para responder needs_input)." });

            // Justificativa obrigatória para rejected/edited (design §2), opcional para accepted.
            if ((newStatus == MappingDraftRuleStatus.Rejected || newStatus == MappingDraftRuleStatus.Edited) && string.IsNullOrWhiteSpace(request.Justification))
                return UnprocessableEntity(new { error = $"Justificativa obrigatória para status \"{newStatus}\"." });

            // Confirma isolamento por workspace ANTES de tentar o UPDATE otimista.
            var draft = await _store.GetDraftIfMemberAsync(draftId, userId, cancellationToken);
            if (draft == null || draft.WorkspaceId != workspaceId)
                return NotFound();

            UpdateRuleOutcome outcome;
            try
            {
                outcome = await _store.UpdateRuleStatusAsync(
                    draftId, ruleId, userId, expectedRowVersion, newStatus, request.Justification,
                    request.SourceRefs, request.TargetRefs, request.Operation, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao atualizar regra {RuleId} do draft {DraftId}.", ruleId, draftId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível atualizar a regra no momento." });
            }

            return outcome.Result switch
            {
                UpdateRuleResult.NotFound => NotFound(),
                UpdateRuleResult.Conflict => StatusCode(StatusCodes.Status412PreconditionFailed, new
                {
                    error = "A regra foi alterada por outra operação — recarregue e tente novamente.",
                    current = await LoadCurrentRuleAsync(draftId, ruleId, userId, cancellationToken),
                }),
                _ => Ok(ToRuleResponse(outcome.Rule!)),
            };
        }

        private async Task<object?> LoadCurrentRuleAsync(Guid draftId, Guid ruleId, Guid userId, CancellationToken cancellationToken)
        {
            var current = await _store.GetRuleIfMemberAsync(draftId, ruleId, userId, cancellationToken);
            return current == null ? null : ToRuleResponse(current);
        }

        private static string? ResolveNewStatus(UpdateRuleRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.Status))
                return request.Status!.ToLowerInvariant();

            // "answer" responde uma regra needs_input — vira "proposed" novamente para nova avaliação humana.
            if (!string.IsNullOrWhiteSpace(request.Answer))
                return MappingDraftRuleStatus.Proposed;

            return null;
        }

        private static object ToDraftResponse(MappingDraftDetail draft) => new
        {
            draftId = draft.DraftId,
            workspaceId = draft.WorkspaceId,
            packageId = draft.PackageId,
            revisionId = draft.RevisionId,
            engine = draft.Engine,
            createdAt = draft.CreatedAt,
            rules = draft.Rules.Select(ToRuleResponse),
        };

        private static object ToRuleResponse(MappingDraftRuleDetail rule) => new
        {
            ruleId = rule.RuleId,
            draftId = rule.DraftId,
            sourceRefs = rule.SourceRefs,
            targetRefs = rule.TargetRefs,
            operation = rule.Operation,
            conditions = rule.ConditionsJson,
            transformations = rule.TransformationsJson,
            cardinality = rule.Cardinality,
            evidence = rule.Evidence,
            confidence = rule.Confidence,
            status = rule.Status,
            questions = rule.OpenQuestions,
            createdAt = rule.CreatedAt,
            eTag = rule.ETag,
        };
    }
}
