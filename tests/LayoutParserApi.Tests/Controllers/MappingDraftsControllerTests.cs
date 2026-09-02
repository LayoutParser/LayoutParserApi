using LayoutParserApi.Controllers;
using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Controllers
{
    /// <summary>
    /// Slice 3 (issue #230) — ETag/If-Match (428/412), isolamento cross-workspace e job de sugestão
    /// não-bloqueante. O filtro de fronteira Sysmiddle é coberto separadamente em
    /// <c>MappingEngineGuardFilterTests</c> (o filtro roda antes do controller na pipeline real).
    /// </summary>
    public class MappingDraftsControllerTests
    {
        private sealed class FakeCurrentUser : ICurrentUser
        {
            public string? Name { get; set; }
            public IReadOnlyList<string> Roles { get; set; } = Array.Empty<string>();
            public bool IsAuthenticated => Name != null;
            public Guid? UserId { get; set; }
            public bool IsInRole(string role) => false;
        }

        private sealed class FakeIdentityWorkspaceService : IIdentityWorkspaceService
        {
            public HashSet<(Guid WorkspaceId, Guid UserId)> Memberships { get; } = new();

            public Task<Guid?> ResolveOrCreateUserAsync(string provider, string? tenantOrIssuer, string subject, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task<WorkspaceMeResult> GetOrCreateMyWorkspacesAsync(Guid userId, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task<WorkspaceSummary?> GetWorkspaceForMemberAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken)
                => Task.FromResult(Memberships.Contains((workspaceId, userId))
                    ? new WorkspaceSummary(workspaceId, "Workspace", "personal", "owner", DateTimeOffset.UtcNow)
                    : null);
        }

        private sealed class FakeDraftStore : IMappingDraftStore
        {
            public Dictionary<Guid, MappingDraftDetail> Drafts { get; } = new();
            public Dictionary<(Guid DraftId, Guid RuleId), MappingDraftRuleDetail> Rules { get; } = new();
            public Guid? LastCreatedForRevision { get; private set; }

            public Task<bool> RevisionBelongsToPackageAsync(Guid packageId, Guid revisionId, CancellationToken cancellationToken)
                => Task.FromResult(true);

            public Task<IReadOnlyList<ArtifactFileRef>> GetArtifactFilesForRevisionAsync(Guid revisionId, CancellationToken cancellationToken)
                => Task.FromResult<IReadOnlyList<ArtifactFileRef>>(Array.Empty<ArtifactFileRef>());

            public Task<MappingDraftDetail> CreateDraftAsync(Guid workspaceId, Guid packageId, Guid revisionId, Guid createdByUserId, string engine, CancellationToken cancellationToken)
            {
                LastCreatedForRevision = revisionId;
                var draft = new MappingDraftDetail(Guid.NewGuid(), workspaceId, packageId, revisionId, engine, DateTimeOffset.UtcNow, Array.Empty<MappingDraftRuleDetail>());
                Drafts[draft.DraftId] = draft;
                return Task.FromResult(draft);
            }

            public Task<MappingDraftDetail?> GetDraftIfMemberAsync(Guid draftId, Guid userId, CancellationToken cancellationToken)
                => Task.FromResult(Drafts.TryGetValue(draftId, out var draft) ? draft : null);

            public Task<MappingDraftRuleDetail?> GetRuleIfMemberAsync(Guid draftId, Guid ruleId, Guid userId, CancellationToken cancellationToken)
                => Task.FromResult(Rules.TryGetValue((draftId, ruleId), out var rule) ? rule : null);

            public Task InsertProposedRulesAsync(Guid draftId, Guid jobId, IReadOnlyList<MappingDraftRuleProposal> proposals, CancellationToken cancellationToken)
                => Task.CompletedTask;

            public Func<byte[], byte[], bool>? RowVersionComparer { get; set; }

            public Task<UpdateRuleOutcome> UpdateRuleStatusAsync(
                Guid draftId, Guid ruleId, Guid userId, byte[] expectedRowVersion, string newStatus, string? justification,
                IReadOnlyList<string>? editedSourceRefs, IReadOnlyList<string>? editedTargetRefs, string? editedOperation,
                CancellationToken cancellationToken)
            {
                if (!Rules.TryGetValue((draftId, ruleId), out var rule))
                    return Task.FromResult(new UpdateRuleOutcome(UpdateRuleResult.NotFound, null));

                var currentRowVersion = Convert.FromBase64String(rule.ETag);
                if (!currentRowVersion.SequenceEqual(expectedRowVersion))
                    return Task.FromResult(new UpdateRuleOutcome(UpdateRuleResult.Conflict, null));

                var updated = rule with { Status = newStatus, ETag = Convert.ToBase64String(Guid.NewGuid().ToByteArray()[..8]) };
                Rules[(draftId, ruleId)] = updated;
                return Task.FromResult(new UpdateRuleOutcome(UpdateRuleResult.Success, updated));
            }
        }

        private sealed class FakeSuggestionService : IMappingSuggestionService
        {
            public bool BlockedUntilReleased { get; set; }
            private readonly TaskCompletionSource _gate = new();
            public Guid EnqueuedJobId { get; } = Guid.NewGuid();
            public bool WasAwaitedBeforeReturn { get; private set; }

            public async Task<Guid> EnqueueAsync(Guid draftId, Guid workspaceId, Guid revisionId, string engine, CancellationToken cancellationToken)
            {
                if (BlockedUntilReleased)
                {
                    // Simula um job "pesado": se o controller esperasse o job terminar, este teste
                    // travaria. O controller deve retornar 202 sem aguardar a IA (spec §8).
                    _ = Task.Run(async () => { await _gate.Task; });
                }
                await Task.CompletedTask;
                WasAwaitedBeforeReturn = true;
                return EnqueuedJobId;
            }

            public void Release() => _gate.TrySetResult();

            public Task<SuggestionJobState?> GetStatusAsync(Guid jobId, CancellationToken cancellationToken)
                => Task.FromResult<SuggestionJobState?>(new SuggestionJobState { JobId = jobId, Status = SuggestionJobStatus.Running });

            public Task<bool> CancelAsync(Guid jobId, CancellationToken cancellationToken)
                => Task.FromResult(true);
        }

        private static MappingDraftsController BuildController(
            FakeDraftStore store, FakeSuggestionService suggestionService, FakeIdentityWorkspaceService identityService, FakeCurrentUser user)
        {
            var controller = new MappingDraftsController(store, suggestionService, identityService, user, NullLogger<MappingDraftsController>.Instance);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            return controller;
        }

        private static MappingDraftRuleDetail NewRule(Guid draftId, string status = MappingDraftRuleStatus.Proposed) => new(
            Guid.NewGuid(), draftId, new[] { "source:X" }, new[] { "xsd:Y" }, "copy", "[]", "[]", "1:1",
            Array.Empty<MappingDraftRuleEvidence>(), "high", status, Array.Empty<string>(), DateTimeOffset.UtcNow,
            Convert.ToBase64String(Guid.NewGuid().ToByteArray()[..8]));

        [Fact]
        public async Task Patch_sem_IfMatch_retorna_428()
        {
            var store = new FakeDraftStore();
            var user = Guid.NewGuid();
            var workspaceId = Guid.NewGuid();
            var identityService = new FakeIdentityWorkspaceService();
            identityService.Memberships.Add((workspaceId, user));
            var draft = new MappingDraftDetail(Guid.NewGuid(), workspaceId, Guid.NewGuid(), Guid.NewGuid(), "tcl", DateTimeOffset.UtcNow, Array.Empty<MappingDraftRuleDetail>());
            store.Drafts[draft.DraftId] = draft;
            var rule = NewRule(draft.DraftId);
            store.Rules[(draft.DraftId, rule.RuleId)] = rule;

            var controller = BuildController(store, new FakeSuggestionService(), identityService, new FakeCurrentUser { UserId = user });

            var result = await controller.UpdateRule(workspaceId, draft.DraftId, rule.RuleId, new UpdateRuleRequest { Status = "accepted" }, CancellationToken.None);

            var objResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status428PreconditionRequired, objResult.StatusCode);
        }

        [Fact]
        public async Task Patch_com_IfMatch_desatualizado_retorna_412()
        {
            var store = new FakeDraftStore();
            var user = Guid.NewGuid();
            var workspaceId = Guid.NewGuid();
            var identityService = new FakeIdentityWorkspaceService();
            identityService.Memberships.Add((workspaceId, user));
            var draft = new MappingDraftDetail(Guid.NewGuid(), workspaceId, Guid.NewGuid(), Guid.NewGuid(), "tcl", DateTimeOffset.UtcNow, Array.Empty<MappingDraftRuleDetail>());
            store.Drafts[draft.DraftId] = draft;
            var rule = NewRule(draft.DraftId);
            store.Rules[(draft.DraftId, rule.RuleId)] = rule;

            var controller = BuildController(store, new FakeSuggestionService(), identityService, new FakeCurrentUser { UserId = user });
            controller.Request.Headers["If-Match"] = Convert.ToBase64String(Guid.NewGuid().ToByteArray()[..8]); // ETag errado, de propósito.

            var result = await controller.UpdateRule(workspaceId, draft.DraftId, rule.RuleId, new UpdateRuleRequest { Status = "accepted" }, CancellationToken.None);

            var objResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status412PreconditionFailed, objResult.StatusCode);
        }

        [Fact]
        public async Task Patch_com_IfMatch_correto_atualiza_com_sucesso()
        {
            var store = new FakeDraftStore();
            var user = Guid.NewGuid();
            var workspaceId = Guid.NewGuid();
            var identityService = new FakeIdentityWorkspaceService();
            identityService.Memberships.Add((workspaceId, user));
            var draft = new MappingDraftDetail(Guid.NewGuid(), workspaceId, Guid.NewGuid(), Guid.NewGuid(), "tcl", DateTimeOffset.UtcNow, Array.Empty<MappingDraftRuleDetail>());
            store.Drafts[draft.DraftId] = draft;
            var rule = NewRule(draft.DraftId);
            store.Rules[(draft.DraftId, rule.RuleId)] = rule;

            var controller = BuildController(store, new FakeSuggestionService(), identityService, new FakeCurrentUser { UserId = user });
            controller.Request.Headers["If-Match"] = rule.ETag;

            var result = await controller.UpdateRule(workspaceId, draft.DraftId, rule.RuleId, new UpdateRuleRequest { Status = "accepted" }, CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.NotNull(ok.Value);
            // O ROWVERSION muda depois da atualização — confirma que não é o mesmo ETag de antes.
            Assert.NotEqual(rule.ETag, store.Rules[(draft.DraftId, rule.RuleId)].ETag);
        }

        [Fact]
        public async Task Patch_rejected_sem_justificativa_e_recusado()
        {
            var store = new FakeDraftStore();
            var user = Guid.NewGuid();
            var workspaceId = Guid.NewGuid();
            var identityService = new FakeIdentityWorkspaceService();
            identityService.Memberships.Add((workspaceId, user));
            var draft = new MappingDraftDetail(Guid.NewGuid(), workspaceId, Guid.NewGuid(), Guid.NewGuid(), "tcl", DateTimeOffset.UtcNow, Array.Empty<MappingDraftRuleDetail>());
            store.Drafts[draft.DraftId] = draft;
            var rule = NewRule(draft.DraftId);
            store.Rules[(draft.DraftId, rule.RuleId)] = rule;

            var controller = BuildController(store, new FakeSuggestionService(), identityService, new FakeCurrentUser { UserId = user });
            controller.Request.Headers["If-Match"] = rule.ETag;

            var result = await controller.UpdateRule(workspaceId, draft.DraftId, rule.RuleId, new UpdateRuleRequest { Status = "rejected" }, CancellationToken.None);

            Assert.IsType<UnprocessableEntityObjectResult>(result);
        }

        [Fact]
        public async Task GetDraft_de_outro_workspace_retorna_404()
        {
            var store = new FakeDraftStore();
            var userA = Guid.NewGuid();
            var workspaceB = Guid.NewGuid();
            var identityService = new FakeIdentityWorkspaceService();
            var draft = new MappingDraftDetail(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "tcl", DateTimeOffset.UtcNow, Array.Empty<MappingDraftRuleDetail>());
            store.Drafts[draft.DraftId] = draft;

            var controller = BuildController(store, new FakeSuggestionService(), identityService, new FakeCurrentUser { UserId = userA });

            // workspaceId da rota não bate com o dono real do draft.
            var result = await controller.GetDraft(workspaceB, draft.DraftId, CancellationToken.None);

            Assert.IsType<NotFoundResult>(result);
        }

        [Fact]
        public async Task CreateDraft_sem_engine_e_recusado_422()
        {
            var store = new FakeDraftStore();
            var user = Guid.NewGuid();
            var workspaceId = Guid.NewGuid();
            var identityService = new FakeIdentityWorkspaceService();
            identityService.Memberships.Add((workspaceId, user));

            var controller = BuildController(store, new FakeSuggestionService(), identityService, new FakeCurrentUser { UserId = user });

            var result = await controller.CreateDraft(workspaceId, Guid.NewGuid(), new CreateDraftRequest { RevisionId = Guid.NewGuid() }, CancellationToken.None);

            Assert.IsType<UnprocessableEntityObjectResult>(result);
        }

        [Fact]
        public async Task CreateSuggestionJob_retorna_202_sem_esperar_o_job_terminar()
        {
            // Fire-and-forget real: o controller responde antes do job pesado concluir (dotnet-standards
            // §Background work / spec §8: "jobs de IA são assíncronos... nunca bloqueia a resposta HTTP").
            var store = new FakeDraftStore();
            var user = Guid.NewGuid();
            var workspaceId = Guid.NewGuid();
            var identityService = new FakeIdentityWorkspaceService();
            identityService.Memberships.Add((workspaceId, user));
            var draft = new MappingDraftDetail(Guid.NewGuid(), workspaceId, Guid.NewGuid(), Guid.NewGuid(), "tcl", DateTimeOffset.UtcNow, Array.Empty<MappingDraftRuleDetail>());
            store.Drafts[draft.DraftId] = draft;

            var suggestionService = new FakeSuggestionService { BlockedUntilReleased = true };
            var controller = BuildController(store, suggestionService, identityService, new FakeCurrentUser { UserId = user });

            var task = controller.CreateSuggestionJob(workspaceId, draft.DraftId, CancellationToken.None);
            var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2)));

            Assert.Same(task, completed); // não travou esperando o job "pesado" liberar o gate.
            var actionResult = await task;
            var result = Assert.IsType<AcceptedAtActionResult>(actionResult);
            Assert.Equal(StatusCodes.Status202Accepted, result.StatusCode);

            suggestionService.Release();
        }
    }
}
