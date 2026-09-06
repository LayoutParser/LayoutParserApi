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
    /// Slice 5 (issue #231) — Fiscal Test Lab: aplica o XSLT compilado, faz diff canônico contra o
    /// gabarito e rastreia cada divergência até a <see cref="MappingDraftRule"/> de origem
    /// (provenance). <see cref="MappingTestRunSummary.RequiredGatesPassed"/> é o contrato com o
    /// Slice 7 — verificado explicitamente em cada cenário.
    /// </summary>
    public class MappingTestRunServiceTests
    {
        private sealed class FakeReleaseStore : IMappingReleaseStore
        {
            public MappingReleaseDetail? Release { get; set; }
            public MappingTestRunSummary? LastAppliedSummary { get; private set; }

            public Task<MappingReleaseDetail> CreateOrGetCompiledReleaseAsync(Guid workspaceId, Guid draftId, string engine, string rulesSnapshotHash, IReadOnlyList<Guid> sourceRuleIds, IReadOnlyList<MappingReleaseArtifact> artifacts, IReadOnlyList<MappingReleaseCompileDiagnostic> compileDiagnostics, string correlationId, Guid jobId, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task<MappingReleaseDetail?> GetReleaseIfMemberAsync(Guid releaseId, Guid userId, CancellationToken cancellationToken)
                => Task.FromResult(Release?.ReleaseId == releaseId ? Release : null);

            public Task<(IReadOnlyList<MappingReleaseDetail> Items, int TotalCount)> ListByWorkspaceAsync(Guid workspaceId, int page, int pageSize, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task<MappingReleaseDetail?> ApplyTestRunResultAsync(Guid releaseId, MappingTestRunSummary summary, CancellationToken cancellationToken)
            {
                LastAppliedSummary = summary;
                if (Release == null || Release.ReleaseId != releaseId)
                    return Task.FromResult<MappingReleaseDetail?>(null);

                Release = Release with
                {
                    TestRunSummary = summary,
                    Status = summary.RequiredGatesPassed ? MappingReleaseStatus.TestPassed : MappingReleaseStatus.TestFailed
                };
                return Task.FromResult<MappingReleaseDetail?>(Release);
            }

            public Task<MappingReleaseDetail> ApproveAsync(Guid releaseId, Guid actorUserId, string justification, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task<MappingReleaseDetail> PublishAsync(Guid releaseId, Guid actorUserId, string environment, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task<MappingReleaseDetail> RollbackAsync(Guid releaseId, Guid actorUserId, CancellationToken cancellationToken)
                => throw new NotSupportedException();
        }

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

        private static IServiceScopeFactory BuildScopeFactory(FakeDraftStore draftStore, FakeReleaseStore releaseStore)
        {
            var services = new ServiceCollection();
            services.AddSingleton<IMappingDraftStore>(draftStore);
            services.AddSingleton<IMappingReleaseStore>(releaseStore);
            // XsdValidationService real: sem XSD no disco de teste, ValidateXmlAgainstXsdAsync degrada
            // (IsValid=false por doc não detectado) — o serviço trata a indisponibilidade como
            // "não bloqueia o gate", conforme a resiliência exigida (dotnet-standards.md).
            services.AddSingleton<XmlDocumentTypeDetector>();
            services.AddLogging();
            services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
            services.AddSingleton<XsdValidationService>();
            var provider = services.BuildServiceProvider();
            return provider.GetRequiredService<IServiceScopeFactory>();
        }

        private static async Task<TestRunJobState> WaitForCompletionAsync(MappingTestRunService service, Guid jobId)
        {
            for (var i = 0; i < 200; i++)
            {
                var state = await service.GetStatusAsync(jobId, CancellationToken.None);
                if (state != null && state.Status is TestRunJobStatus.Completed or TestRunJobStatus.Failed)
                    return state;
                await Task.Delay(10);
            }
            throw new TimeoutException("Job de test-run não concluiu a tempo.");
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
        public async Task TestRun_SaidaBateComGabarito_PassaEComProvenance()
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
            var scopeFactory = BuildScopeFactory(draftStore, releaseStore);

            var service = new MappingTestRunService(NullLogger<MappingTestRunService>.Instance, scopeFactory);

            var inputXml = "<nfe><emit><CNPJ>12345678000199</CNPJ></emit></nfe>";
            var expectedXml = "<dest><cnpj>12345678000199</cnpj></dest>";

            var jobId = await service.EnqueueAsync(workspaceId, draftId, release.ReleaseId, userId, inputXml, expectedXml, null, "corr-1", CancellationToken.None);
            var state = await WaitForCompletionAsync(service, jobId);

            Assert.Equal(TestRunJobStatus.Completed, state.Status);
            Assert.True(state.RequiredGatesPassed);
            Assert.NotNull(releaseStore.LastAppliedSummary);
            Assert.True(releaseStore.LastAppliedSummary!.RequiredGatesPassed);
            Assert.Empty(releaseStore.LastAppliedSummary.Divergences);
            Assert.Equal(MappingReleaseStatus.TestPassed, releaseStore.Release!.Status);
        }

        [Fact]
        public async Task TestRun_SaidaDivergeDoGabarito_FalhaComProvenanceAteARegra()
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
            var scopeFactory = BuildScopeFactory(draftStore, releaseStore);

            var service = new MappingTestRunService(NullLogger<MappingTestRunService>.Instance, scopeFactory);

            var inputXml = "<nfe><emit><CNPJ>12345678000199</CNPJ></emit></nfe>";
            var expectedXml = "<dest><cnpj>00000000000000</cnpj></dest>"; // gabarito diferente do que o XSLT produz

            var jobId = await service.EnqueueAsync(workspaceId, draftId, release.ReleaseId, userId, inputXml, expectedXml, null, "corr-1", CancellationToken.None);
            var state = await WaitForCompletionAsync(service, jobId);

            Assert.Equal(TestRunJobStatus.Completed, state.Status);
            Assert.False(state.RequiredGatesPassed);
            var summary = releaseStore.LastAppliedSummary!;
            Assert.False(summary.RequiredGatesPassed);
            Assert.Single(summary.Divergences);
            var divergence = summary.Divergences[0];
            Assert.Equal(ruleId, divergence.RuleId); // provenance: nó divergente -> regra de origem
            Assert.Equal(new[] { "/nfe/emit/CNPJ" }, divergence.SourceRefs);
            Assert.Equal(MappingReleaseStatus.TestFailed, releaseStore.Release!.Status);
        }

        [Theory]
        [InlineData(true)]  // ataque no inputXml
        [InlineData(false)] // ataque no expectedXml (gabarito)
        public async Task TestRun_PayloadXxeNoFixtureHttp_RejeitadoSemProcessarEntidadeExterna(bool ataqueNoInput)
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
            var scopeFactory = BuildScopeFactory(draftStore, releaseStore);

            var service = new MappingTestRunService(NullLogger<MappingTestRunService>.Instance, scopeFactory);

            // Payload XXE clássico: DOCTYPE com entidade externa apontando pra um arquivo local.
            // Se processado, o parser tentaria ler C:\Windows\win.ini e substituir &xxe; pelo conteúdo.
            const string xxePayload =
                "<?xml version=\"1.0\"?>" +
                "<!DOCTYPE root [<!ENTITY xxe SYSTEM \"file:///C:/Windows/win.ini\">]>" +
                "<root>&xxe;</root>";

            var inputXml = ataqueNoInput ? xxePayload : "<nfe><emit><CNPJ>12345678000199</CNPJ></emit></nfe>";
            var expectedXml = ataqueNoInput ? "<dest><cnpj>12345678000199</cnpj></dest>" : xxePayload;

            var jobId = await service.EnqueueAsync(workspaceId, draftId, release.ReleaseId, userId, inputXml, expectedXml, null, "corr-xxe", CancellationToken.None);
            var state = await WaitForCompletionAsync(service, jobId);

            // Nunca deve derrubar o job (dotnet-standards.md §Resiliência) — vira falha de teste
            // reportada, sem propagar conteúdo de arquivo local nem lançar exceção não tratada.
            Assert.Equal(TestRunJobStatus.Completed, state.Status);
            Assert.False(state.RequiredGatesPassed);
            var summary = releaseStore.LastAppliedSummary!;
            Assert.False(summary.RequiredGatesPassed);
            Assert.DoesNotContain(summary.XsdErrors.Concat(new[] { string.Empty }), e => e.Contains("[fonts]", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task TestRun_ReleaseForaDoWorkspace_LancaSemDerrubarOChamador()
        {
            var workspaceId = Guid.NewGuid();
            var draftId = Guid.NewGuid();
            var userId = Guid.NewGuid();

            var draftStore = new FakeDraftStore
            {
                Draft = new MappingDraftDetail(draftId, workspaceId, Guid.NewGuid(), Guid.NewGuid(), "xslt", DateTimeOffset.UtcNow, Array.Empty<MappingDraftRuleDetail>())
            };
            var releaseStore = new FakeReleaseStore
            {
                Release = BuildCompiledXsltRelease(Guid.NewGuid(), draftId, Array.Empty<MappingDraftRule>()) // workspace diferente
            };
            var scopeFactory = BuildScopeFactory(draftStore, releaseStore);
            var service = new MappingTestRunService(NullLogger<MappingTestRunService>.Instance, scopeFactory);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnqueueAsync(
                workspaceId, draftId, releaseStore.Release!.ReleaseId, userId, "<a/>", "<a/>", null, "corr-1", CancellationToken.None));
        }
    }
}
