using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Services.Fiscal;
using LayoutParserApi.Services.Interfaces;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace LayoutParserApi.Tests.Services.Fiscal
{
    /// <summary>
    /// Slice 5 (issue #231) — job assíncrono de compilação: draft_compiled, idempotência por snapshot
    /// de regras e "engine=sysmiddle" nunca chega aqui (recusado antes, no MappingEngineGuardFilter —
    /// coberto separadamente).
    /// </summary>
    public class MappingCompileServiceTests
    {
        private sealed class FakeDraftStore : IMappingDraftStore
        {
            public MappingDraftDetail? Draft { get; set; }

            public Task<bool> RevisionBelongsToPackageAsync(Guid packageId, Guid revisionId, CancellationToken cancellationToken) => Task.FromResult(true);
            public Task<IReadOnlyList<ArtifactFileRef>> GetArtifactFilesForRevisionAsync(Guid revisionId, CancellationToken cancellationToken)
                => Task.FromResult<IReadOnlyList<ArtifactFileRef>>(Array.Empty<ArtifactFileRef>());
            public Task<MappingDraftDetail> CreateDraftAsync(Guid workspaceId, Guid packageId, Guid revisionId, Guid createdByUserId, string engine, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task<MappingDraftDetail?> GetDraftIfMemberAsync(Guid draftId, Guid userId, CancellationToken cancellationToken)
                => Task.FromResult(Draft?.DraftId == draftId ? Draft : null);
            public Task<MappingDraftRuleDetail?> GetRuleIfMemberAsync(Guid draftId, Guid ruleId, Guid userId, CancellationToken cancellationToken)
                => Task.FromResult(Draft?.Rules.FirstOrDefault(r => r.RuleId == ruleId));
            public Task InsertProposedRulesAsync(Guid draftId, Guid jobId, IReadOnlyList<MappingDraftRuleProposal> proposals, CancellationToken cancellationToken)
                => Task.CompletedTask;
            public Task<UpdateRuleOutcome> UpdateRuleStatusAsync(Guid draftId, Guid ruleId, Guid userId, byte[] expectedRowVersion, string newStatus, string? justification, IReadOnlyList<string>? editedSourceRefs, IReadOnlyList<string>? editedTargetRefs, string? editedOperation, CancellationToken cancellationToken)
                => throw new NotSupportedException();
        }

        private sealed class FakeReleaseStore : IMappingReleaseStore
        {
            public Dictionary<(Guid DraftId, string Hash), MappingReleaseDetail> ByHash { get; } = new();
            public Dictionary<Guid, MappingReleaseDetail> ById { get; } = new();
            public int CreateCalls { get; private set; }

            public Task<MappingReleaseDetail> CreateOrGetCompiledReleaseAsync(
                Guid workspaceId, Guid draftId, string engine, string rulesSnapshotHash, IReadOnlyList<Guid> sourceRuleIds,
                IReadOnlyList<MappingReleaseArtifact> artifacts, IReadOnlyList<MappingReleaseCompileDiagnostic> compileDiagnostics,
                string correlationId, Guid jobId, CancellationToken cancellationToken)
            {
                if (ByHash.TryGetValue((draftId, rulesSnapshotHash), out var existing))
                    return Task.FromResult(existing);

                CreateCalls++;
                var detail = new MappingReleaseDetail(
                    Guid.NewGuid(), workspaceId, draftId, engine, artifacts, sourceRuleIds, compileDiagnostics,
                    rulesSnapshotHash, null, MappingReleaseStatus.DraftCompiled, correlationId, DateTimeOffset.UtcNow, "AAAA",
                    "development", null, null, null, null, null, null);
                ByHash[(draftId, rulesSnapshotHash)] = detail;
                ById[detail.ReleaseId] = detail;
                return Task.FromResult(detail);
            }

            public Task<MappingReleaseDetail?> GetReleaseIfMemberAsync(Guid releaseId, Guid userId, CancellationToken cancellationToken)
                => Task.FromResult(ById.TryGetValue(releaseId, out var r) ? r : null);

            public Task<(IReadOnlyList<MappingReleaseDetail> Items, int TotalCount)> ListByWorkspaceAsync(Guid workspaceId, int page, int pageSize, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task<MappingReleaseDetail?> ApplyTestRunResultAsync(Guid releaseId, MappingTestRunSummary summary, CancellationToken cancellationToken)
            {
                if (!ById.TryGetValue(releaseId, out var existing))
                    return Task.FromResult<MappingReleaseDetail?>(null);

                var updated = existing with
                {
                    TestRunSummary = summary,
                    Status = summary.RequiredGatesPassed ? MappingReleaseStatus.TestPassed : MappingReleaseStatus.TestFailed
                };
                ById[releaseId] = updated;
                return Task.FromResult<MappingReleaseDetail?>(updated);
            }

            public Task<MappingReleaseDetail> ApproveAsync(Guid releaseId, Guid actorUserId, string justification, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task<MappingReleaseDetail> PublishAsync(Guid releaseId, Guid actorUserId, string environment, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task<MappingReleaseDetail> RollbackAsync(Guid releaseId, Guid actorUserId, CancellationToken cancellationToken)
                => throw new NotSupportedException();
        }

        private static MappingDraftRuleDetail AcceptedCopyRule(string source, string target) => new(
            Guid.NewGuid(), Guid.Empty, new[] { source }, new[] { target }, "copy", "[]", "[]", "1:1",
            Array.Empty<MappingDraftRuleEvidence>(), "high", MappingDraftRuleStatus.Accepted, Array.Empty<string>(),
            DateTimeOffset.UtcNow, Convert.ToBase64String(Guid.NewGuid().ToByteArray()[..8]));

        private static (IServiceScopeFactory ScopeFactory, FakeDraftStore DraftStore, FakeReleaseStore ReleaseStore) BuildScopeFactory()
        {
            var draftStore = new FakeDraftStore();
            var releaseStore = new FakeReleaseStore();

            var services = new ServiceCollection();
            services.AddSingleton<IMappingDraftStore>(draftStore);
            services.AddSingleton<IMappingReleaseStore>(releaseStore);
            var provider = services.BuildServiceProvider();

            return (provider.GetRequiredService<IServiceScopeFactory>(), draftStore, releaseStore);
        }

        private static async Task<CompileJobState> WaitForCompletionAsync(MappingCompileService service, Guid jobId)
        {
            for (var i = 0; i < 200; i++)
            {
                var state = await service.GetStatusAsync(jobId, CancellationToken.None);
                if (state != null && state.Status is CompileJobStatus.Completed or CompileJobStatus.Failed)
                    return state;
                await Task.Delay(10);
            }
            throw new TimeoutException("Job de compilação não concluiu a tempo.");
        }

        [Fact]
        public async Task Enqueue_DraftComRegraAceita_GeraReleaseDraftCompiled()
        {
            var (scopeFactory, draftStore, releaseStore) = BuildScopeFactory();
            var workspaceId = Guid.NewGuid();
            var draftId = Guid.NewGuid();
            var userId = Guid.NewGuid();

            draftStore.Draft = new MappingDraftDetail(draftId, workspaceId, Guid.NewGuid(), Guid.NewGuid(), "xslt", DateTimeOffset.UtcNow,
                new[] { AcceptedCopyRule("/nfe/emit/CNPJ", "/dest/cnpj") });

            var service = new MappingCompileService(NullLogger<MappingCompileService>.Instance, scopeFactory);
            var jobId = await service.EnqueueAsync(workspaceId, draftId, userId, "corr-1", CancellationToken.None);
            var state = await WaitForCompletionAsync(service, jobId);

            Assert.Equal(CompileJobStatus.Completed, state.Status);
            Assert.NotNull(state.ReleaseId);
            var release = releaseStore.ById[state.ReleaseId!.Value];
            Assert.Equal(MappingReleaseStatus.DraftCompiled, release.Status);
            Assert.Single(release.Artifacts);
            Assert.Equal("xslt", release.Artifacts[0].Kind);
            Assert.Contains("value-of", release.Artifacts[0].Content);
        }

        [Fact]
        public async Task Enqueue_MesmoSnapshotDeRegras_NaoDuplicaRelease()
        {
            var (scopeFactory, draftStore, releaseStore) = BuildScopeFactory();
            var workspaceId = Guid.NewGuid();
            var draftId = Guid.NewGuid();
            var userId = Guid.NewGuid();

            draftStore.Draft = new MappingDraftDetail(draftId, workspaceId, Guid.NewGuid(), Guid.NewGuid(), "xslt", DateTimeOffset.UtcNow,
                new[] { AcceptedCopyRule("/nfe/emit/CNPJ", "/dest/cnpj") });

            var service = new MappingCompileService(NullLogger<MappingCompileService>.Instance, scopeFactory);

            var job1 = await service.EnqueueAsync(workspaceId, draftId, userId, "corr-1", CancellationToken.None);
            var state1 = await WaitForCompletionAsync(service, job1);

            var job2 = await service.EnqueueAsync(workspaceId, draftId, userId, "corr-2", CancellationToken.None);
            var state2 = await WaitForCompletionAsync(service, job2);

            Assert.Equal(state1.ReleaseId, state2.ReleaseId);
            Assert.Equal(1, releaseStore.CreateCalls);
        }

        [Fact]
        public async Task Enqueue_DraftForaDoWorkspace_NaoDerrubaOChamador()
        {
            var (scopeFactory, draftStore, _) = BuildScopeFactory();
            var draftId = Guid.NewGuid();
            var userId = Guid.NewGuid();

            draftStore.Draft = new MappingDraftDetail(draftId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "xslt", DateTimeOffset.UtcNow,
                Array.Empty<MappingDraftRuleDetail>());

            var service = new MappingCompileService(NullLogger<MappingCompileService>.Instance, scopeFactory);

            // Workspace diferente do dono real do draft — isolamento cross-workspace.
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.EnqueueAsync(Guid.NewGuid(), draftId, userId, "corr-1", CancellationToken.None));
        }
    }
}
