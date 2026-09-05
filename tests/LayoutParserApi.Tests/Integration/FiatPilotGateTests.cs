using System.Text;

using LayoutParserApi.Controllers;
using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Models.Entities.Identity;
using LayoutParserApi.Services.Fiscal;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace LayoutParserApi.Tests.Integration
{
    /// <summary>
    /// Gate FIAT — issue #94, seção 14 da spec (<c>spec-plataforma-fiscal-prompt-original-2026-08-31.md</c>).
    /// Fixture 100% SINTÉTICA (nunca documento fiscal real — a spec proíbe explicitamente publicar isso
    /// no GitHub, ver §14). Cobre a sequência inteira do gate, conectando os 6 slices (2→7) num único
    /// fluxo, com fakes só nas bordas de I/O (SQL/filesystem/Ollama) — a lógica de negócio real de cada
    /// slice roda de verdade (transpilador determinístico, CanonicalDiffer, explicador XSLT, máquina de
    /// estados de governança). Ver design §5: "não automatizar o pipeline inteiro num único teste caro,
    /// dividir por transição" — aqui é um teste por item do checklist §14, encadeados na mesma fixture.
    /// </summary>
    public class FiatPilotGateTests
    {
        // ---------------------------------------------------------------
        // Fakes de borda (SQL/filesystem) — mesma raiz de padrão dos testes de cada slice.
        // ---------------------------------------------------------------

        private sealed class FakeCurrentUser : ICurrentUser
        {
            public string? Name { get; set; }
            public IReadOnlyList<string> Roles { get; set; } = Array.Empty<string>();
            public bool IsAuthenticated => Name != null;
            public Guid? UserId { get; set; }
            public bool IsInRole(string role) => Roles.Contains(role, StringComparer.OrdinalIgnoreCase);
        }

        private sealed class FakeIdentityWorkspaceStore : IIdentityWorkspaceStore
        {
            public Dictionary<(Guid WorkspaceId, Guid UserId), string> Memberships { get; } = new();

            public Task<Guid> ResolveOrCreateUserAsync(string provider, string tenantOrIssuer, string subject, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task<WorkspaceSummary> EnsurePersonalWorkspaceAsync(Guid userId, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task<IReadOnlyList<WorkspaceSummary>> GetMembershipsAsync(Guid userId, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task<WorkspaceSummary?> GetWorkspaceIfMemberAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken)
            {
                if (!Memberships.TryGetValue((workspaceId, userId), out var role))
                    return Task.FromResult<WorkspaceSummary?>(null);
                return Task.FromResult<WorkspaceSummary?>(new WorkspaceSummary(workspaceId, "Workspace FIAT", "team", role, DateTimeOffset.UtcNow));
            }
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
                    ? new WorkspaceSummary(workspaceId, "Workspace FIAT", "team", "fiscal_admin", DateTimeOffset.UtcNow)
                    : null);
        }

        /// <summary>Fake em memória, mesmo comportamento observável de <c>SqlFiscalPackageStore</c> (grava sem SQL real).</summary>
        private sealed class FakeFiscalPackageStore : IFiscalPackageStore
        {
            public Dictionary<Guid, PackageDetail> Packages { get; } = new();

            public Task<bool> EnsureProjectExistsAsync(Guid workspaceId, Guid projectId, CancellationToken cancellationToken) => Task.FromResult(true);

            public Task<PackageDetail> CreatePackageAsync(Guid workspaceId, Guid projectId, Guid createdByUserId, string packageName, string idempotencyKey, IReadOnlyList<PackageArtifact> artifacts, CancellationToken cancellationToken)
            {
                var revision = new RevisionSummary(Guid.NewGuid(), 1, DateTimeOffset.UtcNow,
                    artifacts.Select(a => new ArtifactSummary(a.ArtifactId, a.Kind, a.Sha256, a.SizeBytes, a.OriginalFileName, a.InspectionStatus, DateTimeOffset.UtcNow)).ToList());
                var detail = new PackageDetail(Guid.NewGuid(), workspaceId, projectId, packageName, DateTimeOffset.UtcNow, revision);
                Packages[detail.PackageId] = detail;
                return Task.FromResult(detail);
            }

            public Task<PackageDetail?> GetPackageIfMemberAsync(Guid packageId, Guid userId, CancellationToken cancellationToken)
                => Task.FromResult(Packages.TryGetValue(packageId, out var p) ? p : null);

            public Task<PackageDetail?> FindPackageByIdempotencyKeyAsync(Guid workspaceId, Guid projectId, string idempotencyKey, CancellationToken cancellationToken)
                => Task.FromResult<PackageDetail?>(null); // fixture sempre "primeira vez" — sem duplicata a resolver.

            public Task<ArtifactSummary?> FindArtifactByHashAsync(Guid packageId, string sha256, CancellationToken cancellationToken)
                => Task.FromResult<ArtifactSummary?>(null);

            public Task UpdateInspectionStatusAsync(Guid artifactId, string inspectionStatus, CancellationToken cancellationToken) => Task.CompletedTask;

            public Task<IReadOnlyList<ProjectSummary>> ListProjectsForMemberAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken)
                => Task.FromResult<IReadOnlyList<ProjectSummary>>(Array.Empty<ProjectSummary>());

            public Task<PackageDetail> CreateRevisionAsync(Guid packageId, Guid createdByUserId, IReadOnlyList<PackageArtifact> artifacts, CancellationToken cancellationToken)
                => throw new NotSupportedException("Não exercitado neste conjunto de testes.");

            public Task<string?> GetArtifactStoragePathAsync(Guid artifactId, CancellationToken cancellationToken)
                => Task.FromResult<string?>(null);
        }

        private sealed class NoOpAntivirusScanner : IAntivirusScanner
        {
            // Ambiente de CI sem Defender acessível — comportamento real "indisponível" (null), não um "sempre limpo" fake ingênuo.
            public Task<bool?> ScanAsync(string filePath, CancellationToken cancellationToken) => Task.FromResult<bool?>(null);
        }

        private sealed class FakeDraftStore : IMappingDraftStore
        {
            public Dictionary<Guid, MappingDraftDetail> Drafts { get; } = new();
            public Dictionary<(Guid DraftId, Guid RuleId), MappingDraftRuleDetail> Rules { get; } = new();

            public Task<bool> RevisionBelongsToPackageAsync(Guid packageId, Guid revisionId, CancellationToken cancellationToken) => Task.FromResult(true);
            public Task<IReadOnlyList<ArtifactFileRef>> GetArtifactFilesForRevisionAsync(Guid revisionId, CancellationToken cancellationToken)
                => Task.FromResult<IReadOnlyList<ArtifactFileRef>>(Array.Empty<ArtifactFileRef>());

            public Task<MappingDraftDetail> CreateDraftAsync(Guid workspaceId, Guid packageId, Guid revisionId, Guid createdByUserId, string engine, CancellationToken cancellationToken)
            {
                var draft = new MappingDraftDetail(Guid.NewGuid(), workspaceId, packageId, revisionId, engine, DateTimeOffset.UtcNow, Array.Empty<MappingDraftRuleDetail>());
                Drafts[draft.DraftId] = draft;
                return Task.FromResult(draft);
            }

            public Task<MappingDraftDetail?> GetDraftIfMemberAsync(Guid draftId, Guid userId, CancellationToken cancellationToken)
                => Task.FromResult(RebuildDraftWithCurrentRules(draftId));

            public Task<MappingDraftRuleDetail?> GetRuleIfMemberAsync(Guid draftId, Guid ruleId, Guid userId, CancellationToken cancellationToken)
                => Task.FromResult(Rules.TryGetValue((draftId, ruleId), out var r) ? r : null);

            public Task InsertProposedRulesAsync(Guid draftId, Guid jobId, IReadOnlyList<MappingDraftRuleProposal> proposals, CancellationToken cancellationToken)
            {
                foreach (var p in proposals)
                {
                    var ruleId = Guid.NewGuid();
                    var detail = new MappingDraftRuleDetail(
                        ruleId, draftId, p.SourceRefs, p.TargetRefs, p.Operation, p.ConditionsJson, p.TransformationsJson,
                        p.Cardinality, p.Evidence, p.Confidence, p.Status, p.OpenQuestions, DateTimeOffset.UtcNow,
                        Convert.ToBase64String(Guid.NewGuid().ToByteArray()[..8]));
                    Rules[(draftId, ruleId)] = detail;
                }
                return Task.CompletedTask;
            }

            public Task<UpdateRuleOutcome> UpdateRuleStatusAsync(Guid draftId, Guid ruleId, Guid userId, byte[] expectedRowVersion, string newStatus, string? justification, IReadOnlyList<string>? editedSourceRefs, IReadOnlyList<string>? editedTargetRefs, string? editedOperation, CancellationToken cancellationToken)
            {
                if (!Rules.TryGetValue((draftId, ruleId), out var rule))
                    return Task.FromResult(new UpdateRuleOutcome(UpdateRuleResult.NotFound, null));

                var currentRowVersion = Convert.FromBase64String(rule.ETag);
                if (!currentRowVersion.SequenceEqual(expectedRowVersion))
                    return Task.FromResult(new UpdateRuleOutcome(UpdateRuleResult.Conflict, null));

                var updated = rule with
                {
                    Status = newStatus,
                    SourceRefs = editedSourceRefs ?? rule.SourceRefs,
                    TargetRefs = editedTargetRefs ?? rule.TargetRefs,
                    Operation = editedOperation ?? rule.Operation,
                    ETag = Convert.ToBase64String(Guid.NewGuid().ToByteArray()[..8]),
                };
                Rules[(draftId, ruleId)] = updated;
                return Task.FromResult(new UpdateRuleOutcome(UpdateRuleResult.Success, updated));
            }

            private MappingDraftDetail? RebuildDraftWithCurrentRules(Guid draftId)
            {
                if (!Drafts.TryGetValue(draftId, out var draft))
                    return null;
                var currentRules = Rules.Where(kv => kv.Key.DraftId == draftId).Select(kv => kv.Value).ToList();
                return draft with { Rules = currentRules };
            }
        }

        /// <summary>Reproduz a MESMA regra de negócio do <c>SqlMappingReleaseStore</c> — igual ao dublê de <c>MappingGovernanceControllerTests</c>.</summary>
        private sealed class FakeReleaseStore : IMappingReleaseStore
        {
            public Dictionary<Guid, MappingReleaseDetail> ById { get; } = new();
            public Dictionary<(Guid DraftId, string Hash), MappingReleaseDetail> ByHash { get; } = new();
            public List<(Guid ReleaseId, string From, string To, Guid Actor, string? Justification)> Transitions { get; } = new();

            public Task<MappingReleaseDetail> CreateOrGetCompiledReleaseAsync(Guid workspaceId, Guid draftId, string engine, string rulesSnapshotHash, IReadOnlyList<Guid> sourceRuleIds, IReadOnlyList<MappingReleaseArtifact> artifacts, IReadOnlyList<MappingReleaseCompileDiagnostic> compileDiagnostics, string correlationId, Guid jobId, CancellationToken cancellationToken)
            {
                if (ByHash.TryGetValue((draftId, rulesSnapshotHash), out var existing))
                    return Task.FromResult(existing);

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

            public Task<MappingReleaseDetail?> ApplyTestRunResultAsync(Guid releaseId, MappingTestRunSummary summary, CancellationToken cancellationToken)
            {
                if (!ById.TryGetValue(releaseId, out var existing))
                    return Task.FromResult<MappingReleaseDetail?>(null);
                var updated = existing with
                {
                    TestRunSummary = summary,
                    Status = summary.RequiredGatesPassed ? MappingReleaseStatus.TestPassed : MappingReleaseStatus.TestFailed,
                };
                ById[releaseId] = updated;
                return Task.FromResult<MappingReleaseDetail?>(updated);
            }

            public Task<MappingReleaseDetail> ApproveAsync(Guid releaseId, Guid actorUserId, string justification, CancellationToken cancellationToken)
            {
                var current = ById[releaseId];
                if (current.Status != MappingReleaseStatus.TestPassed)
                    throw new InvalidOperationException($"Release {releaseId} está em \"{current.Status}\"; aprovação exige \"{MappingReleaseStatus.TestPassed}\".");

                Transitions.Add((releaseId, MappingReleaseStatus.TestPassed, MappingReleaseStatus.InReview, actorUserId, justification));
                Transitions.Add((releaseId, MappingReleaseStatus.InReview, MappingReleaseStatus.Approved, actorUserId, justification));

                var updated = current with { Status = MappingReleaseStatus.Approved, ApprovedByUserId = actorUserId, ApprovedAt = DateTimeOffset.UtcNow, ApprovalJustification = justification };
                ById[releaseId] = updated;
                return Task.FromResult(updated);
            }

            public Task<MappingReleaseDetail> PublishAsync(Guid releaseId, Guid actorUserId, string environment, CancellationToken cancellationToken)
            {
                var current = ById[releaseId];
                if (current.Status != MappingReleaseStatus.Approved)
                    throw new InvalidOperationException($"Release {releaseId} está em \"{current.Status}\"; publicação exige \"{MappingReleaseStatus.Approved}\".");

                var previousPublished = ById.Values.FirstOrDefault(r => r.DraftId == current.DraftId && r.Status == MappingReleaseStatus.Published && r.ReleaseId != releaseId);
                if (previousPublished != null)
                {
                    Transitions.Add((previousPublished.ReleaseId, MappingReleaseStatus.Published, MappingReleaseStatus.Deprecated, actorUserId, "Substituída."));
                    ById[previousPublished.ReleaseId] = previousPublished with { Status = MappingReleaseStatus.Deprecated };
                }

                Transitions.Add((releaseId, MappingReleaseStatus.Approved, MappingReleaseStatus.Published, actorUserId, $"Publicado em \"{environment}\"."));
                var updated = current with
                {
                    Status = MappingReleaseStatus.Published,
                    Environment = environment,
                    PublishedByUserId = actorUserId,
                    PublishedAt = DateTimeOffset.UtcNow,
                    PreviousPublishedReleaseId = previousPublished?.ReleaseId,
                };
                ById[releaseId] = updated;
                return Task.FromResult(updated);
            }

            public Task<MappingReleaseDetail> RollbackAsync(Guid releaseId, Guid actorUserId, CancellationToken cancellationToken)
                => throw new NotSupportedException("Não exercitado neste gate — coberto em MappingGovernanceControllerTests.");
        }

        private static IServiceScopeFactory BuildScopeFactory(FakeDraftStore draftStore, FakeReleaseStore releaseStore)
        {
            var services = new ServiceCollection();
            services.AddSingleton<IMappingDraftStore>(draftStore);
            services.AddSingleton<IMappingReleaseStore>(releaseStore);
            services.AddSingleton<LayoutParserApi.Services.XmlAnalysis.XmlDocumentTypeDetector>();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            services.AddSingleton<LayoutParserApi.Services.XmlAnalysis.XsdValidationService>();
            var provider = services.BuildServiceProvider();
            return provider.GetRequiredService<IServiceScopeFactory>();
        }

        private static async Task<TState> WaitForCompletionAsync<TState>(Func<Task<TState?>> poll, Func<TState, bool> isDone) where TState : class
        {
            for (var i = 0; i < 300; i++)
            {
                var state = await poll();
                if (state != null && isDone(state))
                    return state;
                await Task.Delay(10);
            }
            throw new TimeoutException("Job não concluiu a tempo.");
        }

        // ---------------------------------------------------------------
        // Fixture sintética FIAT — NF-e simplificada, sem dado real de cliente (spec §14).
        // ---------------------------------------------------------------

        private const string SyntheticSampleTxt = "C0000000012345678000199FIAT AUTOMOVEIS SA\r\n";
        private const string SyntheticLayoutXml = "<MAP><LINE identifier=\"C\" name=\"Cabecalho\"><FIELD name=\"cUF\" length=\"2\"/><FIELD name=\"CNPJ\" length=\"14\"/></LINE></MAP>";

        /// <summary>Item 3 do gate: "IA propõe regras com evidência" — simula a saída determinística de
        /// <c>IMappingSuggestionService</c> (Ollama mockado, não chamado de verdade — fixture sintética).
        /// Uma regra clara (1:1, alta confiança) + uma regra AMBÍGUA que vira <c>needs_input</c> (item 4).</summary>
        private static IReadOnlyList<MappingDraftRuleProposal> SyntheticAiProposals() => new[]
        {
            new MappingDraftRuleProposal(
                SourceRefs: new[] { "/sample/C/CNPJ" },
                TargetRefs: new[] { "/nfe/emit/CNPJ" },
                Operation: "copy",
                ConditionsJson: "[]",
                TransformationsJson: "[]",
                Cardinality: "1:1",
                Evidence: new[] { new MappingDraftRuleEvidence("sample", "linha 1, posição 3-16") },
                Confidence: "high",
                Status: MappingDraftRuleStatus.Proposed,
                OpenQuestions: Array.Empty<string>()),
            new MappingDraftRuleProposal(
                SourceRefs: new[] { "/sample/C/cUF" },
                TargetRefs: new[] { "/nfe/ide/cUF" },
                Operation: "lookup",
                ConditionsJson: "[]",
                TransformationsJson: "[]",
                Cardinality: "1:1",
                Evidence: new[] { new MappingDraftRuleEvidence("sample", "linha 1, posição 1-2") },
                Confidence: "low",
                Status: MappingDraftRuleStatus.NeedsInput,
                OpenQuestions: new[] { "Tabela de código de UF não está no pacote — qual fonte usar pra 'cUF' de 2 dígitos?" }),
        };

        [Fact]
        public async Task Gate_FIAT_ponta_a_ponta_conecta_os_6_slices_com_fixture_sintetica()
        {
            var workspaceId = Guid.NewGuid();
            var userAnalyst = Guid.NewGuid();
            var userReviewer = Guid.NewGuid();
            var correlationId = $"fiat-gate-{Guid.NewGuid():N}";

            // ---- 1) Pacote criado no workspace correto (Slice 2) ----
            var packageStore = new FakeFiscalPackageStore();
            var tempStorePath = Path.Combine(Path.GetTempPath(), "lp-fiat-gate-tests", Guid.NewGuid().ToString("N"));
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ML:FiscalMappingPackagesPath"] = tempStorePath })
                .Build();
            var packageService = new FiscalPackageService(packageStore, new NoOpAntivirusScanner(), new FiscalMappingRuleExtractor(NullLogger<FiscalMappingRuleExtractor>.Instance), NullLogger<FiscalPackageService>.Instance, configuration);

            var artifacts = new List<UploadedArtifactInput>
            {
                new(ArtifactKind.Sample, "amostra_fiat.txt", "text/plain", Encoding.UTF8.GetBytes(SyntheticSampleTxt)),
                new(ArtifactKind.Layout, "layout_fiat.xml", "application/xml", Encoding.UTF8.GetBytes(SyntheticLayoutXml)),
            };

            CreatePackageOutcome outcome;
            try
            {
                outcome = await packageService.CreatePackageAsync(workspaceId, Guid.NewGuid(), userAnalyst, "Pacote FIAT NF-e 4.00 (sintético)", null, artifacts, CancellationToken.None);
                Assert.True(outcome.Success, outcome.Error);
            }
            finally
            {
                if (Directory.Exists(tempStorePath))
                    Directory.Delete(tempStorePath, recursive: true);
            }

            var package = outcome.Package!;
            Assert.Equal(workspaceId, package.WorkspaceId);

            // ---- 2) Inventário determinístico dos artefatos ----
            // Mesmo conteúdo -> mesmo sha256, sempre, e os 2 artefatos aparecem no inventário da revisão.
            Assert.Equal(2, package.LatestRevision.Artifacts.Count);
            var sampleArtifact = package.LatestRevision.Artifacts.Single(a => a.Kind == ArtifactKind.Sample);
            var expectedSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(SyntheticSampleTxt))).ToLowerInvariant();
            Assert.Equal(expectedSha, sampleArtifact.Sha256);

            // ---- 3) IA propõe regras com evidência (Slice 3) — Ollama mockado pela fixture sintética,
            //         não chamado de verdade. O DraftStore é a fronteira mockada. ----
            var draftStore = new FakeDraftStore();
            var draft = await draftStore.CreateDraftAsync(workspaceId, package.PackageId, package.LatestRevision.RevisionId, userAnalyst, "xslt", CancellationToken.None);
            await draftStore.InsertProposedRulesAsync(draft.DraftId, Guid.NewGuid(), SyntheticAiProposals(), CancellationToken.None);

            var draftAfterProposals = await draftStore.GetDraftIfMemberAsync(draft.DraftId, userAnalyst, CancellationToken.None);
            Assert.NotNull(draftAfterProposals);
            Assert.Equal(2, draftAfterProposals!.Rules.Count);
            var clearRule = draftAfterProposals.Rules.Single(r => r.Confidence == "high");
            var ambiguousRule = draftAfterProposals.Rules.Single(r => r.Confidence == "low");
            Assert.Contains(clearRule.Evidence, e => e.Reference.Contains("linha 1"));

            // ---- 4) Ambiguidade vira pergunta (needs_input) ----
            Assert.Equal(MappingDraftRuleStatus.NeedsInput, ambiguousRule.Status);
            Assert.NotEmpty(ambiguousRule.OpenQuestions);

            // ---- 5) Especialista revisa/aceita campos obrigatórios (Slice 3) — ETag/If-Match real ----
            var identityWorkspaceService = new FakeIdentityWorkspaceService();
            identityWorkspaceService.Memberships.Add((workspaceId, userReviewer));
            var suggestionServiceStub = new StubSuggestionService();
            var draftsController = new MappingDraftsController(draftStore, suggestionServiceStub, identityWorkspaceService, new FakeCurrentUser { UserId = userReviewer }, NullLogger<MappingDraftsController>.Instance);
            draftsController.ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };
            draftsController.Request.Headers["If-Match"] = clearRule.ETag;

            var acceptResult = await draftsController.UpdateRule(workspaceId, draft.DraftId, clearRule.RuleId, new UpdateRuleRequest { Status = MappingDraftRuleStatus.Accepted }, CancellationToken.None);
            Assert.IsType<OkObjectResult>(acceptResult);

            // A regra ambígua (needs_input) permanece pendente — nunca vira accepted/edited sem decisão humana.
            var draftAfterReview = await draftStore.GetDraftIfMemberAsync(draft.DraftId, userReviewer, CancellationToken.None);
            Assert.Equal(MappingDraftRuleStatus.Accepted, draftAfterReview!.Rules.Single(r => r.RuleId == clearRule.RuleId).Status);
            Assert.Equal(MappingDraftRuleStatus.NeedsInput, draftAfterReview.Rules.Single(r => r.RuleId == ambiguousRule.RuleId).Status);

            // ---- 6) TCL/XSL/XSLT gerados (Slice 5, transpilador determinístico) — só a regra aceita é emitida ----
            var releaseStore = new FakeReleaseStore();
            var scopeFactory = BuildScopeFactory(draftStore, releaseStore);
            var compileService = new MappingCompileService(NullLogger<MappingCompileService>.Instance, scopeFactory);

            var compileJobId = await compileService.EnqueueAsync(workspaceId, draft.DraftId, userReviewer, correlationId, CancellationToken.None);
            var compileState = await WaitForCompletionAsync(() => compileService.GetStatusAsync(compileJobId, CancellationToken.None), s => s.Status is CompileJobStatus.Completed or CompileJobStatus.Failed);
            Assert.Equal(CompileJobStatus.Completed, compileState.Status);
            Assert.NotNull(compileState.ReleaseId);

            var release = releaseStore.ById[compileState.ReleaseId!.Value];
            Assert.Equal(MappingReleaseStatus.DraftCompiled, release.Status);
            var xsltArtifact = Assert.Single(release.Artifacts);
            Assert.Equal("xslt", xsltArtifact.Kind);
            Assert.Contains("value-of", xsltArtifact.Content); // regra "copy" -> xsl:value-of, gerado de verdade.
            // needs_input nunca entra no artefato compilado — só o RuleId da regra ACEITA aparece na proveniência.
            Assert.Contains(clearRule.RuleId.ToString(), xsltArtifact.Content);
            Assert.DoesNotContain(ambiguousRule.RuleId.ToString(), xsltArtifact.Content);

            // ---- 7) Código é explicável (Slice 4) — parser real do explicador XSLT sobre o artefato compilado ----
            var explanation = XsltExplanationAdapter.ExplainXsltDocument(draft.DraftId.ToString(), "draft", xsltArtifact.Content);
            Assert.Equal("xslt", explanation.Engine);
            Assert.Contains(explanation.Rules, r => r.Operations.Contains("value-of") && r.SupportLevel == LayoutParserApi.Models.Dtos.Fiscal.MappingExplanationSupportLevel.Authoritative);
            Assert.DoesNotContain(explanation.Limitations, l => l.Contains("ilegível"));

            // ---- 8/9/10) Execução produz XML válido, comparado ao gabarito, com provenance nas divergências ----
            var testRunService = new MappingTestRunService(NullLogger<MappingTestRunService>.Instance, scopeFactory);

            var inputXmlBatendoComGabarito = "<sample><C><CNPJ>12345678000199</CNPJ></C></sample>";
            // O nome do elemento raiz do XSLT é "root{PackageId:N}" (ver MappingCompileService) — resolve
            // dinamicamente aqui em vez de fixar um literal.
            var rootName = System.Xml.Linq.XDocument.Parse(xsltArtifact.Content)
                .Descendants().First(e => e.Name.LocalName == "template")
                .Elements().First(e => e.Name.LocalName != "text").Name.LocalName;
            // O transpilador emite um elemento FLAT por regra (nome = último segmento do targetRef,
            // "CNPJ" para "/nfe/emit/CNPJ") direto sob a raiz — não recria a árvore /nfe/emit/... .
            var gabaritoBatendo = $"<{rootName}><CNPJ>12345678000199</CNPJ></{rootName}>";

            var testRunJobIdOk = await testRunService.EnqueueAsync(workspaceId, draft.DraftId, release.ReleaseId, userReviewer, inputXmlBatendoComGabarito, gabaritoBatendo, null, correlationId, CancellationToken.None);
            var testRunStateOk = await WaitForCompletionAsync(() => testRunService.GetStatusAsync(testRunJobIdOk, CancellationToken.None), s => s.Status is TestRunJobStatus.Completed or TestRunJobStatus.Failed);

            Assert.Equal(TestRunJobStatus.Completed, testRunStateOk.Status);
            Assert.True(testRunStateOk.RequiredGatesPassed, "XML gerado deveria bater com o gabarito sintético — checar o rootName resolvido dinamicamente.");
            var releaseAposTestRunOk = releaseStore.ById[release.ReleaseId];
            Assert.Equal(MappingReleaseStatus.TestPassed, releaseAposTestRunOk.Status);
            Assert.Empty(releaseAposTestRunOk.TestRunSummary!.Divergences);

            // ---- 10) Divergências têm provenance — repete com gabarito propositalmente diferente ----
            var gabaritoDivergente = $"<{rootName}><CNPJ>00000000000000</CNPJ></{rootName}>";
            var testRunJobIdDivergente = await testRunService.EnqueueAsync(workspaceId, draft.DraftId, release.ReleaseId, userReviewer, inputXmlBatendoComGabarito, gabaritoDivergente, null, correlationId, CancellationToken.None);
            var testRunStateDivergente = await WaitForCompletionAsync(() => testRunService.GetStatusAsync(testRunJobIdDivergente, CancellationToken.None), s => s.Status is TestRunJobStatus.Completed or TestRunJobStatus.Failed);

            Assert.False(testRunStateDivergente.RequiredGatesPassed);
            var releaseAposTestRunDivergente = releaseStore.ById[release.ReleaseId];
            Assert.Equal(MappingReleaseStatus.TestFailed, releaseAposTestRunDivergente.Status);
            var divergencia = Assert.Single(releaseAposTestRunDivergente.TestRunSummary!.Divergences);
            Assert.Equal(clearRule.RuleId, divergencia.RuleId); // provenance: nó divergente -> regra de origem -> evidência.
            Assert.Equal(clearRule.SourceRefs, divergencia.SourceRefs);

            // Restaura o release ao estado "test_passed" pra seguir o fluxo de governança adiante.
            releaseStore.ById[release.ReleaseId] = releaseAposTestRunOk;

            // ---- 11) Regressão antecede publicação (Slice 7) — bloqueio explícito de test_failed ----
            var governanceController = new MappingGovernanceController(releaseStore, new FakeCurrentUser { UserId = userReviewer }, NullLogger<MappingGovernanceController>.Instance);

            releaseStore.ById[release.ReleaseId] = releaseAposTestRunDivergente; // simula tentar publicar um release que falhou
            var publishSemAprovarComFalha = await governanceController.Publish(workspaceId, release.ReleaseId, null, CancellationToken.None);
            Assert.IsType<UnprocessableEntityObjectResult>(publishSemAprovarComFalha); // nem chegou a "approved" — publish recusa.
            var approveComTestFailed = await governanceController.Approve(workspaceId, release.ReleaseId, new ApproveReleaseRequest { Justification = "tentativa indevida" }, CancellationToken.None);
            Assert.IsType<UnprocessableEntityObjectResult>(approveComTestFailed);

            releaseStore.ById[release.ReleaseId] = releaseAposTestRunOk; // volta ao estado test_passed (regressão OK)
            var approveResult = await governanceController.Approve(workspaceId, release.ReleaseId, new ApproveReleaseRequest { Justification = "Revisado por especialista fiscal — regra clara, evidência conferida." }, CancellationToken.None);
            Assert.IsType<OkObjectResult>(approveResult);
            Assert.Equal(MappingReleaseStatus.Approved, releaseStore.ById[release.ReleaseId].Status);

            var publishResult = await governanceController.Publish(workspaceId, release.ReleaseId, new PublishReleaseRequest { Environment = "validation" }, CancellationToken.None);
            Assert.IsType<OkObjectResult>(publishResult);
            Assert.Equal(MappingReleaseStatus.Published, releaseStore.ById[release.ReleaseId].Status);

            // ---- 12) Todos os correlation IDs são conectáveis — o mesmo correlationId flui do compile ao test-run ----
            Assert.Equal(correlationId, release.CorrelationId);
            // MappingTransition (governança) carrega o releaseId, que por sua vez carrega o correlationId original —
            // cadeia rastreável package -> draft -> release(correlationId) -> transitions, sem elo perdido.
            Assert.Contains(releaseStore.Transitions, t => t.ReleaseId == release.ReleaseId && t.To == MappingReleaseStatus.Approved);
            Assert.Contains(releaseStore.Transitions, t => t.ReleaseId == release.ReleaseId && t.To == MappingReleaseStatus.Published);

            // ---- 13) Nenhum artefato Sysmiddle é alterado — MappingEngineGuardFilter recusa engine=sysmiddle ----
            var guardFilter = new LayoutParserApi.Services.Filters.MappingEngineGuardFilter();
            var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            httpContext.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString("?engine=sysmiddle");
            var routeData = new Microsoft.AspNetCore.Routing.RouteData();
            var actionContext = new Microsoft.AspNetCore.Mvc.ActionContext(httpContext, routeData, new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
            var actionExecutingContext = new Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext(
                actionContext, new List<Microsoft.AspNetCore.Mvc.Filters.IFilterMetadata>(), new Dictionary<string, object?>(), controller: new object());

            var nextCalled = false;
            await guardFilter.OnActionExecutionAsync(actionExecutingContext, () =>
            {
                nextCalled = true;
                return Task.FromResult(new Microsoft.AspNetCore.Mvc.Filters.ActionExecutedContext(actionContext, new List<Microsoft.AspNetCore.Mvc.Filters.IFilterMetadata>(), controller: new object()));
            });

            Assert.False(nextCalled); // pipeline nunca chega no controller — bloqueado na fronteira.
            var guardResult = Assert.IsType<UnprocessableEntityObjectResult>(actionExecutingContext.Result);
            Assert.Equal(Microsoft.AspNetCore.Http.StatusCodes.Status422UnprocessableEntity, guardResult.StatusCode);
        }

        /// <summary>Stub inerte — não é exercitado neste gate (a proposta de regras entra direto via <c>InsertProposedRulesAsync</c>, simulando o resultado JÁ produzido pelo job real de sugestão).</summary>
        private sealed class StubSuggestionService : IMappingSuggestionService
        {
            public Task<Guid> EnqueueAsync(Guid draftId, Guid workspaceId, Guid revisionId, string engine, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task<SuggestionJobState?> GetStatusAsync(Guid jobId, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task<bool> CancelAsync(Guid jobId, CancellationToken cancellationToken)
                => throw new NotSupportedException();
        }
    }
}
