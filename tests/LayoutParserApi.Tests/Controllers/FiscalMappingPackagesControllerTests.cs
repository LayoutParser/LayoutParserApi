using LayoutParserApi.Controllers;
using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Controllers
{
    /// <summary>
    /// Slice 2 (issue #229) — isolamento cross-workspace no GET, mesmo racional do
    /// <c>WorkspacesControllerTests</c> do Slice 1: usuário de outro workspace nunca lê o pacote alheio.
    /// </summary>
    public class FiscalMappingPackagesControllerTests
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

        /// <summary>Dublê do serviço de orquestração — 🔴 o filtro que importa fica em <see cref="GetPackageIfMemberAsync"/>.</summary>
        private sealed class FakePackageService : IFiscalPackageService
        {
            public Dictionary<Guid, (PackageDetail Detail, Guid OwnerUserId)> Packages { get; } = new();
            public Dictionary<Guid, List<ProjectSummary>> ProjectsByWorkspace { get; } = new();
            public CreateRevisionOutcome? NextCreateRevisionOutcome { get; set; }
            public ExcelInventoryOutcome? NextExcelInventoryOutcome { get; set; }

            public Task<CreatePackageOutcome> CreatePackageAsync(Guid workspaceId, Guid projectId, Guid userId, string packageName, string? idempotencyKey, IReadOnlyList<UploadedArtifactInput> artifacts, CancellationToken cancellationToken)
                => throw new NotSupportedException("Não exercitado neste conjunto de testes.");

            public Task<PackageDetail?> GetPackageIfMemberAsync(Guid packageId, Guid userId, CancellationToken cancellationToken)
            {
                if (!Packages.TryGetValue(packageId, out var entry) || entry.OwnerUserId != userId)
                    return Task.FromResult<PackageDetail?>(null);

                return Task.FromResult<PackageDetail?>(entry.Detail);
            }

            public Task<IReadOnlyList<ProjectSummary>> ListProjectsAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken)
                => Task.FromResult<IReadOnlyList<ProjectSummary>>(
                    ProjectsByWorkspace.TryGetValue(workspaceId, out var projects) ? projects : new List<ProjectSummary>());

            public Task<CreateRevisionOutcome> CreateRevisionAsync(Guid workspaceId, Guid packageId, Guid userId, IReadOnlyList<UploadedArtifactInput> artifacts, CancellationToken cancellationToken)
                => Task.FromResult(NextCreateRevisionOutcome ?? throw new NotSupportedException("Configure NextCreateRevisionOutcome antes de chamar."));

            public Task<ExcelInventoryOutcome> GetExcelInventoryAsync(Guid workspaceId, Guid packageId, Guid artifactId, Guid userId, CancellationToken cancellationToken)
                => Task.FromResult(NextExcelInventoryOutcome ?? throw new NotSupportedException("Configure NextExcelInventoryOutcome antes de chamar."));
        }

        private static FiscalMappingPackagesController BuildController(FakePackageService packageService, FakeIdentityWorkspaceService identityService, FakeCurrentUser user)
            => new(packageService, identityService, user, NullLogger<FiscalMappingPackagesController>.Instance);

        [Fact]
        public async Task GetPackage_membro_recebe_200()
        {
            var packageService = new FakePackageService();
            var identityService = new FakeIdentityWorkspaceService();
            var userA = Guid.NewGuid();
            var workspaceId = Guid.NewGuid();
            var packageId = Guid.NewGuid();
            var detail = new PackageDetail(packageId, workspaceId, Guid.NewGuid(), "Pacote A", DateTimeOffset.UtcNow,
                new RevisionSummary(Guid.NewGuid(), 1, DateTimeOffset.UtcNow, Array.Empty<ArtifactSummary>()));
            packageService.Packages[packageId] = (detail, userA);

            var controller = BuildController(packageService, identityService, new FakeCurrentUser { UserId = userA });

            var result = await controller.GetPackage(workspaceId, packageId, CancellationToken.None);

            Assert.IsType<OkObjectResult>(result);
        }

        /// <summary>
        /// 🔴 O TESTE QUE MORDE: usuário B nunca é dono do pacote de A. Se o filtro de ownership/
        /// membership for removido do serviço, esta asserção vira <c>OkObjectResult</c> e o teste
        /// fica vermelho — mesmo racional do <c>WorkspacesControllerTests</c> (Slice 1).
        /// </summary>
        [Fact]
        public async Task GetPackage_usuario_de_outro_workspace_nao_le_pacote_alheio()
        {
            var packageService = new FakePackageService();
            var identityService = new FakeIdentityWorkspaceService();
            var userA = Guid.NewGuid();
            var userB = Guid.NewGuid();
            var workspaceDeA = Guid.NewGuid();
            var packageId = Guid.NewGuid();
            var detail = new PackageDetail(packageId, workspaceDeA, Guid.NewGuid(), "Pacote da Alice", DateTimeOffset.UtcNow,
                new RevisionSummary(Guid.NewGuid(), 1, DateTimeOffset.UtcNow, Array.Empty<ArtifactSummary>()));
            packageService.Packages[packageId] = (detail, userA);
            // userB deliberadamente sem posse do pacote.

            var controllerComoB = BuildController(packageService, identityService, new FakeCurrentUser { UserId = userB });

            var result = await controllerComoB.GetPackage(workspaceDeA, packageId, CancellationToken.None);

            Assert.IsType<NotFoundResult>(result);
        }

        [Fact]
        public async Task GetPackage_pacote_inexistente_e_alheio_respondem_o_mesmo_404()
        {
            var packageService = new FakePackageService();
            var identityService = new FakeIdentityWorkspaceService();
            var userId = Guid.NewGuid();
            var outroDono = Guid.NewGuid();
            var workspaceId = Guid.NewGuid();
            var packageAlheio = Guid.NewGuid();
            var packageInexistente = Guid.NewGuid();
            var detail = new PackageDetail(packageAlheio, workspaceId, Guid.NewGuid(), "Não é meu", DateTimeOffset.UtcNow,
                new RevisionSummary(Guid.NewGuid(), 1, DateTimeOffset.UtcNow, Array.Empty<ArtifactSummary>()));
            packageService.Packages[packageAlheio] = (detail, outroDono);

            var controller = BuildController(packageService, identityService, new FakeCurrentUser { UserId = userId });

            var resultadoAlheio = await controller.GetPackage(workspaceId, packageAlheio, CancellationToken.None);
            var resultadoInexistente = await controller.GetPackage(workspaceId, packageInexistente, CancellationToken.None);

            Assert.IsType<NotFoundResult>(resultadoAlheio);
            Assert.IsType<NotFoundResult>(resultadoInexistente);
        }

        [Fact]
        public async Task GetPackage_sem_identidade_resolvida_retorna_404_uniforme()
        {
            var controller = BuildController(new FakePackageService(), new FakeIdentityWorkspaceService(), new FakeCurrentUser { UserId = null });

            var result = await controller.GetPackage(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

            Assert.IsType<NotFoundResult>(result);
        }

        [Fact]
        public async Task GetPackage_workspaceId_da_rota_divergente_do_dono_real_tambem_404()
        {
            // Mesmo com "posse" no serviço, se o workspaceId da rota não bate com o dono real do
            // pacote, o controller não deve confiar cegamente no parâmetro de rota.
            var packageService = new FakePackageService();
            var identityService = new FakeIdentityWorkspaceService();
            var userA = Guid.NewGuid();
            var workspaceReal = Guid.NewGuid();
            var workspaceForjado = Guid.NewGuid();
            var packageId = Guid.NewGuid();
            var detail = new PackageDetail(packageId, workspaceReal, Guid.NewGuid(), "Pacote A", DateTimeOffset.UtcNow,
                new RevisionSummary(Guid.NewGuid(), 1, DateTimeOffset.UtcNow, Array.Empty<ArtifactSummary>()));
            packageService.Packages[packageId] = (detail, userA);

            var controller = BuildController(packageService, identityService, new FakeCurrentUser { UserId = userA });

            var result = await controller.GetPackage(workspaceForjado, packageId, CancellationToken.None);

            Assert.IsType<NotFoundResult>(result);
        }

        // ---- Gap 1 (issue #201): listagem de projetos fiscais ----

        [Fact]
        public async Task ListProjects_membro_recebe_200_com_a_lista_do_workspace()
        {
            var packageService = new FakePackageService();
            var identityService = new FakeIdentityWorkspaceService();
            var userId = Guid.NewGuid();
            var workspaceId = Guid.NewGuid();
            identityService.Memberships.Add((workspaceId, userId));
            packageService.ProjectsByWorkspace[workspaceId] = new List<ProjectSummary>
            {
                new(Guid.NewGuid(), workspaceId, "Projeto A", DateTimeOffset.UtcNow)
            };

            var controller = BuildController(packageService, identityService, new FakeCurrentUser { UserId = userId });

            var result = await controller.ListProjects(workspaceId, CancellationToken.None);

            Assert.IsType<OkObjectResult>(result);
        }

        [Fact]
        public async Task ListProjects_nao_membro_recebe_404()
        {
            var packageService = new FakePackageService();
            var identityService = new FakeIdentityWorkspaceService(); // sem membership cadastrada.
            var controller = BuildController(packageService, identityService, new FakeCurrentUser { UserId = Guid.NewGuid() });

            var result = await controller.ListProjects(Guid.NewGuid(), CancellationToken.None);

            Assert.IsType<NotFoundResult>(result);
        }

        [Fact]
        public async Task ListProjects_sem_identidade_resolvida_retorna_404_uniforme()
        {
            var controller = BuildController(new FakePackageService(), new FakeIdentityWorkspaceService(), new FakeCurrentUser { UserId = null });

            var result = await controller.ListProjects(Guid.NewGuid(), CancellationToken.None);

            Assert.IsType<NotFoundResult>(result);
        }

        // ---- Gap 2 (issue #201): nova revisão de pacote existente ----

        [Fact]
        public async Task CreateRevision_pacote_inexistente_ou_alheio_recebe_404()
        {
            var packageService = new FakePackageService
            {
                NextCreateRevisionOutcome = new CreateRevisionOutcome(false, null, true, null)
            };
            var controller = BuildController(packageService, new FakeIdentityWorkspaceService(), new FakeCurrentUser { UserId = Guid.NewGuid() });
            AttachMultipartForm(controller, ("sample", "sample.txt", "text/plain", new byte[] { 1, 2, 3 }));

            var result = await controller.CreateRevision(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

            Assert.IsType<NotFoundResult>(result);
        }

        [Fact]
        public async Task CreateRevision_artefato_invalido_recebe_422()
        {
            var packageService = new FakePackageService
            {
                NextCreateRevisionOutcome = new CreateRevisionOutcome(false, "Artefato \"sample\": extensão errada.", false, null)
            };
            var controller = BuildController(packageService, new FakeIdentityWorkspaceService(), new FakeCurrentUser { UserId = Guid.NewGuid() });
            AttachMultipartForm(controller, ("sample", "sample.txt", "text/plain", new byte[] { 1, 2, 3 }));

            var result = await controller.CreateRevision(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

            Assert.IsType<UnprocessableEntityObjectResult>(result);
        }

        [Fact]
        public async Task CreateRevision_sucesso_recebe_201_com_revisionNumber_incrementado()
        {
            var packageId = Guid.NewGuid();
            var workspaceId = Guid.NewGuid();
            var detail = new PackageDetail(packageId, workspaceId, Guid.NewGuid(), "Pacote", DateTimeOffset.UtcNow,
                new RevisionSummary(Guid.NewGuid(), 2, DateTimeOffset.UtcNow, Array.Empty<ArtifactSummary>()));
            var packageService = new FakePackageService
            {
                NextCreateRevisionOutcome = new CreateRevisionOutcome(true, null, false, detail)
            };
            var controller = BuildController(packageService, new FakeIdentityWorkspaceService(), new FakeCurrentUser { UserId = Guid.NewGuid() });
            AttachMultipartForm(controller, ("sample", "sample.txt", "text/plain", new byte[] { 1, 2, 3 }));

            var result = await controller.CreateRevision(workspaceId, packageId, CancellationToken.None);

            var created = Assert.IsType<CreatedAtActionResult>(result);
            Assert.Equal(nameof(FiscalMappingPackagesController.GetPackage), created.ActionName);
        }

        [Fact]
        public async Task CreateRevision_sem_identidade_resolvida_retorna_404_uniforme()
        {
            var controller = BuildController(new FakePackageService(), new FakeIdentityWorkspaceService(), new FakeCurrentUser { UserId = null });
            AttachMultipartForm(controller, ("sample", "sample.txt", "text/plain", new byte[] { 1, 2, 3 }));

            var result = await controller.CreateRevision(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

            Assert.IsType<NotFoundResult>(result);
        }

        // ---- Gap 3 (issue #201): inventário de estrutura do Excel ----

        [Fact]
        public async Task GetExcelInventory_pacote_ou_artefato_inexistente_recebe_404()
        {
            var packageService = new FakePackageService
            {
                NextExcelInventoryOutcome = new ExcelInventoryOutcome(false, null, true, null)
            };
            var controller = BuildController(packageService, new FakeIdentityWorkspaceService(), new FakeCurrentUser { UserId = Guid.NewGuid() });

            var result = await controller.GetExcelInventory(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

            Assert.IsType<NotFoundResult>(result);
        }

        [Fact]
        public async Task GetExcelInventory_artefato_que_nao_e_spec_recebe_422()
        {
            var packageService = new FakePackageService
            {
                NextExcelInventoryOutcome = new ExcelInventoryOutcome(false, "Inventário de estrutura só está disponível para artefatos do tipo \"spec\".", false, null)
            };
            var controller = BuildController(packageService, new FakeIdentityWorkspaceService(), new FakeCurrentUser { UserId = Guid.NewGuid() });

            var result = await controller.GetExcelInventory(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

            Assert.IsType<UnprocessableEntityObjectResult>(result);
        }

        [Fact]
        public async Task GetExcelInventory_sucesso_recebe_200_com_abas_e_colunas()
        {
            var inventory = new ExcelInventoryResult(
                new List<ExcelSheetInventory> { new("Regra-CST", new[] { "orig", "CST" }, 3) },
                new List<string> { "Layout-Emissao" });
            var packageService = new FakePackageService
            {
                NextExcelInventoryOutcome = new ExcelInventoryOutcome(true, null, false, inventory)
            };
            var controller = BuildController(packageService, new FakeIdentityWorkspaceService(), new FakeCurrentUser { UserId = Guid.NewGuid() });

            var result = await controller.GetExcelInventory(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

            Assert.IsType<OkObjectResult>(result);
        }

        [Fact]
        public async Task GetExcelInventory_sem_identidade_resolvida_retorna_404_uniforme()
        {
            var controller = BuildController(new FakePackageService(), new FakeIdentityWorkspaceService(), new FakeCurrentUser { UserId = null });

            var result = await controller.GetExcelInventory(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

            Assert.IsType<NotFoundResult>(result);
        }

        /// <summary>Monta um <c>Request.Form.Files</c> multipart mínimo para exercitar <c>CreateRevision</c> sem servidor HTTP real.</summary>
        private static void AttachMultipartForm(FiscalMappingPackagesController controller, params (string FieldName, string FileName, string ContentType, byte[] Content)[] files)
        {
            var formFiles = new FormFileCollection();
            foreach (var (fieldName, fileName, contentType, content) in files)
            {
                var stream = new MemoryStream(content);
                var formFile = new FormFile(stream, 0, content.Length, fieldName, fileName)
                {
                    Headers = new HeaderDictionary(),
                    ContentType = contentType,
                };
                formFiles.Add(formFile);
            }

            var httpContext = new DefaultHttpContext
            {
                Request = { Form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(), formFiles) }
            };

            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        }
    }
}
