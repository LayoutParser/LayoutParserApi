using LayoutParserApi.Controllers;
using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Services.Fiscal;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Controllers
{
    /// <summary>
    /// Regressão da brecha de isolamento do <c>POST .../mapping-packages/{packageId}/drafts</c>
    /// (LayoutParserReact#196): membro do workspace A que conhecia <c>packageId</c>/<c>revisionId</c>
    /// do workspace B criava draft em A apontando para B. Agora o pacote precisa pertencer ao
    /// workspace da rota; senão a resposta é a MESMA (404) de "revisão não pertence ao pacote".
    /// </summary>
    public class MappingDraftsControllerCreateIsolationTests
    {
        private sealed class FakeCurrentUser : ICurrentUser
        {
            public string? Name { get; set; } = "u";
            public IReadOnlyList<string> Roles { get; set; } = Array.Empty<string>();
            public bool IsAuthenticated => true;
            public Guid? UserId { get; set; }
            public bool IsInRole(string role) => false;
        }

        private sealed class FakeIdentity : IIdentityWorkspaceService
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

        /// <summary>Espelha a semântica do SELECT novo: revisão do pacote E pacote do workspace.</summary>
        private sealed class FakeStore : IMappingDraftStore
        {
            public Dictionary<Guid, Guid> PackageWorkspace { get; } = new();
            public Dictionary<Guid, Guid> RevisionPackage { get; } = new();
            public int DraftsCreated { get; private set; }

            public Task<bool> RevisionBelongsToWorkspacePackageAsync(Guid workspaceId, Guid packageId, Guid revisionId, CancellationToken cancellationToken)
                => Task.FromResult(
                    RevisionPackage.TryGetValue(revisionId, out var pkg) && pkg == packageId
                    && PackageWorkspace.TryGetValue(packageId, out var ws) && ws == workspaceId);

            public Task<MappingDraftDetail> CreateDraftAsync(Guid workspaceId, Guid packageId, Guid revisionId, Guid createdByUserId, string engine, CancellationToken cancellationToken)
            {
                DraftsCreated++;
                return Task.FromResult(new MappingDraftDetail(Guid.NewGuid(), workspaceId, packageId, revisionId, engine, DateTimeOffset.UtcNow, Array.Empty<MappingDraftRuleDetail>()));
            }

            public Task<(IReadOnlyList<MappingDraftSummary> Items, int TotalCount)> ListByWorkspaceAsync(Guid workspaceId, int page, int pageSize, string? engine, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task<IReadOnlyList<ArtifactFileRef>> GetArtifactFilesForRevisionAsync(Guid revisionId, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task<MappingDraftDetail?> GetDraftIfMemberAsync(Guid draftId, Guid userId, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task<MappingDraftRuleDetail?> GetRuleIfMemberAsync(Guid draftId, Guid ruleId, Guid userId, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task InsertProposedRulesAsync(Guid draftId, Guid jobId, IReadOnlyList<MappingDraftRuleProposal> proposals, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task<UpdateRuleOutcome> UpdateRuleStatusAsync(Guid draftId, Guid ruleId, Guid userId, byte[] expectedRowVersion, string newStatus, string? justification,
                IReadOnlyList<string>? editedSourceRefs, IReadOnlyList<string>? editedTargetRefs, string? editedOperation, CancellationToken cancellationToken)
                => throw new NotSupportedException();
            public Task<MappingDraftDetail?> SetFiscalProfileAsync(Guid draftId, Guid userId, FiscalProfile profile, CancellationToken cancellationToken)
                => throw new NotSupportedException();
        }

        private sealed class NoopSuggestions : IMappingSuggestionService
        {
            public Task<Guid> EnqueueAsync(Guid draftId, Guid workspaceId, Guid revisionId, string engine, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<SuggestionJobState?> GetStatusAsync(Guid jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<bool> CancelAsync(Guid jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
        }

        private sealed class NoopResolver : IFiscalProfileResolver
        {
            public FiscalProfileValidationResult Validate(FiscalProfile profile) => throw new NotSupportedException();
            public FiscalResolvedXsd? Resolve(string documentType, string schemaVersion) => throw new NotSupportedException();
        }

        private readonly Guid _user = Guid.NewGuid();
        private readonly Guid _wsA = Guid.NewGuid();
        private readonly Guid _wsB = Guid.NewGuid();
        private readonly Guid _pkgA = Guid.NewGuid();
        private readonly Guid _revA = Guid.NewGuid();
        private readonly Guid _pkgB = Guid.NewGuid();
        private readonly Guid _revB = Guid.NewGuid();

        private (MappingDraftsController Controller, FakeStore Store) Build()
        {
            var identity = new FakeIdentity();
            identity.Memberships.Add((_wsA, _user)); // chamador é membro só do workspace A

            var store = new FakeStore();
            store.PackageWorkspace[_pkgA] = _wsA;
            store.PackageWorkspace[_pkgB] = _wsB;
            store.RevisionPackage[_revA] = _pkgA;
            store.RevisionPackage[_revB] = _pkgB;

            var controller = new MappingDraftsController(store, new NoopSuggestions(), identity, new NoopResolver(),
                new FakeCurrentUser { UserId = _user }, NullLogger<MappingDraftsController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            };
            return (controller, store);
        }

        [Fact]
        public async Task CreateDraft_PacoteERevisaoDoMesmoWorkspace_CriaDraft()
        {
            var (controller, store) = Build();

            var result = await controller.CreateDraft(_wsA, _pkgA, new CreateDraftRequest { RevisionId = _revA, Engine = "xslt" }, CancellationToken.None);

            Assert.IsType<CreatedAtActionResult>(result);
            Assert.Equal(1, store.DraftsCreated);
        }

        [Fact]
        public async Task CreateDraft_PacoteERevisaoDeOutroWorkspace_Retorna404ENaoCriaDraft()
        {
            var (controller, store) = Build();

            var result = await controller.CreateDraft(_wsA, _pkgB, new CreateDraftRequest { RevisionId = _revB, Engine = "xslt" }, CancellationToken.None);

            Assert.IsType<NotFoundResult>(result);
            Assert.Equal(0, store.DraftsCreated);
        }

        [Fact]
        public async Task CreateDraft_RevisaoDoPacoteCertoMasPackageIdDeOutroWorkspace_Retorna404ENaoCriaDraft()
        {
            var (controller, store) = Build();

            // revisão do pacote A, mas packageId da rota é o do workspace B
            var result = await controller.CreateDraft(_wsA, _pkgB, new CreateDraftRequest { RevisionId = _revA, Engine = "xslt" }, CancellationToken.None);

            Assert.IsType<NotFoundResult>(result);
            Assert.Equal(0, store.DraftsCreated);
        }

        [Fact]
        public async Task CreateDraft_RespostaCrossWorkspaceIgualARevisaoQueNaoPertenceAoPacote()
        {
            var (controller, _) = Build();

            var crossWorkspace = await controller.CreateDraft(_wsA, _pkgB, new CreateDraftRequest { RevisionId = _revB, Engine = "xslt" }, CancellationToken.None);
            var revisaoDeOutroPacote = await controller.CreateDraft(_wsA, _pkgA, new CreateDraftRequest { RevisionId = _revB, Engine = "xslt" }, CancellationToken.None);

            // Mesmo tipo/status: não revela a existência do pacote em outro workspace.
            Assert.Equal(revisaoDeOutroPacote.GetType(), crossWorkspace.GetType());
        }
    }
}
