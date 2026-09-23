using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Services.Fiscal;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.XmlAnalysis;
using LayoutParserApi.Services.XmlAnalysis.Models;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace LayoutParserApi.Tests.Services.Fiscal
{
    /// <summary>
    /// Issue #423 — suíte de teste versionada: múltiplas fixtures executadas em bloco contra uma
    /// release, com resultado agregado (passou/falhou por fixture) e histórico persistido. Reaproveita
    /// <see cref="MappingTestRunService.EvaluateFixtureAsync"/> — os testes aqui cobrem a AGREGAÇÃO,
    /// não o diff canônico em si (já coberto por <c>MappingTestRunServiceTests</c>).
    /// </summary>
    public class TestSuiteRunServiceTests
    {
        private sealed class FakeDraftStore : IMappingDraftStore
        {
            public MappingDraftDetail? Draft { get; set; }

            public Task<bool> RevisionBelongsToWorkspacePackageAsync(Guid workspaceId, Guid packageId, Guid revisionId, CancellationToken cancellationToken) => Task.FromResult(true);
            public Task<(IReadOnlyList<MappingDraftSummary> Items, int TotalCount)> ListByWorkspaceAsync(Guid workspaceId, int page, int pageSize, string? engine, CancellationToken cancellationToken)
                => throw new NotSupportedException();
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
            public Task<MappingDraftDetail?> SetFiscalProfileAsync(Guid draftId, Guid userId, FiscalProfile profile, CancellationToken cancellationToken)
                => throw new NotSupportedException();
        }

        private sealed class FakeReleaseStore : IMappingReleaseStore
        {
            public MappingReleaseDetail? Release { get; set; }

            public Task<MappingReleaseDetail> CreateOrGetCompiledReleaseAsync(Guid workspaceId, Guid draftId, string engine, string rulesSnapshotHash, IReadOnlyList<Guid> sourceRuleIds, IReadOnlyList<MappingReleaseArtifact> artifacts, IReadOnlyList<MappingReleaseCompileDiagnostic> compileDiagnostics, string correlationId, Guid jobId, CancellationToken cancellationToken, FiscalProfile? fiscalProfile = null)
                => throw new NotSupportedException();
            public Task<MappingReleaseDetail?> GetReleaseIfMemberAsync(Guid releaseId, Guid userId, CancellationToken cancellationToken)
                => Task.FromResult(Release?.ReleaseId == releaseId ? Release : null);
            public Task<(IReadOnlyList<MappingReleaseDetail> Items, int TotalCount)> ListByWorkspaceAsync(Guid workspaceId, int page, int pageSize, string? status, Guid? draftId, string? environment, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task<CreateManualEditOutcome> CreateManualEditArtifactReleaseAsync(Guid workspaceId, Guid draftId, string engine, string content, string manualEditReason, string expectedArtifactHash, Guid actorUserId, string correlationId, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task<MappingReleaseDetail?> ApplyTestRunResultAsync(Guid releaseId, MappingTestRunSummary summary, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task<MappingReleaseDetail> ApproveAsync(Guid releaseId, Guid actorUserId, string justification, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task<MappingReleaseDetail> PublishAsync(Guid releaseId, Guid actorUserId, string environment, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task<MappingReleaseDetail> RollbackAsync(Guid releaseId, Guid actorUserId, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task<MappingReleaseDetail> DeprecateAsync(Guid releaseId, Guid actorUserId, string? justification, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task<MappingReleaseDetail> ArchiveAsync(Guid releaseId, Guid actorUserId, string? justification, CancellationToken cancellationToken)
                => throw new NotSupportedException();
        }

        private sealed class FakeSuiteStore : ITestSuiteStore
        {
            public TestSuiteDetail? Suite { get; set; }
            public List<TestSuiteFixtureDetail> Fixtures { get; } = new();
            public List<TestSuiteRunDetail> Runs { get; } = new();

            public Task<TestSuiteDetail> CreateSuiteAsync(Guid workspaceId, Guid draftId, string name, string? description, Guid createdByUserId, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task<TestSuiteDetail?> GetSuiteIfMemberAsync(Guid suiteId, Guid userId, CancellationToken cancellationToken)
                => Task.FromResult(Suite?.SuiteId == suiteId ? Suite : null);
            public Task<IReadOnlyList<TestSuiteDetail>> ListByDraftAsync(Guid draftId, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task<TestSuiteFixtureDetail> AddFixtureAsync(Guid suiteId, string name, string inputXml, string expectedXml, string? xsdVersion, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task<IReadOnlyList<TestSuiteFixtureDetail>> ListFixturesAsync(Guid suiteId, CancellationToken cancellationToken)
                => Task.FromResult<IReadOnlyList<TestSuiteFixtureDetail>>(Fixtures.Where(f => f.SuiteId == suiteId).ToList());
            public Task<TestSuiteRunDetail> RecordRunAsync(Guid suiteId, Guid releaseId, Guid executedByUserId, IReadOnlyList<TestSuiteFixtureResult> fixtureResults, double durationMs, CancellationToken cancellationToken)
            {
                var passed = fixtureResults.Count(f => f.Passed);
                var failed = fixtureResults.Count - passed;
                var run = new TestSuiteRunDetail(
                    Guid.NewGuid(), suiteId, releaseId, executedByUserId, DateTimeOffset.UtcNow,
                    fixtureResults.Count, passed, failed, fixtureResults.Count > 0 && failed == 0, durationMs, fixtureResults);
                Runs.Add(run);
                return Task.FromResult(run);
            }
            public Task<(IReadOnlyList<TestSuiteRunDetail> Items, int TotalCount)> ListRunsAsync(Guid suiteId, int page, int pageSize, CancellationToken cancellationToken)
                => Task.FromResult<(IReadOnlyList<TestSuiteRunDetail>, int)>((Runs.Where(r => r.SuiteId == suiteId).OrderByDescending(r => r.ExecutedAt).ToList(), Runs.Count));
        }

        private static IServiceScopeFactory BuildScopeFactory()
        {
            var services = new ServiceCollection();
            services.AddSingleton<XmlDocumentTypeDetector>();
            services.AddLogging();
            services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
            services.AddSingleton<XsdValidationService>();
            var provider = services.BuildServiceProvider();
            return provider.GetRequiredService<IServiceScopeFactory>();
        }

        private static MappingDraftRuleDetail AcceptedCopyRule(Guid ruleId, string source, string target) => new(
            ruleId, Guid.Empty, new[] { source }, new[] { target }, "copy", "[]", "[]", "1:1",
            new[] { new MappingDraftRuleEvidence("sample", "linha 12") }, "high", MappingDraftRuleStatus.Accepted,
            Array.Empty<string>(), DateTimeOffset.UtcNow, Convert.ToBase64String(Guid.NewGuid().ToByteArray()[..8]));

        private static MappingReleaseDetail BuildCompiledXsltRelease(Guid workspaceId, Guid draftId, IReadOnlyList<MappingDraftRule> rules)
        {
            var result = MappingDraftRuleTranspiler.ToXslt(rules, new SchemaRef("origem"), new SchemaRef("dest"));
            var artifact = new MappingReleaseArtifact("xslt", result.Content, "hash", DateTimeOffset.UtcNow);
            return new MappingReleaseDetail(
                Guid.NewGuid(), workspaceId, draftId, "xslt", new[] { artifact }, rules.Select(r => r.RuleId).ToList(),
                Array.Empty<MappingReleaseCompileDiagnostic>(), "hash", null, MappingReleaseStatus.DraftCompiled,
                "corr-0", DateTimeOffset.UtcNow, "AAAA",
                "development", null, null, null, null, null, null);
        }

        [Fact]
        public async Task RunSuite_MisturaPassaEFalha_ResultadoAgregadoReflete()
        {
            var workspaceId = Guid.NewGuid();
            var draftId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var ruleId = Guid.NewGuid();

            var ruleDetail = AcceptedCopyRule(ruleId, "/nfe/emit/CNPJ", "/dest/cnpj");
            var draft = new MappingDraftDetail(draftId, workspaceId, Guid.NewGuid(), Guid.NewGuid(), "xslt", DateTimeOffset.UtcNow, new[] { ruleDetail });

            var rule = new MappingDraftRule
            {
                RuleId = ruleId, DraftId = draftId, SourceRefs = new[] { "/nfe/emit/CNPJ" },
                TargetRefs = new[] { "/dest/cnpj" }, Operation = "copy", Status = MappingDraftRuleStatus.Accepted,
            };
            var release = BuildCompiledXsltRelease(workspaceId, draftId, new[] { rule });

            var draftStore = new FakeDraftStore { Draft = draft };
            var releaseStore = new FakeReleaseStore { Release = release };
            var suiteId = Guid.NewGuid();
            var suiteStore = new FakeSuiteStore
            {
                Suite = new TestSuiteDetail(suiteId, workspaceId, draftId, "suíte fiscal", null, userId, DateTimeOffset.UtcNow, "AAAA"),
            };
            suiteStore.Fixtures.Add(new TestSuiteFixtureDetail(
                Guid.NewGuid(), suiteId, "fixture-ok", "<nfe><emit><CNPJ>12345678000199</CNPJ></emit></nfe>", "<dest><cnpj>12345678000199</cnpj></dest>", null, 0, DateTimeOffset.UtcNow));
            suiteStore.Fixtures.Add(new TestSuiteFixtureDetail(
                Guid.NewGuid(), suiteId, "fixture-diverge", "<nfe><emit><CNPJ>12345678000199</CNPJ></emit></nfe>", "<dest><cnpj>00000000000000</cnpj></dest>", null, 1, DateTimeOffset.UtcNow));

            var testRunService = new MappingTestRunService(NullLogger<MappingTestRunService>.Instance, BuildScopeFactory());
            var runService = new TestSuiteRunService(suiteStore, draftStore, releaseStore, testRunService, NullLogger<TestSuiteRunService>.Instance);

            var run = await runService.RunSuiteAsync(workspaceId, draftId, suiteId, release.ReleaseId, userId, "corr-suite-1", CancellationToken.None);

            Assert.Equal(2, run.TotalFixtures);
            Assert.Equal(1, run.Passed);
            Assert.Equal(1, run.Failed);
            Assert.False(run.RequiredGatesPassed);
            Assert.True(run.FixtureResults.Single(f => f.FixtureName == "fixture-ok").Passed);
            Assert.False(run.FixtureResults.Single(f => f.FixtureName == "fixture-diverge").Passed);
            Assert.Single(suiteStore.Runs);
        }

        [Fact]
        public async Task RunSuite_TodasPassam_RequiredGatesPassedTrue()
        {
            var workspaceId = Guid.NewGuid();
            var draftId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var ruleId = Guid.NewGuid();

            var ruleDetail = AcceptedCopyRule(ruleId, "/nfe/emit/CNPJ", "/dest/cnpj");
            var draft = new MappingDraftDetail(draftId, workspaceId, Guid.NewGuid(), Guid.NewGuid(), "xslt", DateTimeOffset.UtcNow, new[] { ruleDetail });

            var rule = new MappingDraftRule
            {
                RuleId = ruleId, DraftId = draftId, SourceRefs = new[] { "/nfe/emit/CNPJ" },
                TargetRefs = new[] { "/dest/cnpj" }, Operation = "copy", Status = MappingDraftRuleStatus.Accepted,
            };
            var release = BuildCompiledXsltRelease(workspaceId, draftId, new[] { rule });

            var draftStore = new FakeDraftStore { Draft = draft };
            var releaseStore = new FakeReleaseStore { Release = release };
            var suiteId = Guid.NewGuid();
            var suiteStore = new FakeSuiteStore
            {
                Suite = new TestSuiteDetail(suiteId, workspaceId, draftId, "suíte fiscal", null, userId, DateTimeOffset.UtcNow, "AAAA"),
            };
            suiteStore.Fixtures.Add(new TestSuiteFixtureDetail(
                Guid.NewGuid(), suiteId, "fixture-1", "<nfe><emit><CNPJ>12345678000199</CNPJ></emit></nfe>", "<dest><cnpj>12345678000199</cnpj></dest>", null, 0, DateTimeOffset.UtcNow));

            var testRunService = new MappingTestRunService(NullLogger<MappingTestRunService>.Instance, BuildScopeFactory());
            var runService = new TestSuiteRunService(suiteStore, draftStore, releaseStore, testRunService, NullLogger<TestSuiteRunService>.Instance);

            var run = await runService.RunSuiteAsync(workspaceId, draftId, suiteId, release.ReleaseId, userId, "corr-suite-2", CancellationToken.None);

            Assert.Equal(1, run.TotalFixtures);
            Assert.Equal(1, run.Passed);
            Assert.Equal(0, run.Failed);
            Assert.True(run.RequiredGatesPassed);
        }

        [Fact]
        public async Task RunSuite_SemFixtures_LancaSemPersistirHistorico()
        {
            var workspaceId = Guid.NewGuid();
            var draftId = Guid.NewGuid();
            var userId = Guid.NewGuid();

            var draft = new MappingDraftDetail(draftId, workspaceId, Guid.NewGuid(), Guid.NewGuid(), "xslt", DateTimeOffset.UtcNow, Array.Empty<MappingDraftRuleDetail>());
            var release = BuildCompiledXsltRelease(workspaceId, draftId, Array.Empty<MappingDraftRule>());

            var draftStore = new FakeDraftStore { Draft = draft };
            var releaseStore = new FakeReleaseStore { Release = release };
            var suiteId = Guid.NewGuid();
            var suiteStore = new FakeSuiteStore
            {
                Suite = new TestSuiteDetail(suiteId, workspaceId, draftId, "suíte vazia", null, userId, DateTimeOffset.UtcNow, "AAAA"),
            };

            var testRunService = new MappingTestRunService(NullLogger<MappingTestRunService>.Instance, BuildScopeFactory());
            var runService = new TestSuiteRunService(suiteStore, draftStore, releaseStore, testRunService, NullLogger<TestSuiteRunService>.Instance);

            await Assert.ThrowsAsync<InvalidOperationException>(() => runService.RunSuiteAsync(
                workspaceId, draftId, suiteId, release.ReleaseId, userId, "corr-suite-3", CancellationToken.None));

            Assert.Empty(suiteStore.Runs);
        }

        [Fact]
        public async Task RunSuite_ReleaseForaDoWorkspaceDaSuite_Lanca()
        {
            var workspaceId = Guid.NewGuid();
            var draftId = Guid.NewGuid();
            var userId = Guid.NewGuid();

            var draft = new MappingDraftDetail(draftId, workspaceId, Guid.NewGuid(), Guid.NewGuid(), "xslt", DateTimeOffset.UtcNow, Array.Empty<MappingDraftRuleDetail>());
            var releaseOutroWorkspace = BuildCompiledXsltRelease(Guid.NewGuid(), draftId, Array.Empty<MappingDraftRule>());

            var draftStore = new FakeDraftStore { Draft = draft };
            var releaseStore = new FakeReleaseStore { Release = releaseOutroWorkspace };
            var suiteId = Guid.NewGuid();
            var suiteStore = new FakeSuiteStore
            {
                Suite = new TestSuiteDetail(suiteId, workspaceId, draftId, "suíte", null, userId, DateTimeOffset.UtcNow, "AAAA"),
            };
            suiteStore.Fixtures.Add(new TestSuiteFixtureDetail(Guid.NewGuid(), suiteId, "f1", "<a/>", "<a/>", null, 0, DateTimeOffset.UtcNow));

            var testRunService = new MappingTestRunService(NullLogger<MappingTestRunService>.Instance, BuildScopeFactory());
            var runService = new TestSuiteRunService(suiteStore, draftStore, releaseStore, testRunService, NullLogger<TestSuiteRunService>.Instance);

            await Assert.ThrowsAsync<InvalidOperationException>(() => runService.RunSuiteAsync(
                workspaceId, draftId, suiteId, releaseOutroWorkspace.ReleaseId, userId, "corr-suite-4", CancellationToken.None));
        }
    }
}
