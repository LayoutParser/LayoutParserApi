using LayoutParserApi.Controllers;
using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Models.Entities.Identity;
using LayoutParserApi.Services.Filters;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace LayoutParserApi.Tests.Controllers
{
    /// <summary>
    /// Slice 7 (issue #94) — governança/publicação: máquina de estados estendida, RBAC mínimo por
    /// papel de workspace e rollback idempotente. Dublê de <see cref="IMappingReleaseStore"/> reproduz
    /// a MESMA regra de negócio do <c>SqlMappingReleaseStore</c> real (não é um fake burro que sempre
    /// aceita) — se as regras do teste_failed→in_review ou do rollback forem quebradas no store real,
    /// este dublê fica com o comportamento divergente e os testes acusam a intenção do design.
    /// </summary>
    public class MappingGovernanceControllerTests
    {
        // --- fakes ---

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

                return Task.FromResult<WorkspaceSummary?>(new WorkspaceSummary(workspaceId, "Workspace", "team", role, DateTimeOffset.UtcNow));
            }
        }

        /// <summary>
        /// Reproduz a máquina de estados do <c>SqlMappingReleaseStore</c> real: bloqueia aprovação fora
        /// de <c>test_passed</c>, grava <c>PreviousPublishedReleaseId</c> no publish e faz o rollback
        /// idempotente exatamente como a implementação SQL (§3 do design).
        /// </summary>
        private sealed class FakeReleaseStore : IMappingReleaseStore
        {
            public Dictionary<Guid, MappingReleaseDetail> ById { get; } = new();
            public List<(Guid ReleaseId, string From, string To, Guid Actor, string? Justification)> Transitions { get; } = new();

            public Task<MappingReleaseDetail> CreateOrGetCompiledReleaseAsync(Guid workspaceId, Guid draftId, string engine, string rulesSnapshotHash, IReadOnlyList<Guid> sourceRuleIds, IReadOnlyList<MappingReleaseArtifact> artifacts, IReadOnlyList<MappingReleaseCompileDiagnostic> compileDiagnostics, string correlationId, Guid jobId, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task<MappingReleaseDetail?> GetReleaseIfMemberAsync(Guid releaseId, Guid userId, CancellationToken cancellationToken)
                => Task.FromResult(ById.TryGetValue(releaseId, out var r) ? r : null);

            public Task<(IReadOnlyList<MappingReleaseDetail> Items, int TotalCount)> ListByWorkspaceAsync(Guid workspaceId, int page, int pageSize, CancellationToken cancellationToken)
            {
                var doWorkspace = ById.Values.Where(r => r.WorkspaceId == workspaceId).OrderByDescending(r => r.CreatedAt).ToList();
                var pagina = doWorkspace.Skip((page - 1) * pageSize).Take(pageSize).ToList();
                return Task.FromResult(((IReadOnlyList<MappingReleaseDetail>)pagina, doWorkspace.Count));
            }

            public Task<MappingReleaseDetail?> ApplyTestRunResultAsync(Guid releaseId, MappingTestRunSummary summary, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task<MappingReleaseDetail> ApproveAsync(Guid releaseId, Guid actorUserId, string justification, CancellationToken cancellationToken)
            {
                var current = ById[releaseId];
                if (current.Status != MappingReleaseStatus.TestPassed)
                    throw new InvalidOperationException($"Release {releaseId} está em \"{current.Status}\"; aprovação exige \"{MappingReleaseStatus.TestPassed}\".");

                Transitions.Add((releaseId, MappingReleaseStatus.TestPassed, MappingReleaseStatus.InReview, actorUserId, justification));
                Transitions.Add((releaseId, MappingReleaseStatus.InReview, MappingReleaseStatus.Approved, actorUserId, justification));

                var updated = current with
                {
                    Status = MappingReleaseStatus.Approved,
                    ApprovedByUserId = actorUserId,
                    ApprovedAt = DateTimeOffset.UtcNow,
                    ApprovalJustification = justification
                };
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
                    PreviousPublishedReleaseId = previousPublished?.ReleaseId
                };
                ById[releaseId] = updated;
                return Task.FromResult(updated);
            }

            public Task<MappingReleaseDetail> RollbackAsync(Guid releaseId, Guid actorUserId, CancellationToken cancellationToken)
            {
                var current = ById[releaseId];
                // Idempotente: não está mais published (rollback anterior já rodou) — no-op.
                if (current.Status != MappingReleaseStatus.Published)
                    return Task.FromResult(current);

                if (current.PreviousPublishedReleaseId is not Guid previousId)
                    throw new InvalidOperationException("Sem release publicada anterior para reverter.");

                Transitions.Add((releaseId, MappingReleaseStatus.Published, MappingReleaseStatus.Deprecated, actorUserId, "Rollback."));
                ById[releaseId] = current with { Status = MappingReleaseStatus.Deprecated };

                var previous = ById[previousId];
                Transitions.Add((previousId, MappingReleaseStatus.Deprecated, MappingReleaseStatus.Published, actorUserId, "Rollback: promovida de volta."));
                ById[previousId] = previous with { Status = MappingReleaseStatus.Published, PublishedByUserId = actorUserId, PublishedAt = DateTimeOffset.UtcNow };

                return Task.FromResult(ById[releaseId]);
            }
        }

        private static MappingReleaseDetail NewRelease(Guid workspaceId, Guid draftId, string status) => new(
            Guid.NewGuid(), workspaceId, draftId, "xslt", Array.Empty<MappingReleaseArtifact>(), Array.Empty<Guid>(),
            Array.Empty<MappingReleaseCompileDiagnostic>(), "hash", null, status, "corr-0", DateTimeOffset.UtcNow, "AAAA",
            "development", null, null, null, null, null, null);

        private static MappingGovernanceController BuildController(FakeReleaseStore store, FakeCurrentUser user)
            => new(store, user, NullLogger<MappingGovernanceController>.Instance);

        // --- Transição bloqueada: test_failed não pode ir pra in_review/approved ---

        [Fact]
        public async Task Approve_release_test_failed_e_recusado()
        {
            var store = new FakeReleaseStore();
            var workspaceId = Guid.NewGuid();
            var release = NewRelease(workspaceId, Guid.NewGuid(), MappingReleaseStatus.TestFailed);
            store.ById[release.ReleaseId] = release;

            var controller = BuildController(store, new FakeCurrentUser { UserId = Guid.NewGuid() });
            var result = await controller.Approve(workspaceId, release.ReleaseId, new ApproveReleaseRequest { Justification = "ok" }, CancellationToken.None);

            Assert.IsType<UnprocessableEntityObjectResult>(result);
            Assert.Equal(MappingReleaseStatus.TestFailed, store.ById[release.ReleaseId].Status);
        }

        [Fact]
        public async Task Approve_release_test_passed_promove_para_approved()
        {
            var store = new FakeReleaseStore();
            var workspaceId = Guid.NewGuid();
            var release = NewRelease(workspaceId, Guid.NewGuid(), MappingReleaseStatus.TestPassed);
            store.ById[release.ReleaseId] = release;
            var actor = Guid.NewGuid();

            var controller = BuildController(store, new FakeCurrentUser { UserId = actor });
            var result = await controller.Approve(workspaceId, release.ReleaseId, new ApproveReleaseRequest { Justification = "revisado" }, CancellationToken.None);

            Assert.IsType<OkObjectResult>(result);
            Assert.Equal(MappingReleaseStatus.Approved, store.ById[release.ReleaseId].Status);

            // MappingTransition registra ator/instante/justificativa das DUAS transições (in_review + approved).
            Assert.Contains(store.Transitions, t => t.ReleaseId == release.ReleaseId && t.From == MappingReleaseStatus.TestPassed && t.To == MappingReleaseStatus.InReview && t.Actor == actor && t.Justification == "revisado");
            Assert.Contains(store.Transitions, t => t.ReleaseId == release.ReleaseId && t.From == MappingReleaseStatus.InReview && t.To == MappingReleaseStatus.Approved && t.Actor == actor);
        }

        // --- Publicação: approved → published, imutabilidade (nova revisão nunca herda gate) ---

        [Fact]
        public async Task Publish_sem_aprovacao_previa_e_recusado()
        {
            var store = new FakeReleaseStore();
            var workspaceId = Guid.NewGuid();
            var release = NewRelease(workspaceId, Guid.NewGuid(), MappingReleaseStatus.TestPassed);
            store.ById[release.ReleaseId] = release;

            var controller = BuildController(store, new FakeCurrentUser { UserId = Guid.NewGuid() });
            var result = await controller.Publish(workspaceId, release.ReleaseId, null, CancellationToken.None);

            Assert.IsType<UnprocessableEntityObjectResult>(result);
        }

        [Fact]
        public async Task Publish_release_nova_nao_herda_gate_da_anterior_ao_publicar()
        {
            // "Nova revisão exige regressão": a segunda release (mesmo DraftId) precisa do próprio
            // approved — publicá-la não reaproveita o status da primeira, é uma linha independente.
            var store = new FakeReleaseStore();
            var workspaceId = Guid.NewGuid();
            var draftId = Guid.NewGuid();
            var actor = Guid.NewGuid();

            var releaseA = NewRelease(workspaceId, draftId, MappingReleaseStatus.Approved);
            store.ById[releaseA.ReleaseId] = releaseA;
            var publishA = await controllerFor(store, actor).Publish(workspaceId, releaseA.ReleaseId, null, CancellationToken.None);
            Assert.IsType<OkObjectResult>(publishA);

            var releaseB = NewRelease(workspaceId, draftId, MappingReleaseStatus.TestPassed);
            store.ById[releaseB.ReleaseId] = releaseB;

            // releaseB ainda não foi aprovada — publish tem que recusar mesmo sendo do mesmo draft.
            var publishBSemAprovar = await controllerFor(store, actor).Publish(workspaceId, releaseB.ReleaseId, null, CancellationToken.None);
            Assert.IsType<UnprocessableEntityObjectResult>(publishBSemAprovar);

            await controllerFor(store, actor).Approve(workspaceId, releaseB.ReleaseId, new ApproveReleaseRequest { Justification = "ok" }, CancellationToken.None);
            var publishB = await controllerFor(store, actor).Publish(workspaceId, releaseB.ReleaseId, null, CancellationToken.None);

            Assert.IsType<OkObjectResult>(publishB);
            Assert.Equal(MappingReleaseStatus.Deprecated, store.ById[releaseA.ReleaseId].Status);
            Assert.Equal(MappingReleaseStatus.Published, store.ById[releaseB.ReleaseId].Status);
            Assert.Equal(releaseA.ReleaseId, store.ById[releaseB.ReleaseId].PreviousPublishedReleaseId);
        }

        private static MappingGovernanceController controllerFor(FakeReleaseStore store, Guid actor)
            => BuildController(store, new FakeCurrentUser { UserId = actor });

        // --- Rollback idempotente ---

        [Fact]
        public async Task Rollback_duas_vezes_seguidas_nao_duplica_nem_quebra()
        {
            var store = new FakeReleaseStore();
            var workspaceId = Guid.NewGuid();
            var draftId = Guid.NewGuid();
            var actor = Guid.NewGuid();

            var releaseA = NewRelease(workspaceId, draftId, MappingReleaseStatus.Approved);
            store.ById[releaseA.ReleaseId] = releaseA;
            await controllerFor(store, actor).Publish(workspaceId, releaseA.ReleaseId, null, CancellationToken.None);

            var releaseB = NewRelease(workspaceId, draftId, MappingReleaseStatus.Approved);
            store.ById[releaseB.ReleaseId] = releaseB;
            await controllerFor(store, actor).Publish(workspaceId, releaseB.ReleaseId, null, CancellationToken.None);

            var primeiroRollback = await controllerFor(store, actor).Rollback(workspaceId, releaseB.ReleaseId, CancellationToken.None);
            Assert.IsType<OkObjectResult>(primeiroRollback);
            Assert.Equal(MappingReleaseStatus.Deprecated, store.ById[releaseB.ReleaseId].Status);
            Assert.Equal(MappingReleaseStatus.Published, store.ById[releaseA.ReleaseId].Status);
            var transitionCountAposPrimeiro = store.Transitions.Count;

            var segundoRollback = await controllerFor(store, actor).Rollback(workspaceId, releaseB.ReleaseId, CancellationToken.None);

            Assert.IsType<OkObjectResult>(segundoRollback);
            Assert.Equal(MappingReleaseStatus.Deprecated, store.ById[releaseB.ReleaseId].Status);
            Assert.Equal(MappingReleaseStatus.Published, store.ById[releaseA.ReleaseId].Status);
            Assert.Equal(transitionCountAposPrimeiro, store.Transitions.Count); // no-op: nenhuma transição nova gravada.
        }

        // --- Isolamento cross-workspace (mesmo padrão dos slices anteriores) ---

        [Fact]
        public async Task Approve_release_de_outro_workspace_retorna_404()
        {
            var store = new FakeReleaseStore();
            var workspaceDoAtacante = Guid.NewGuid();
            var release = NewRelease(Guid.NewGuid(), Guid.NewGuid(), MappingReleaseStatus.TestPassed);
            store.ById[release.ReleaseId] = release;

            var controller = BuildController(store, new FakeCurrentUser { UserId = Guid.NewGuid() });
            var result = await controller.Approve(workspaceDoAtacante, release.ReleaseId, new ApproveReleaseRequest { Justification = "x" }, CancellationToken.None);

            Assert.IsType<NotFoundResult>(result);
        }

        // --- RBAC: 403 quando papel insuficiente, via o filtro real (não bypassado) ---

        private static async Task<IActionResult> RunFilterAsync(RequireWorkspaceRoleFilter filter, Guid workspaceId, Guid userId, ICurrentUser currentUser)
        {
            var httpContext = new DefaultHttpContext();
            var routeData = new RouteData();
            routeData.Values["workspaceId"] = workspaceId.ToString();
            var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());
            var context = new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller: new object());

            IActionResult? nextResult = null;
            await filter.OnActionExecutionAsync(context, () =>
            {
                nextResult = new OkResult();
                return Task.FromResult(new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), controller: new object()));
            });

            return context.Result ?? nextResult!;
        }

        [Theory]
        [InlineData(WorkspaceRole.Viewer)]
        [InlineData(WorkspaceRole.Mapper)]
        [InlineData(WorkspaceRole.Operator)]
        public async Task RequireWorkspaceRole_papel_insuficiente_retorna_403(string roleInsuficiente)
        {
            var workspaceId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var workspaceStore = new FakeIdentityWorkspaceStore();
            workspaceStore.Memberships[(workspaceId, userId)] = roleInsuficiente;
            var currentUser = new FakeCurrentUser { UserId = userId };
            var filter = new RequireWorkspaceRoleFilter(new[] { WorkspaceRole.FiscalAdmin, WorkspaceRole.Owner }, currentUser, workspaceStore, NullLogger<RequireWorkspaceRoleFilter>.Instance);

            var result = await RunFilterAsync(filter, workspaceId, userId, currentUser);

            var objectResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
        }

        [Fact]
        public async Task RequireWorkspaceRole_papel_suficiente_deixa_passar()
        {
            var workspaceId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var workspaceStore = new FakeIdentityWorkspaceStore();
            workspaceStore.Memberships[(workspaceId, userId)] = WorkspaceRole.FiscalAdmin;
            var currentUser = new FakeCurrentUser { UserId = userId };
            var filter = new RequireWorkspaceRoleFilter(new[] { WorkspaceRole.FiscalAdmin, WorkspaceRole.Owner }, currentUser, workspaceStore, NullLogger<RequireWorkspaceRoleFilter>.Instance);

            var result = await RunFilterAsync(filter, workspaceId, userId, currentUser);

            Assert.IsType<OkResult>(result);
        }

        [Fact]
        public async Task RequireWorkspaceRole_sem_membership_retorna_404_nao_403()
        {
            var workspaceId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var workspaceStore = new FakeIdentityWorkspaceStore(); // sem membership nenhuma
            var currentUser = new FakeCurrentUser { UserId = userId };
            var filter = new RequireWorkspaceRoleFilter(new[] { WorkspaceRole.FiscalAdmin, WorkspaceRole.Owner }, currentUser, workspaceStore, NullLogger<RequireWorkspaceRoleFilter>.Instance);

            var result = await RunFilterAsync(filter, workspaceId, userId, currentUser);

            Assert.IsType<NotFoundResult>(result);
        }

        // --- Listagem (issue #198 do front) ---

        [Fact]
        public async Task List_workspace_sem_releases_retorna_vazio()
        {
            var store = new FakeReleaseStore();
            var workspaceId = Guid.NewGuid();
            var controller = BuildController(store, new FakeCurrentUser { UserId = Guid.NewGuid() });

            var result = await controller.List(workspaceId, 1, 20, CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = Assert.IsAssignableFrom<object>(ok.Value);
            var totalCount = (int)payload.GetType().GetProperty("totalCount")!.GetValue(payload)!;
            var items = (System.Collections.IEnumerable)payload.GetType().GetProperty("items")!.GetValue(payload)!;
            Assert.Equal(0, totalCount);
            Assert.Empty(items.Cast<object>());
        }

        [Fact]
        public async Task List_pagina_multiplas_releases_do_workspace()
        {
            var store = new FakeReleaseStore();
            var workspaceId = Guid.NewGuid();
            for (var i = 0; i < 5; i++)
            {
                var release = NewRelease(workspaceId, Guid.NewGuid(), MappingReleaseStatus.DraftCompiled);
                store.ById[release.ReleaseId] = release;
            }

            var controller = BuildController(store, new FakeCurrentUser { UserId = Guid.NewGuid() });
            var result = await controller.List(workspaceId, 1, 2, CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = ok.Value!;
            var totalCount = (int)payload.GetType().GetProperty("totalCount")!.GetValue(payload)!;
            var items = ((System.Collections.IEnumerable)payload.GetType().GetProperty("items")!.GetValue(payload)!).Cast<object>().ToList();
            Assert.Equal(5, totalCount); // total real, mesmo a página trazendo só 2.
            Assert.Equal(2, items.Count);
        }

        [Fact]
        public async Task List_isola_releases_de_outro_workspace()
        {
            var store = new FakeReleaseStore();
            var workspaceId = Guid.NewGuid();
            var outroWorkspaceId = Guid.NewGuid();
            var releaseDoWorkspace = NewRelease(workspaceId, Guid.NewGuid(), MappingReleaseStatus.DraftCompiled);
            var releaseDeOutro = NewRelease(outroWorkspaceId, Guid.NewGuid(), MappingReleaseStatus.DraftCompiled);
            store.ById[releaseDoWorkspace.ReleaseId] = releaseDoWorkspace;
            store.ById[releaseDeOutro.ReleaseId] = releaseDeOutro;

            var controller = BuildController(store, new FakeCurrentUser { UserId = Guid.NewGuid() });
            var result = await controller.List(workspaceId, 1, 20, CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = ok.Value!;
            var totalCount = (int)payload.GetType().GetProperty("totalCount")!.GetValue(payload)!;
            Assert.Equal(1, totalCount); // a release do outro workspace não aparece.
        }

        [Theory]
        [InlineData(0, 20)]
        [InlineData(-1, 20)]
        [InlineData(1, 0)]
        [InlineData(1, 101)]
        public async Task List_parametros_invalidos_retorna_400(int page, int pageSize)
        {
            var store = new FakeReleaseStore();
            var controller = BuildController(store, new FakeCurrentUser { UserId = Guid.NewGuid() });

            var result = await controller.List(Guid.NewGuid(), page, pageSize, CancellationToken.None);

            Assert.IsType<BadRequestObjectResult>(result);
        }

        // --- RBAC do endpoint de leitura: Viewer (papel mais baixo) tem acesso, diferente das mutações ---

        [Fact]
        public async Task RequireWorkspaceRole_leitura_aceita_viewer()
        {
            var workspaceId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var workspaceStore = new FakeIdentityWorkspaceStore();
            workspaceStore.Memberships[(workspaceId, userId)] = WorkspaceRole.Viewer;
            var currentUser = new FakeCurrentUser { UserId = userId };
            var filter = new RequireWorkspaceRoleFilter(
                new[] { WorkspaceRole.Owner, WorkspaceRole.FiscalAdmin, WorkspaceRole.Mapper, WorkspaceRole.Reviewer, WorkspaceRole.Operator, WorkspaceRole.Viewer },
                currentUser, workspaceStore, NullLogger<RequireWorkspaceRoleFilter>.Instance);

            var result = await RunFilterAsync(filter, workspaceId, userId, currentUser);

            Assert.IsType<OkResult>(result);
        }
    }
}
