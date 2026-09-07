using LayoutParserApi.Controllers;
using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Controllers
{
    /// <summary>
    /// Issue #200 (LayoutParserReact) — prova de isolamento por workspace no server-side do
    /// <see cref="MappingDraftsController"/>. O enforcement real já existe em produção (membership
    /// check via <see cref="IIdentityWorkspaceService.GetWorkspaceForMemberAsync"/> +
    /// <c>draft.WorkspaceId != workspaceId</c> da rota); o que faltava era um teste que provasse
    /// isso explicitamente. Diferente de <see cref="TransformationExecutionControllerUserIsolationTests"/>
    /// (isolamento por usuário), aqui o cenário é: usuário autenticado, membro do workspace A,
    /// tenta acessar/mutar um recurso que pertence ao workspace B (do qual NÃO é membro) → espera
    /// 404 (o controller não distingue "não existe" de "não é seu" — mesmo padrão de
    /// <see cref="IIdentityWorkspaceService"/>), nunca 200 nem vazamento de conteúdo.
    ///
    /// <para>Os fakes abaixo reproduzem o contrato real da camada de dados: <see cref="FakeMappingDraftStore"/>
    /// só devolve um draft de <c>GetDraftIfMemberAsync</c>/<c>GetRuleIfMemberAsync</c> quando o
    /// <c>userId</c> pedido é de fato membro do workspace DONO do draft (mesma regra que
    /// <c>SqlMappingDraftStore</c> aplica via JOIN no banco) — não é um "sempre retorna" que
    /// mascararia a checagem do controller.</para>
    /// </summary>
    public class MappingDraftsControllerWorkspaceIsolationTests
    {
        // --- fakes ---

        private sealed class FakeCurrentUser : ICurrentUser
        {
            public string? Name { get; set; }
            public IReadOnlyList<string> Roles { get; set; } = Array.Empty<string>();
            public bool IsAuthenticated => Name != null;
            public bool IsInRole(string role) => Roles.Contains(role, StringComparer.OrdinalIgnoreCase);
            public Guid? UserId { get; set; }
        }

        /// <summary>Membership em memória: workspaceId → conjunto de userIds membros.</summary>
        private sealed class FakeIdentityWorkspaceService : IIdentityWorkspaceService
        {
            private readonly Dictionary<Guid, HashSet<Guid>> _membership = new();
            private readonly Dictionary<Guid, WorkspaceSummary> _workspaces = new();

            public void AddMember(Guid workspaceId, Guid userId)
            {
                if (!_membership.TryGetValue(workspaceId, out var members))
                    _membership[workspaceId] = members = new HashSet<Guid>();
                members.Add(userId);

                _workspaces[workspaceId] = new WorkspaceSummary(workspaceId, $"ws-{workspaceId}", "team", "member", DateTimeOffset.UtcNow);
            }

            public bool IsMember(Guid workspaceId, Guid userId) =>
                _membership.TryGetValue(workspaceId, out var members) && members.Contains(userId);

            public Task<Guid?> ResolveOrCreateUserAsync(string provider, string? tenantOrIssuer, string subject, CancellationToken cancellationToken)
                => throw new NotSupportedException("Não exercitado por estes testes.");

            public Task<WorkspaceMeResult> GetOrCreateMyWorkspacesAsync(Guid userId, CancellationToken cancellationToken)
                => throw new NotSupportedException("Não exercitado por estes testes.");

            public Task<WorkspaceSummary?> GetWorkspaceForMemberAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken)
                => Task.FromResult(IsMember(workspaceId, userId) && _workspaces.TryGetValue(workspaceId, out var ws) ? ws : null);
        }

        /// <summary>
        /// Armazena drafts em memória, marcados com o workspace dono. Só devolve o draft/regra se o
        /// userId pedido for membro do workspace dono — mesma regra do store real (SQL JOIN), a peça
        /// que este teste precisa exercitar de verdade (não um stub "sempre true").
        /// </summary>
        private sealed class FakeMappingDraftStore : IMappingDraftStore
        {
            private readonly FakeIdentityWorkspaceService _identity;
            private readonly Dictionary<Guid, MappingDraftDetail> _drafts = new();

            public FakeMappingDraftStore(FakeIdentityWorkspaceService identity) => _identity = identity;

            public Task<bool> RevisionBelongsToPackageAsync(Guid packageId, Guid revisionId, CancellationToken cancellationToken)
                => Task.FromResult(true);

            public Task<IReadOnlyList<ArtifactFileRef>> GetArtifactFilesForRevisionAsync(Guid revisionId, CancellationToken cancellationToken)
                => Task.FromResult<IReadOnlyList<ArtifactFileRef>>(Array.Empty<ArtifactFileRef>());

            public Task<MappingDraftDetail> CreateDraftAsync(Guid workspaceId, Guid packageId, Guid revisionId, Guid createdByUserId, string engine, CancellationToken cancellationToken)
            {
                var draft = new MappingDraftDetail(
                    DraftId: Guid.NewGuid(),
                    WorkspaceId: workspaceId,
                    PackageId: packageId,
                    RevisionId: revisionId,
                    Engine: engine,
                    CreatedAt: DateTimeOffset.UtcNow,
                    Rules: new List<MappingDraftRuleDetail>
                    {
                        new MappingDraftRuleDetail(
                            RuleId: Guid.NewGuid(),
                            DraftId: Guid.Empty, // preenchido abaixo
                            SourceRefs: new[] { "/src/campo" },
                            TargetRefs: new[] { "/dst/campo" },
                            Operation: "copy",
                            ConditionsJson: "{}",
                            TransformationsJson: "{}",
                            Cardinality: "1:1",
                            Evidence: Array.Empty<MappingDraftRuleEvidence>(),
                            Confidence: "high",
                            Status: "proposed",
                            OpenQuestions: Array.Empty<string>(),
                            CreatedAt: DateTimeOffset.UtcNow,
                            ETag: Convert.ToBase64String(new byte[] { 1, 2, 3 })),
                    });

                // Corrige o DraftId embutido na regra (record imutável — reconstrói).
                draft = draft with
                {
                    Rules = draft.Rules.Select(r => r with { DraftId = draft.DraftId }).ToList(),
                };

                _drafts[draft.DraftId] = draft;
                return Task.FromResult(draft);
            }

            public Task<MappingDraftDetail?> GetDraftIfMemberAsync(Guid draftId, Guid userId, CancellationToken cancellationToken)
            {
                if (!_drafts.TryGetValue(draftId, out var draft))
                    return Task.FromResult<MappingDraftDetail?>(null);

                return Task.FromResult(_identity.IsMember(draft.WorkspaceId, userId) ? draft : null);
            }

            public Task<MappingDraftRuleDetail?> GetRuleIfMemberAsync(Guid draftId, Guid ruleId, Guid userId, CancellationToken cancellationToken)
            {
                if (!_drafts.TryGetValue(draftId, out var draft) || !_identity.IsMember(draft.WorkspaceId, userId))
                    return Task.FromResult<MappingDraftRuleDetail?>(null);

                return Task.FromResult(draft.Rules.FirstOrDefault(r => r.RuleId == ruleId));
            }

            public Task InsertProposedRulesAsync(Guid draftId, Guid jobId, IReadOnlyList<MappingDraftRuleProposal> proposals, CancellationToken cancellationToken)
                => throw new NotSupportedException("Não exercitado por estes testes.");

            public Task<UpdateRuleOutcome> UpdateRuleStatusAsync(
                Guid draftId, Guid ruleId, Guid userId, byte[] expectedRowVersion, string newStatus, string? justification,
                IReadOnlyList<string>? editedSourceRefs, IReadOnlyList<string>? editedTargetRefs, string? editedOperation,
                CancellationToken cancellationToken)
                => throw new NotSupportedException("Não exercitado por estes testes — a checagem de workspace acontece antes deste ponto.");
        }

        private sealed class NoopMappingSuggestionService : IMappingSuggestionService
        {
            public Guid? LastEnqueuedDraftId { get; private set; }

            public Task<Guid> EnqueueAsync(Guid draftId, Guid workspaceId, Guid revisionId, string engine, CancellationToken cancellationToken)
            {
                LastEnqueuedDraftId = draftId;
                return Task.FromResult(Guid.NewGuid());
            }

            public Task<SuggestionJobState?> GetStatusAsync(Guid jobId, CancellationToken cancellationToken)
                => Task.FromResult<SuggestionJobState?>(new SuggestionJobState { JobId = jobId, Status = SuggestionJobStatus.Queued });

            public Task<bool> CancelAsync(Guid jobId, CancellationToken cancellationToken)
                => Task.FromResult(true);
        }

        private sealed class Fixture
        {
            public required MappingDraftsController Controller;
            public required FakeIdentityWorkspaceService Identity;
            public required FakeMappingDraftStore Store;
            public required NoopMappingSuggestionService Suggestions;
            public required FakeCurrentUser User;
        }

        private static Fixture Build()
        {
            var identity = new FakeIdentityWorkspaceService();
            var store = new FakeMappingDraftStore(identity);
            var suggestions = new NoopMappingSuggestionService();
            var user = new FakeCurrentUser();

            var controller = new MappingDraftsController(
                store,
                suggestions,
                identity,
                user,
                NullLogger<MappingDraftsController>.Instance);

            return new Fixture { Controller = controller, Identity = identity, Store = store, Suggestions = suggestions, User = user };
        }

        [Fact]
        public async Task Membro_do_workspace_cria_e_consulta_o_proprio_draft_com_sucesso()
        {
            var fx = Build();
            var workspaceA = Guid.NewGuid();
            var userAlice = Guid.NewGuid();
            fx.Identity.AddMember(workspaceA, userAlice);
            fx.User.UserId = userAlice;

            var createResult = await fx.Controller.CreateDraft(
                workspaceA, Guid.NewGuid(), new CreateDraftRequest { Engine = "xslt", RevisionId = Guid.NewGuid() }, CancellationToken.None);
            var created = Assert.IsType<CreatedAtActionResult>(createResult);
            var draftId = (Guid)created.RouteValues!["draftId"]!;

            var getResult = await fx.Controller.GetDraft(workspaceA, draftId, CancellationToken.None);
            Assert.IsType<OkObjectResult>(getResult);
        }

        [Fact]
        public async Task Membro_de_outro_workspace_recebe_404_ao_tentar_ler_draft_alheio_sem_vazar_conteudo()
        {
            var fx = Build();
            var workspaceA = Guid.NewGuid();
            var workspaceB = Guid.NewGuid();
            var userAlice = Guid.NewGuid();
            var userBob = Guid.NewGuid();
            fx.Identity.AddMember(workspaceA, userAlice);
            fx.Identity.AddMember(workspaceB, userBob);

            fx.User.UserId = userAlice;
            var createResult = await fx.Controller.CreateDraft(
                workspaceA, Guid.NewGuid(), new CreateDraftRequest { Engine = "tcl", RevisionId = Guid.NewGuid() }, CancellationToken.None);
            var created = Assert.IsType<CreatedAtActionResult>(createResult);
            var draftId = (Guid)created.RouteValues!["draftId"]!;

            // Bob não é membro do workspace A — tenta ler o draft de Alice usando o workspaceId real dele.
            fx.User.UserId = userBob;
            var getResult = await fx.Controller.GetDraft(workspaceA, draftId, CancellationToken.None);

            Assert.IsType<NotFoundResult>(getResult);
            Assert.IsNotType<OkObjectResult>(getResult); // nenhum conteúdo do draft de Alice vaza pra Bob
        }

        [Fact]
        public async Task WorkspaceId_da_rota_divergente_do_dono_real_retorna_404_mesmo_com_membership_valida()
        {
            // Cobre a segunda camada de defesa do controller (draft.WorkspaceId != workspaceId da rota):
            // Alice é membro dos workspaces A e C, mas o draft pertence a A — pedir pela rota de C
            // (workspace do qual ela TAMBÉM é membro) não deve enxergar o draft de A.
            var fx = Build();
            var workspaceA = Guid.NewGuid();
            var workspaceC = Guid.NewGuid();
            var userAlice = Guid.NewGuid();
            fx.Identity.AddMember(workspaceA, userAlice);
            fx.Identity.AddMember(workspaceC, userAlice);
            fx.User.UserId = userAlice;

            var createResult = await fx.Controller.CreateDraft(
                workspaceA, Guid.NewGuid(), new CreateDraftRequest { Engine = "xslt", RevisionId = Guid.NewGuid() }, CancellationToken.None);
            var created = Assert.IsType<CreatedAtActionResult>(createResult);
            var draftId = (Guid)created.RouteValues!["draftId"]!;

            var getResult = await fx.Controller.GetDraft(workspaceC, draftId, CancellationToken.None);
            Assert.IsType<NotFoundResult>(getResult);
        }

        [Fact]
        public async Task Membro_de_outro_workspace_nao_consegue_disparar_job_de_sugestao_em_draft_alheio()
        {
            var fx = Build();
            var workspaceA = Guid.NewGuid();
            var workspaceB = Guid.NewGuid();
            var userAlice = Guid.NewGuid();
            var userBob = Guid.NewGuid();
            fx.Identity.AddMember(workspaceA, userAlice);
            fx.Identity.AddMember(workspaceB, userBob);

            fx.User.UserId = userAlice;
            var created = Assert.IsType<CreatedAtActionResult>(await fx.Controller.CreateDraft(
                workspaceA, Guid.NewGuid(), new CreateDraftRequest { Engine = "xslt", RevisionId = Guid.NewGuid() }, CancellationToken.None));
            var draftId = (Guid)created.RouteValues!["draftId"]!;

            fx.User.UserId = userBob;
            var jobResult = await fx.Controller.CreateSuggestionJob(workspaceA, draftId, CancellationToken.None);

            Assert.IsType<NotFoundResult>(jobResult);
            Assert.Null(fx.Suggestions.LastEnqueuedDraftId); // o job nem chega a ser enfileirado
        }

        [Fact]
        public async Task Membro_de_outro_workspace_nao_consegue_ler_regra_isolada_de_draft_alheio()
        {
            var fx = Build();
            var workspaceA = Guid.NewGuid();
            var workspaceB = Guid.NewGuid();
            var userAlice = Guid.NewGuid();
            var userBob = Guid.NewGuid();
            fx.Identity.AddMember(workspaceA, userAlice);
            fx.Identity.AddMember(workspaceB, userBob);

            fx.User.UserId = userAlice;
            var created = Assert.IsType<CreatedAtActionResult>(await fx.Controller.CreateDraft(
                workspaceA, Guid.NewGuid(), new CreateDraftRequest { Engine = "xslt", RevisionId = Guid.NewGuid() }, CancellationToken.None));
            var draftId = (Guid)created.RouteValues!["draftId"]!;
            var okDraft = Assert.IsType<OkObjectResult>(await fx.Controller.GetDraft(workspaceA, draftId, CancellationToken.None));

            // Extrai o ruleId da resposta anônima via reflection (mesmo padrão do teste de execução) —
            // evita reimplementar o shape do DTO aqui.
            var rules = (System.Collections.IEnumerable)okDraft.Value!.GetType().GetProperty("rules")!.GetValue(okDraft.Value)!;
            var firstRule = rules.Cast<object>().First();
            var ruleId = (Guid)firstRule.GetType().GetProperty("ruleId")!.GetValue(firstRule)!;

            fx.User.UserId = userBob;

            // O controller exige o header If-Match ANTES de checar workspace (design §3) — precisa
            // estar presente para o teste de fato exercitar a checagem de isolamento, não a validação
            // de header. UpdateRuleStatusAsync do fake lança se for chamado: se o isolamento falhar e o
            // controller seguir adiante mesmo com Bob não sendo membro, o teste falha por exceção, não
            // silenciosamente.
            var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            httpContext.Request.Headers["If-Match"] = "\"AQID\""; // base64("\x01\x02\x03") — mesmo ETag do fake
            fx.Controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

            var updateResult = await fx.Controller.UpdateRule(
                workspaceA, draftId, ruleId,
                new UpdateRuleRequest { Status = "accepted" },
                CancellationToken.None);

            Assert.IsType<NotFoundResult>(updateResult);
        }
    }
}
