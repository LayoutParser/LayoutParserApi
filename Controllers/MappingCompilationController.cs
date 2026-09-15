using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Models.Entities.Identity;
using LayoutParserApi.Services.Fiscal;
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

    /// <summary>Corpo do <c>PATCH .../mapping-drafts/{draftId}/artifacts/{engine}</c> (issue #381).</summary>
    public sealed class UpdateArtifactRequest
    {
        public string? Content { get; set; }
        public string? Justification { get; set; }
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
        private readonly IFiscalProfileResolver _fiscalProfileResolver;
        private readonly ICurrentUser _currentUser;
        private readonly ILogger<MappingCompilationController> _logger;

        public MappingCompilationController(
            IMappingDraftStore draftStore,
            IMappingReleaseStore releaseStore,
            IMappingCompileService compileService,
            IMappingTestRunService testRunService,
            IFiscalProfileResolver fiscalProfileResolver,
            ICurrentUser currentUser,
            ILogger<MappingCompilationController> logger)
        {
            _draftStore = draftStore;
            _releaseStore = releaseStore;
            _compileService = compileService;
            _testRunService = testRunService;
            _fiscalProfileResolver = fiscalProfileResolver;
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

            return Ok(ToReleaseResponse(release, _fiscalProfileResolver));
        }

        // engine="sysmiddle" nunca é aceito aqui: MappingEngineGuardFilter só enxerga query/body, não
        // este segmento de rota — a checagem explícita abaixo cobre o que o filtro de classe não vê.
        private static readonly IReadOnlyCollection<string> AllowedArtifactEngines = new[] { "tcl", "xslt" };

        /// <summary>
        /// Editor manual de artefato TCL/XSLT (issue #381 sub-fases 5b/5c, ADR
        /// <c>adr-edicao-manual-artefato-versionamento-2026-09-10.md</c>). Nunca sobrescreve a
        /// release-base — cria uma <see cref="MappingRelease"/> derivada com
        /// <c>ArtifactSource=manual_edit</c>. Concorrência otimista via <c>If-Match</c> (mesmo
        /// vocabulário 428/400/412 do <c>PATCH .../rules/{ruleId}</c>), mas o <c>200</c> aqui MUTA um
        /// recurso novo em vez de editar in-place (ADR §2.3).
        /// </summary>
        /// <remarks>
        /// RBAC (ADR §2.4): exige papel <c>mapper</c>/<c>fiscal_admin</c>/<c>owner</c> no workspace da
        /// rota — maior impacto que a edição de regra (produz release candidata). Sem membership → 404;
        /// papel insuficiente → 403. <c>engine=sysmiddle</c> ou fora de <c>{tcl,xslt}</c> → 422;
        /// <c>content</c>/<c>justification</c> ausentes → 422; sintaxe inválida (5c) → 422; sem release
        /// compilada do draft/engine para servir de base → 422; <c>If-Match</c> ausente → 428; não-base64
        /// → 400; divergente do hash atual → 412 com <c>current</c>. <b>Sem 401</b> (identidade vem do BFF).
        /// </remarks>
        [HttpPatch("mapping-drafts/{draftId:guid}/artifacts/{engine}")]
        [RequireWorkspaceRole(WorkspaceRole.Mapper, WorkspaceRole.FiscalAdmin, WorkspaceRole.Owner)]
        public async Task<IActionResult> UpdateArtifact(Guid workspaceId, Guid draftId, string engine, [FromBody] UpdateArtifactRequest request, CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid userId)
                return NotFound();

            var normalizedEngine = engine?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(normalizedEngine) || !AllowedArtifactEngines.Contains(normalizedEngine))
                return UnprocessableEntity(new { error = $"\"engine\" deve ser um de: {string.Join(", ", AllowedArtifactEngines)}. \"sysmiddle\" é somente leitura/explicação." });

            if (string.IsNullOrEmpty(request.Content))
                return UnprocessableEntity(new { error = "Campo \"content\" obrigatório — texto completo do artefato editado." });

            if (string.IsNullOrWhiteSpace(request.Justification))
                return UnprocessableEntity(new { error = "Campo \"justification\" obrigatório para editar um artefato manualmente." });

            if (!Request.Headers.TryGetValue("If-Match", out var ifMatchValues) || string.IsNullOrWhiteSpace(ifMatchValues.ToString()))
                return StatusCode(StatusCodes.Status428PreconditionRequired, new { error = "Header If-Match é obrigatório para editar um artefato." });

            string expectedArtifactHash;
            try
            {
                expectedArtifactHash = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(ifMatchValues.ToString().Trim('"')));
            }
            catch (FormatException)
            {
                return BadRequest(new { error = "Header If-Match inválido (esperado base64 do hash do artefato)." });
            }

            var draft = await _draftStore.GetDraftIfMemberAsync(draftId, userId, cancellationToken);
            if (draft == null || draft.WorkspaceId != workspaceId)
                return NotFound();

            // O draft só tem UM motor (Slice 3) — não existe artefato "tcl" gerado a partir de um draft
            // "xslt" (e vice-versa). Ainda não é o filtro de sysmiddle (já recusado acima); é a
            // consistência draft↔engine.
            if (!string.Equals(draft.Engine, normalizedEngine, StringComparison.OrdinalIgnoreCase))
                return UnprocessableEntity(new { error = $"O draft usa o motor \"{draft.Engine}\"; não há artefato \"{normalizedEngine}\" para editar." });

            if (!ArtifactSyntaxValidator.TryValidate(normalizedEngine, request.Content, out var syntaxError))
                return UnprocessableEntity(new { error = syntaxError });

            var correlationId = HttpContext.TraceIdentifier;
            CreateManualEditOutcome outcome;
            try
            {
                outcome = await _releaseStore.CreateManualEditArtifactReleaseAsync(
                    workspaceId, draftId, normalizedEngine, request.Content, request.Justification,
                    expectedArtifactHash, userId, correlationId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao editar artefato \"{Engine}\" do draft {DraftId}.", normalizedEngine, draftId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível salvar a edição do artefato no momento." });
            }

            return outcome.Result switch
            {
                CreateManualEditResult.NoBaseRelease => UnprocessableEntity(new
                {
                    error = "Não há release compilada para este draft/engine — compile o draft antes de editar o artefato manualmente.",
                }),
                CreateManualEditResult.Conflict => StatusCode(StatusCodes.Status412PreconditionFailed, new
                {
                    error = "O artefato foi alterado por outra operação — recarregue e tente novamente.",
                    current = outcome.CurrentArtifact,
                }),
                // ADR §4: aqui "eTag" é o hash do artefato NOVO (não o RowVersion genérico da release,
                // que é o que ToReleaseResponse usa por padrão) — é contra ele que o próximo PATCH deste
                // engine casa o If-Match, não contra o RowVersion.
                _ => Ok(ToReleaseResponse(outcome.Release!, _fiscalProfileResolver,
                    eTagOverride: Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                        outcome.Release!.Artifacts.First(a => a.Kind == normalizedEngine).Hash)))),
            };
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

        private static object ToReleaseResponse(MappingReleaseDetail release, IFiscalProfileResolver fiscalProfileResolver, string? eTagOverride = null) => new
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
            // Diff granular por regra (issue #367 / LayoutParserReact #228): mesma divergência de
            // testRunSummary.divergences, agrupada por ruleId — evita o front ter que fazer
            // divergences.filter(d => d.ruleId === x) no cliente. Não quebra o agregado existente.
            divergencesByRuleId = release.TestRunSummary == null
                ? null
                : MappingTestRunSummaryExtensions.GroupDivergencesByRule(release.TestRunSummary),
            status = release.Status,
            // Issue #381 (ADR §2.2/§4): "compiled" (default) ou "manual_edit". rulesDesynced é
            // derivado (não persistido) — o front desabilita o diff-por-regra e mostra o selo
            // "editado manualmente" quando true.
            artifactSource = release.ArtifactSource,
            derivedFromReleaseId = release.DerivedFromReleaseId,
            manualEditReason = release.ManualEditReason,
            manuallyEditedArtifactKinds = release.ManuallyEditedArtifactKinds,
            rulesDesynced = release.RulesDesynced,
            correlationId = release.CorrelationId,
            createdAt = release.CreatedAt,
            eTag = eTagOverride ?? release.ETag,
            // Issue #379 (ADR §2.6): snapshot congelado do perfil fiscal da release + resolvedXsd
            // recalculado a partir do snapshot (estável — documentType+schemaVersion congelados).
            fiscalProfile = release.FiscalProfile == null ? null : ToFiscalProfileResponse(release.FiscalProfile, fiscalProfileResolver),
        };

        private static object ToFiscalProfileResponse(FiscalProfile profile, IFiscalProfileResolver fiscalProfileResolver) => new
        {
            documentType = profile.DocumentType,
            schemaVersion = profile.SchemaVersion,
            operation = profile.Operation,
            jurisdiction = profile.Jurisdiction,
            resolvedXsd = fiscalProfileResolver.Resolve(profile.DocumentType, profile.SchemaVersion),
        };
    }
}
