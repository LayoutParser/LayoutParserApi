using LayoutParserApi.Models.Dtos.Fiscal;
using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Services.Fiscal;
using LayoutParserApi.Services.Interfaces;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace LayoutParserApi.Tests.Fiscal
{
    /// <summary>
    /// Slice 4 (issue #226/#227) — os 5 testes obrigatórios do design: Capabilities.Author=false
    /// sempre no Sysmiddle, função desconhecida vira opaque, tradução TCL quase-1:1, XSL sem
    /// artefato compilado vira unsupported, e isolamento cross-workspace.
    /// </summary>
    public class MappingExplanationAdaptersTests
    {
        private sealed class FakeCachedMapperService : ICachedMapperService
        {
            public List<Mapper> Mappers { get; } = new();

            public Task<List<Mapper>> GetAllMappersAsync() => Task.FromResult(Mappers);
            public Task<List<Mapper>> GetMappersByInputLayoutGuidAsync(string inputLayoutGuid) => Task.FromResult(new List<Mapper>());
            public Task<List<Mapper>> GetMappersByTargetLayoutGuidAsync(string targetLayoutGuid) => Task.FromResult(new List<Mapper>());
            public Task RefreshCacheFromDatabaseAsync() => Task.CompletedTask;
        }

        private sealed class FakeDraftStore : IMappingDraftStore
        {
            public Dictionary<Guid, MappingDraftDetail> Drafts { get; } = new();

            public Task<bool> RevisionBelongsToPackageAsync(Guid packageId, Guid revisionId, CancellationToken cancellationToken) => Task.FromResult(true);
            public Task<IReadOnlyList<ArtifactFileRef>> GetArtifactFilesForRevisionAsync(Guid revisionId, CancellationToken cancellationToken)
                => Task.FromResult<IReadOnlyList<ArtifactFileRef>>(Array.Empty<ArtifactFileRef>());
            public Task<MappingDraftDetail> CreateDraftAsync(Guid workspaceId, Guid packageId, Guid revisionId, Guid createdByUserId, string engine, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task<MappingDraftDetail?> GetDraftIfMemberAsync(Guid draftId, Guid userId, CancellationToken cancellationToken)
                => Task.FromResult(Drafts.TryGetValue(draftId, out var d) ? d : null);
            public Task<MappingDraftRuleDetail?> GetRuleIfMemberAsync(Guid draftId, Guid ruleId, Guid userId, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task InsertProposedRulesAsync(Guid draftId, Guid jobId, IReadOnlyList<MappingDraftRuleProposal> proposals, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task<UpdateRuleOutcome> UpdateRuleStatusAsync(Guid draftId, Guid ruleId, Guid userId, byte[] expectedRowVersion, string newStatus, string? justification, IReadOnlyList<string>? editedSourceRefs, IReadOnlyList<string>? editedTargetRefs, string? editedOperation, CancellationToken cancellationToken)
                => throw new NotSupportedException();
        }

        private static Mapper BuildSysmiddleMapper(string mapperGuid, string ruleContentValue) => new()
        {
            MapperGuid = mapperGuid,
            Name = "Mapper de teste",
            Description = "Descrição",
            InputLayoutGuid = "FLD_IN",
            TargetLayoutGuid = "TAG_OUT",
            DecryptedContent = $"""
                <MapperVO>
                    <MapperGuid>{mapperGuid}</MapperGuid>
                    <Name>Mapper de teste</Name>
                    <InputLayoutGuid>FLD_IN</InputLayoutGuid>
                    <TargetLayoutGuid>TAG_OUT</TargetLayoutGuid>
                    <Rule>
                        <Name>RegraTeste</Name>
                        <Sequence>1</Sequence>
                        <ElementGuid>ATT_1</ElementGuid>
                        <TargetElementGuid>ATT_1</TargetElementGuid>
                        <ContentValue>{ruleContentValue}</ContentValue>
                    </Rule>
                </MapperVO>
                """,
        };

        // ── 1) Capabilities.Author sempre false, mesmo tentando forçar via payload/config ──

        [Fact]
        public async Task Sysmiddle_CapabilitiesAuthor_IsAlwaysFalse()
        {
            var cache = new FakeCachedMapperService();
            cache.Mappers.Add(BuildSysmiddleMapper("MAP_1", "%beginRuleContent;T.xMun=I.LINHA1/Campo;%endRuleContent;"));
            var adapter = new SysmiddleExplanationAdapter(cache, NullLogger<SysmiddleExplanationAdapter>.Instance);

            var explanation = await adapter.ExplainAsync(
                new MappingExplanationRequest(Guid.NewGuid(), Guid.NewGuid(), "MAP_1", "current"), CancellationToken.None);

            Assert.NotNull(explanation);
            Assert.False(explanation!.Capabilities.Author);
            Assert.True(explanation.Capabilities.Explain);
        }

        // ── 2) função desconhecida vira opaque, não erro nem invenção ──

        [Fact]
        public async Task Sysmiddle_UnknownFunction_BecomesOpaque()
        {
            var cache = new FakeCachedMapperService();
            cache.Mappers.Add(BuildSysmiddleMapper("MAP_2",
                "%beginRuleContent;T.xMun=FuncaoDesconhecidaQualquer(I.LINHA1/Campo);%endRuleContent;"));
            var adapter = new SysmiddleExplanationAdapter(cache, NullLogger<SysmiddleExplanationAdapter>.Instance);

            var explanation = await adapter.ExplainAsync(
                new MappingExplanationRequest(Guid.NewGuid(), Guid.NewGuid(), "MAP_2", "current"), CancellationToken.None);

            Assert.NotNull(explanation);
            Assert.NotEmpty(explanation!.Rules);
            Assert.Contains(explanation.Rules, r => r.SupportLevel == MappingExplanationSupportLevel.Opaque);
            Assert.True(explanation.OpaqueRuleCount >= 1);
        }

        [Fact]
        public async Task Sysmiddle_KnownFunction_IsAuthoritative()
        {
            var cache = new FakeCachedMapperService();
            cache.Mappers.Add(BuildSysmiddleMapper("MAP_3",
                "%beginRuleContent;T.xMun=GetLength(I.LINHA1/Campo);%endRuleContent;"));
            var adapter = new SysmiddleExplanationAdapter(cache, NullLogger<SysmiddleExplanationAdapter>.Instance);

            var explanation = await adapter.ExplainAsync(
                new MappingExplanationRequest(Guid.NewGuid(), Guid.NewGuid(), "MAP_3", "current"), CancellationToken.None);

            Assert.NotNull(explanation);
            Assert.Contains(explanation!.Rules, r => r.SupportLevel == MappingExplanationSupportLevel.Authoritative);
        }

        [Fact]
        public async Task Sysmiddle_UnknownMapperGuid_ReturnsNull()
        {
            var cache = new FakeCachedMapperService();
            var adapter = new SysmiddleExplanationAdapter(cache, NullLogger<SysmiddleExplanationAdapter>.Instance);

            var explanation = await adapter.ExplainAsync(
                new MappingExplanationRequest(Guid.NewGuid(), Guid.NewGuid(), "MAP_INEXISTENTE", "current"), CancellationToken.None);

            Assert.Null(explanation);
        }

        // ── 3) TCL: MappingDraftRule mapeia corretamente os campos para ExplainedRule ──

        [Fact]
        public async Task Tcl_MapsMappingDraftRuleFields_ToExplainedRule()
        {
            var workspaceId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var draftId = Guid.NewGuid();
            var ruleId = Guid.NewGuid();

            var rule = new MappingDraftRuleDetail(
                ruleId, draftId,
                SourceRefs: new[] { "I.LINHA1/Campo" },
                TargetRefs: new[] { "T.xMun" },
                Operation: "assign",
                ConditionsJson: "[]",
                TransformationsJson: "[]",
                Cardinality: "1:1",
                Evidence: new[] { new MappingDraftRuleEvidence("sample", "linha-42") },
                Confidence: "high",
                Status: MappingDraftRuleStatus.Accepted,
                OpenQuestions: Array.Empty<string>(),
                CreatedAt: DateTimeOffset.UtcNow,
                ETag: Convert.ToBase64String(new byte[8]));

            var store = new FakeDraftStore();
            store.Drafts[draftId] = new MappingDraftDetail(draftId, workspaceId, Guid.NewGuid(), Guid.NewGuid(), "tcl", DateTimeOffset.UtcNow, new[] { rule });

            var adapter = new TclExplanationAdapter(store, NullLogger<TclExplanationAdapter>.Instance);
            var explanation = await adapter.ExplainAsync(
                new MappingExplanationRequest(workspaceId, userId, draftId.ToString(), "draft"), CancellationToken.None);

            Assert.NotNull(explanation);
            var explained = Assert.Single(explanation!.Rules);
            Assert.Equal(ruleId.ToString(), explained.RuleId);
            Assert.Equal(rule.SourceRefs, explained.SourceRefs);
            Assert.Equal(rule.TargetRefs, explained.TargetRefs);
            Assert.Equal(new[] { "assign" }, explained.Operations);
            Assert.Equal("1:1", explained.Cardinality);
            Assert.Equal("sample", explained.Evidence.Single().Kind);
            Assert.Equal(MappingExplanationSupportLevel.Authoritative, explained.SupportLevel); // accepted → authoritative
        }

        [Fact]
        public async Task Tcl_ProposedRule_IsBestEffort_NotAuthoritative()
        {
            var workspaceId = Guid.NewGuid();
            var draftId = Guid.NewGuid();
            var ruleId = Guid.NewGuid();

            var rule = new MappingDraftRuleDetail(
                ruleId, draftId, new[] { "I.A" }, new[] { "T.b" }, "assign", "[]", "[]", "1:1",
                Array.Empty<MappingDraftRuleEvidence>(), "low", MappingDraftRuleStatus.Proposed,
                Array.Empty<string>(), DateTimeOffset.UtcNow, Convert.ToBase64String(new byte[8]));

            var store = new FakeDraftStore();
            store.Drafts[draftId] = new MappingDraftDetail(draftId, workspaceId, Guid.NewGuid(), Guid.NewGuid(), "tcl", DateTimeOffset.UtcNow, new[] { rule });

            var adapter = new TclExplanationAdapter(store, NullLogger<TclExplanationAdapter>.Instance);
            var explanation = await adapter.ExplainAsync(
                new MappingExplanationRequest(workspaceId, Guid.NewGuid(), draftId.ToString(), "draft"), CancellationToken.None);

            Assert.Equal(MappingExplanationSupportLevel.BestEffort, Assert.Single(explanation!.Rules).SupportLevel);
        }

        // ── 4) XSL sem XSLT real associado → unsupported com limitations, não inventa ──

        [Fact]
        public async Task Xslt_NoCompiledArtifact_ReturnsUnsupportedWithLimitations()
        {
            var workspaceId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var draftId = Guid.NewGuid();

            var store = new FakeDraftStore();
            store.Drafts[draftId] = new MappingDraftDetail(draftId, workspaceId, Guid.NewGuid(), Guid.NewGuid(), "xslt", DateTimeOffset.UtcNow, Array.Empty<MappingDraftRuleDetail>());

            var adapter = new XsltExplanationAdapter(store, NullLogger<XsltExplanationAdapter>.Instance);
            var explanation = await adapter.ExplainAsync(
                new MappingExplanationRequest(workspaceId, userId, draftId.ToString(), "draft"), CancellationToken.None);

            Assert.NotNull(explanation);
            Assert.Empty(explanation!.Rules);
            Assert.NotEmpty(explanation.Limitations);
        }

        [Fact]
        public void Xslt_ParserReal_KnownElements_AreAuthoritative_UnknownExtension_IsOpaque()
        {
            const string xslt = """
                <xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:msxsl="urn:schemas-microsoft-com:xslt">
                  <xsl:template match="/root">
                    <xsl:value-of select="campoA"/>
                    <msxsl:script implements-prefix="ext">function foo(){}</msxsl:script>
                  </xsl:template>
                </xsl:stylesheet>
                """;

            var explanation = XsltExplanationAdapter.ExplainXsltDocument("draft-1", "draft", xslt);

            Assert.Contains(explanation.Rules, r => r.SupportLevel == MappingExplanationSupportLevel.Authoritative && r.Operations.Contains("value-of"));
            Assert.Contains(explanation.Rules, r => r.SupportLevel == MappingExplanationSupportLevel.Opaque);
            Assert.True(explanation.OpaqueRuleCount >= 1);
        }

        // ── 5) Isolamento cross-workspace (TCL e XSLT, mesmo padrão dos slices anteriores) ──

        [Fact]
        public async Task Tcl_DraftFromOtherWorkspace_ReturnsNull()
        {
            var draftId = Guid.NewGuid();
            var rule = new MappingDraftRuleDetail(
                Guid.NewGuid(), draftId, new[] { "I.A" }, new[] { "T.b" }, "assign", "[]", "[]", "1:1",
                Array.Empty<MappingDraftRuleEvidence>(), "low", MappingDraftRuleStatus.Accepted,
                Array.Empty<string>(), DateTimeOffset.UtcNow, Convert.ToBase64String(new byte[8]));

            var store = new FakeDraftStore();
            store.Drafts[draftId] = new MappingDraftDetail(draftId, Guid.NewGuid() /* dono real */, Guid.NewGuid(), Guid.NewGuid(), "tcl", DateTimeOffset.UtcNow, new[] { rule });

            var adapter = new TclExplanationAdapter(store, NullLogger<TclExplanationAdapter>.Instance);

            // Chamador pede com um workspaceId DIFERENTE do dono real do draft.
            var explanation = await adapter.ExplainAsync(
                new MappingExplanationRequest(Guid.NewGuid(), Guid.NewGuid(), draftId.ToString(), "draft"), CancellationToken.None);

            Assert.Null(explanation);
        }

        [Fact]
        public async Task Xslt_DraftFromOtherWorkspace_ReturnsNull()
        {
            var draftId = Guid.NewGuid();
            var store = new FakeDraftStore();
            store.Drafts[draftId] = new MappingDraftDetail(draftId, Guid.NewGuid() /* dono real */, Guid.NewGuid(), Guid.NewGuid(), "xslt", DateTimeOffset.UtcNow, Array.Empty<MappingDraftRuleDetail>());

            var adapter = new XsltExplanationAdapter(store, NullLogger<XsltExplanationAdapter>.Instance);

            var explanation = await adapter.ExplainAsync(
                new MappingExplanationRequest(Guid.NewGuid(), Guid.NewGuid(), draftId.ToString(), "draft"), CancellationToken.None);

            Assert.Null(explanation);
        }
    }
}
