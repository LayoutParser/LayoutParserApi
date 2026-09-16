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
    /// Issue #416 — <c>GET .../mapping-drafts</c>. Não havia NENHUM endpoint de descoberta de
    /// drafts: o único de leitura (<c>GetDraft</c>) exige o GUID de antemão. Mesmo padrão de
    /// listagem do <c>MappingGovernanceController.List</c> (issue #198/#377) — paginação,
    /// isolamento por workspace, filtro opcional (aqui só <c>engine</c>; draft não tem "status"
    /// próprio como release tem).
    /// </summary>
    public class MappingDraftsControllerListTests
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
            public Task<Guid?> ResolveOrCreateUserAsync(string provider, string? tenantOrIssuer, string subject, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task<WorkspaceMeResult> GetOrCreateMyWorkspacesAsync(Guid userId, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task<WorkspaceSummary?> GetWorkspaceForMemberAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken)
                => throw new NotSupportedException("Não exercitado por este teste — RBAC do endpoint é filtro de rota, não checado no controller diretamente.");
        }

        /// <summary>Reproduz o WHERE condicional do <c>SqlMappingDraftStore.ListByWorkspaceAsync</c> — filtro nulo/vazio não entra.</summary>
        private sealed class FakeDraftStore : IMappingDraftStore
        {
            public List<MappingDraftSummary> All { get; } = new();

            public Task<bool> RevisionBelongsToPackageAsync(Guid packageId, Guid revisionId, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task<IReadOnlyList<ArtifactFileRef>> GetArtifactFilesForRevisionAsync(Guid revisionId, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task<MappingDraftDetail> CreateDraftAsync(Guid workspaceId, Guid packageId, Guid revisionId, Guid createdByUserId, string engine, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task<MappingDraftDetail?> GetDraftIfMemberAsync(Guid draftId, Guid userId, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task<MappingDraftRuleDetail?> GetRuleIfMemberAsync(Guid draftId, Guid ruleId, Guid userId, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task InsertProposedRulesAsync(Guid draftId, Guid jobId, IReadOnlyList<MappingDraftRuleProposal> proposals, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task<UpdateRuleOutcome> UpdateRuleStatusAsync(
                Guid draftId, Guid ruleId, Guid userId, byte[] expectedRowVersion, string newStatus, string? justification,
                IReadOnlyList<string>? editedSourceRefs, IReadOnlyList<string>? editedTargetRefs, string? editedOperation,
                CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task<MappingDraftDetail?> SetFiscalProfileAsync(Guid draftId, Guid userId, FiscalProfile profile, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task<(IReadOnlyList<MappingDraftSummary> Items, int TotalCount)> ListByWorkspaceAsync(
                Guid workspaceId, int page, int pageSize, string? engine, CancellationToken cancellationToken)
            {
                var filtrado = All.Where(d => d.WorkspaceId == workspaceId);
                if (!string.IsNullOrWhiteSpace(engine))
                    filtrado = filtrado.Where(d => d.Engine == engine);
                var ordenado = filtrado.OrderByDescending(d => d.CreatedAt).ToList();
                var pagina = ordenado.Skip((page - 1) * pageSize).Take(pageSize).ToList();
                return Task.FromResult(((IReadOnlyList<MappingDraftSummary>)pagina, ordenado.Count));
            }
        }

        /// <summary>Stub sempre-válido — não exercitado nestes testes (resposta só usa Resolve quando fiscalProfile != null).</summary>
        private sealed class FakeFiscalProfileResolver : IFiscalProfileResolver
        {
            public FiscalProfileValidationResult Validate(FiscalProfile profile)
                => new(true, null, new FiscalResolvedXsd(profile.SchemaVersion, "urn:test", "Root"));

            public FiscalResolvedXsd? Resolve(string documentType, string schemaVersion)
                => new(schemaVersion, "urn:test", "Root");
        }

        private static MappingDraftsController BuildController(FakeDraftStore store, FakeCurrentUser? user = null)
        {
            var controller = new MappingDraftsController(
                store,
                suggestionService: null!,
                identityWorkspaceService: new FakeIdentityWorkspaceService(),
                fiscalProfileResolver: new FakeFiscalProfileResolver(),
                currentUser: user ?? new FakeCurrentUser { UserId = Guid.NewGuid() },
                logger: NullLogger<MappingDraftsController>.Instance);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            return controller;
        }

        private static MappingDraftSummary NewDraft(Guid workspaceId, string engine = "xslt", FiscalProfile? fiscalProfile = null) => new(
            Guid.NewGuid(), workspaceId, Guid.NewGuid(), Guid.NewGuid(), engine, DateTimeOffset.UtcNow, RulesCount: 0, FiscalProfile: fiscalProfile);

        private static (int TotalCount, List<object> Items) ReadListPayload(IActionResult result)
        {
            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = ok.Value!;
            var totalCount = (int)payload.GetType().GetProperty("totalCount")!.GetValue(payload)!;
            var items = ((System.Collections.IEnumerable)payload.GetType().GetProperty("items")!.GetValue(payload)!).Cast<object>().ToList();
            return (totalCount, items);
        }

        [Fact]
        public async Task Workspace_sem_drafts_retorna_lista_vazia()
        {
            var store = new FakeDraftStore();
            var controller = BuildController(store);

            var (totalCount, items) = ReadListPayload(await controller.ListDrafts(Guid.NewGuid(), cancellationToken: CancellationToken.None));

            Assert.Equal(0, totalCount);
            Assert.Empty(items);
        }

        [Fact]
        public async Task Lista_paginada_traz_total_real_mesmo_com_pagina_menor()
        {
            var store = new FakeDraftStore();
            var workspaceId = Guid.NewGuid();
            for (var i = 0; i < 5; i++)
                store.All.Add(NewDraft(workspaceId));

            var controller = BuildController(store);
            var (totalCount, items) = ReadListPayload(await controller.ListDrafts(workspaceId, page: 1, pageSize: 2, cancellationToken: CancellationToken.None));

            Assert.Equal(5, totalCount);
            Assert.Equal(2, items.Count);
        }

        [Fact]
        public async Task Nao_vaza_drafts_de_outro_workspace()
        {
            var store = new FakeDraftStore();
            var workspaceA = Guid.NewGuid();
            var workspaceB = Guid.NewGuid();
            store.All.Add(NewDraft(workspaceA));
            store.All.Add(NewDraft(workspaceB));
            store.All.Add(NewDraft(workspaceB));

            var controller = BuildController(store);
            var (totalCount, items) = ReadListPayload(await controller.ListDrafts(workspaceA, cancellationToken: CancellationToken.None));

            Assert.Equal(1, totalCount);
            Assert.Single(items);
        }

        [Fact]
        public async Task Sem_filtro_retorna_todos_os_engines_do_workspace()
        {
            var store = new FakeDraftStore();
            var workspaceId = Guid.NewGuid();
            store.All.Add(NewDraft(workspaceId, "tcl"));
            store.All.Add(NewDraft(workspaceId, "xslt"));

            var controller = BuildController(store);
            var (totalCount, _) = ReadListPayload(await controller.ListDrafts(workspaceId, cancellationToken: CancellationToken.None));

            Assert.Equal(2, totalCount);
        }

        [Fact]
        public async Task Filtra_por_engine()
        {
            var store = new FakeDraftStore();
            var workspaceId = Guid.NewGuid();
            store.All.Add(NewDraft(workspaceId, "tcl"));
            store.All.Add(NewDraft(workspaceId, "xslt"));
            store.All.Add(NewDraft(workspaceId, "xslt"));

            var controller = BuildController(store);
            var (totalCount, items) = ReadListPayload(await controller.ListDrafts(workspaceId, engine: "xslt", cancellationToken: CancellationToken.None));

            Assert.Equal(2, totalCount);
            Assert.Equal(2, items.Count);
        }

        [Theory]
        [InlineData("sysmiddle")]
        [InlineData("json")]
        public async Task Engine_invalido_retorna_400(string engine)
        {
            var store = new FakeDraftStore();
            var controller = BuildController(store);

            var result = await controller.ListDrafts(Guid.NewGuid(), engine: engine, cancellationToken: CancellationToken.None);

            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Theory]
        [InlineData(0, 20)]
        [InlineData(-1, 20)]
        [InlineData(1, 0)]
        [InlineData(1, 101)]
        public async Task Parametros_invalidos_retorna_400(int page, int pageSize)
        {
            var store = new FakeDraftStore();
            var controller = BuildController(store);

            var result = await controller.ListDrafts(Guid.NewGuid(), page, pageSize, cancellationToken: CancellationToken.None);

            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        public async Task Item_da_lista_traz_fiscalProfile_quando_definido()
        {
            var store = new FakeDraftStore();
            var workspaceId = Guid.NewGuid();
            var profile = new FiscalProfile("NFe", "v1", "outbound", "SP");
            store.All.Add(NewDraft(workspaceId, fiscalProfile: profile));

            var controller = BuildController(store);
            var (_, items) = ReadListPayload(await controller.ListDrafts(workspaceId, cancellationToken: CancellationToken.None));

            var item = items.Single();
            var fiscalProfile = item.GetType().GetProperty("fiscalProfile")!.GetValue(item);
            Assert.NotNull(fiscalProfile);
            var documentType = fiscalProfile!.GetType().GetProperty("documentType")!.GetValue(fiscalProfile);
            Assert.Equal("NFe", documentType);
        }
    }
}
