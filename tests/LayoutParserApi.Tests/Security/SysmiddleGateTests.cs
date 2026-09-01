using System.Reflection;
using System.Text.Json;

using LayoutParserApi.Controllers;
using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Services.Fiscal;
using LayoutParserApi.Services.Filters;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;

using Xunit;

namespace LayoutParserApi.Tests.Security
{
    /// <summary>
    /// Slice 6 (issue #232) — suíte consolidada que prova, num lugar só, a garantia central do
    /// produto: "nenhum endpoint, payload adulterado, role ou estado permite mutação Sysmiddle".
    /// Complementa (não substitui) <see cref="LayoutParserApi.Tests.Filters.MappingEngineGuardFilterTests"/>
    /// e <see cref="LayoutParserApi.Tests.Fiscal.MappingExplanationAdaptersTests"/> — aqui o foco é
    /// reunir TODOS os vetores mapeados em <c>docs/architecture/design-slice6-gate-sysmiddle-2026-09-01.md</c>
    /// em um único arquivo que documenta e trava a garantia, não repetir cobertura já existente.
    /// </summary>
    public class SysmiddleGateTests
    {
        // ── 1) Endpoints fiscais recusam engine=sysmiddle — string, array, objeto, ausência ──

        [Theory]
        [InlineData(typeof(MappingDraftsController))]
        [InlineData(typeof(MappingCompilationController))]
        public void Controllers_fiscais_aplicam_MappingEngineGuardFilter_no_nivel_da_classe(Type controllerType)
        {
            var atributo = controllerType
                .GetCustomAttributes(typeof(ServiceFilterAttribute), inherit: false)
                .Cast<ServiceFilterAttribute>()
                .SingleOrDefault(a => a.ServiceType == typeof(MappingEngineGuardFilter));

            Assert.NotNull(atributo);
        }

        [Fact]
        public void MappingExplanationController_NAO_aplica_o_filtro_deliberadamente()
        {
            // Único uso legítimo de "sysmiddle" no sistema (spec §4: Sysmiddle explica, nunca autoria).
            // Aplicar o filtro aqui bloquearia o próprio caso que ele deveria permitir.
            var atributo = typeof(MappingExplanationController)
                .GetCustomAttributes(typeof(ServiceFilterAttribute), inherit: false)
                .Cast<ServiceFilterAttribute>()
                .SingleOrDefault(a => a.ServiceType == typeof(MappingEngineGuardFilter));

            Assert.Null(atributo);
        }

        [Theory]
        // string simples, variações de casing
        [InlineData("sysmiddle")]
        [InlineData("SYSMIDDLE")]
        [InlineData("SysMiddle")]
        public async Task Engine_sysmiddle_em_qualquer_variacao_de_casing_e_recusado_no_query(string valor)
        {
            var filtro = new MappingEngineGuardFilter();
            var context = CriarExecutingContext(new Dictionary<string, StringValues> { ["engine"] = valor });
            var nextChamado = false;

            await filtro.OnActionExecutionAsync(context, () =>
            {
                nextChamado = true;
                return Task.FromResult(CriarExecutedContext());
            });

            Assert.False(nextChamado);
            Assert.IsType<UnprocessableEntityObjectResult>(context.Result);
        }

        [Fact]
        public async Task Engine_sysmiddle_com_espacos_ao_redor_e_recusado()
        {
            // Corrigido (Slice 6 / issue #232): IsSysmiddle() agora aplica .Trim() antes da
            // comparação, então "engine=%20sysmiddle%20" (query) é recusado igual a "sysmiddle"
            // sem espaços. Este teste antes caracterizava o bypass (bug conhecido); agora garante
            // o comportamento correto e evita regressão.
            var filtro = new MappingEngineGuardFilter();
            var context = CriarExecutingContext(new Dictionary<string, StringValues> { ["engine"] = " sysmiddle " });
            var nextChamado = false;

            await filtro.OnActionExecutionAsync(context, () =>
            {
                nextChamado = true;
                return Task.FromResult(CriarExecutedContext());
            });

            Assert.False(nextChamado);
            Assert.IsType<UnprocessableEntityObjectResult>(context.Result);
        }

        [Fact]
        public async Task Engine_sysmiddle_como_string_no_body_e_recusado()
        {
            var filtro = new MappingEngineGuardFilter();
            var context = CriarExecutingContext(new Dictionary<string, StringValues>(), bodyJson: "{\"engine\":\"sysmiddle\"}");
            var nextChamado = false;

            await filtro.OnActionExecutionAsync(context, () =>
            {
                nextChamado = true;
                return Task.FromResult(CriarExecutedContext());
            });

            Assert.False(nextChamado);
            Assert.IsType<UnprocessableEntityObjectResult>(context.Result);
        }

        [Fact]
        public async Task Engine_sysmiddle_como_array_no_body_e_recusado()
        {
            var filtro = new MappingEngineGuardFilter();
            var context = CriarExecutingContext(new Dictionary<string, StringValues>(), bodyJson: "{\"engine\":[\"tcl\",\"sysmiddle\"]}");
            var nextChamado = false;

            await filtro.OnActionExecutionAsync(context, () =>
            {
                nextChamado = true;
                return Task.FromResult(CriarExecutedContext());
            });

            Assert.False(nextChamado);
            Assert.IsType<UnprocessableEntityObjectResult>(context.Result);
        }

        [Fact]
        public async Task Engine_sysmiddle_como_objeto_no_body_e_recusado_failclosed()
        {
            var filtro = new MappingEngineGuardFilter();
            var context = CriarExecutingContext(new Dictionary<string, StringValues>(), bodyJson: "{\"engine\":{\"value\":\"sysmiddle\"}}");
            var nextChamado = false;

            await filtro.OnActionExecutionAsync(context, () =>
            {
                nextChamado = true;
                return Task.FromResult(CriarExecutedContext());
            });

            Assert.False(nextChamado);
            Assert.IsType<UnprocessableEntityObjectResult>(context.Result);
        }

        [Fact]
        public async Task Engine_ausente_nao_e_bloqueada_pelo_filtro_mas_MappingDraftsController_recusa_a_ausencia()
        {
            // O filtro não bloqueia ausência (design §3.3) — a responsabilidade é do controller.
            // Aqui provamos as duas metades da garantia juntas: filtro deixa passar, controller recusa.
            var filtro = new MappingEngineGuardFilter();
            var context = CriarExecutingContext(new Dictionary<string, StringValues>());
            var nextChamado = false;

            await filtro.OnActionExecutionAsync(context, () =>
            {
                nextChamado = true;
                return Task.FromResult(CriarExecutedContext());
            });

            Assert.True(nextChamado);

            var store = new FakeDraftStore();
            var user = Guid.NewGuid();
            var workspaceId = Guid.NewGuid();
            var identityService = new FakeIdentityWorkspaceService();
            identityService.Memberships.Add((workspaceId, user));

            var controller = new MappingDraftsController(
                store, new FakeSuggestionService(), identityService, new FakeCurrentUser { UserId = user }, NullLogger<MappingDraftsController>.Instance);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

            var result = await controller.CreateDraft(workspaceId, Guid.NewGuid(), new CreateDraftRequest { RevisionId = Guid.NewGuid() }, CancellationToken.None);

            Assert.IsType<UnprocessableEntityObjectResult>(result);
        }

        [Fact]
        public async Task Query_e_body_divergentes_sysmiddle_em_qualquer_um_basta_para_recusar()
        {
            // Regressão do Slice 3: engine=xslt na query não deve fazer o filtro "sair cedo" sem
            // checar o body. Replicado aqui como parte do gate consolidado (não reimplementado).
            var filtro = new MappingEngineGuardFilter();
            var context = CriarExecutingContext(
                new Dictionary<string, StringValues> { ["engine"] = "xslt" },
                bodyJson: "{\"engine\":\"sysmiddle\"}");
            var nextChamado = false;

            await filtro.OnActionExecutionAsync(context, () =>
            {
                nextChamado = true;
                return Task.FromResult(CriarExecutedContext());
            });

            Assert.False(nextChamado);
            Assert.IsType<UnprocessableEntityObjectResult>(context.Result);
        }

        [Fact]
        public async Task ContentType_nao_JSON_engine_sysmiddle_no_body_nao_e_inspecionado_pelo_filtro()
        {
            // Vetor teórico documentado no design §3.5: o filtro só lê corpo JSON
            // (HasJsonContentType). Body multipart/form-urlencoded não é parseado — isso é
            // esperado, e nenhum endpoint dos Slices 1-5 aceita "engine" fora de JSON puro
            // (todos os DTOs de CreateDraft/CreateTestRun são [FromBody] JSON). Documentamos o
            // comportamento em vez de normalizar silenciosamente: se algum endpoint futuro passar
            // a aceitar form-urlencoded com "engine", este teste precisa ser revisitado.
            var filtro = new MappingEngineGuardFilter();
            var httpContext = new DefaultHttpContext();
            var bytes = System.Text.Encoding.UTF8.GetBytes("engine=sysmiddle");
            httpContext.Request.Body = new MemoryStream(bytes);
            httpContext.Request.ContentType = "application/x-www-form-urlencoded";
            httpContext.Request.ContentLength = bytes.Length;
            var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
            var context = new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller: new object());
            var nextChamado = false;

            await filtro.OnActionExecutionAsync(context, () =>
            {
                nextChamado = true;
                return Task.FromResult(CriarExecutedContext());
            });

            // Passa despercebido pelo filtro — vetor teórico confirmado, não explorável hoje porque
            // nenhum controller fiscal lê "engine" de um form body.
            Assert.True(nextChamado);
        }

        // ── 2) MappingExplanationController: engine=sysmiddle É permitido (único caso legítimo) ──

        [Fact]
        public async Task MappingExplanationController_engine_sysmiddle_via_MapperGuid_retorna_200()
        {
            var workspaceId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var identityService = new FakeIdentityWorkspaceService();
            identityService.Memberships.Add((workspaceId, userId));

            var cache = new FakeCachedMapperService();
            cache.Mappers.Add(BuildSysmiddleMapper("MAP_SYSMIDDLE_OK"));
            var sysmiddleAdapter = new SysmiddleExplanationAdapter(cache, NullLogger<SysmiddleExplanationAdapter>.Instance);
            var draftStore = new FakeDraftStore();
            var tclAdapter = new TclExplanationAdapter(draftStore, NullLogger<TclExplanationAdapter>.Instance);
            var xsltAdapter = new XsltExplanationAdapter(draftStore, NullLogger<XsltExplanationAdapter>.Instance);

            var controller = new MappingExplanationController(
                draftStore,
                new IMappingExplanationAdapter[] { sysmiddleAdapter, tclAdapter, xsltAdapter },
                identityService,
                new FakeCurrentUser { UserId = userId },
                NullLogger<MappingExplanationController>.Instance);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

            // "engine=sysmiddle" aqui é conceitual: mappingId não é um draftId (guid), então o
            // controller cai no fallback do MapperGuid Sysmiddle real (design §3, fluxo 2).
            var result = await controller.GetExplanation(workspaceId, "MAP_SYSMIDDLE_OK", "current", CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.NotNull(ok.Value);
        }

        [Fact]
        public async Task MappingExplanationController_mesmo_permitindo_sysmiddle_nunca_habilita_Author()
        {
            // Reafirma o achado do Slice 4: Capabilities.Author é sempre false, hard-coded — não é
            // possível "ligar" autoria via nenhum payload, nem no único endpoint que aceita sysmiddle.
            var cache = new FakeCachedMapperService();
            cache.Mappers.Add(BuildSysmiddleMapper("MAP_AUTHOR_CHECK"));
            var adapter = new SysmiddleExplanationAdapter(cache, NullLogger<SysmiddleExplanationAdapter>.Instance);

            var explanation = await adapter.ExplainAsync(
                new MappingExplanationRequest(Guid.NewGuid(), Guid.NewGuid(), "MAP_AUTHOR_CHECK", "current"), CancellationToken.None);

            Assert.NotNull(explanation);
            Assert.False(explanation!.Capabilities.Author);
            Assert.False(explanation.Capabilities.Publish);
            Assert.True(explanation.Capabilities.Explain);
        }

        // ── 3) Pathway antigo (execução pré-existente): ausência estrutural de capacidade de autoria ──

        [Fact]
        public void Nenhum_tipo_carregado_na_API_parece_um_writer_ou_serializer_Sysmiddle()
        {
            // Teste de caracterização (design §5.3): a garantia do pathway antigo é "nunca existiu
            // capacidade de autoria", não um comportamento testável em runtime — então provamos
            // varrendo os tipos carregados do assembly da API por qualquer nome que sugira
            // escrita/serialização do formato Sysmiddle. Se este teste falhar, é achado crítico:
            // NÃO normalizar, reportar antes de seguir.
            var apiAssembly = typeof(TransformationExecutionController).Assembly;

            var suspeitos = apiAssembly.GetTypes()
                .Where(t => t.Name.Contains("Sysmiddle", StringComparison.OrdinalIgnoreCase))
                .Where(t =>
                    t.Name.Contains("Writer", StringComparison.OrdinalIgnoreCase) ||
                    t.Name.Contains("Serializer", StringComparison.OrdinalIgnoreCase) ||
                    t.Name.Contains("Encoder", StringComparison.OrdinalIgnoreCase) ||
                    t.Name.Contains("Author", StringComparison.OrdinalIgnoreCase) ||
                    t.Name.Contains("Publisher", StringComparison.OrdinalIgnoreCase) ||
                    t.Name.Contains("Generator", StringComparison.OrdinalIgnoreCase))
                .ToList();

            Assert.True(suspeitos.Count == 0,
                $"Tipo(s) suspeito(s) de capacidade de autoria Sysmiddle encontrado(s): {string.Join(", ", suspeitos.Select(t => t.FullName))}");
        }

        [Fact]
        public void ExecuteSysmiddleCandidatesAsync_e_privado_e_nao_expoe_metodo_publico_de_escrita()
        {
            // Confirma, via reflection, que o método que interage com o pathway Sysmiddle antigo
            // não é uma superfície pública de escrita — é privado, chamado internamente pelo pipeline
            // de execução de candidatos (Controllers/TransformationExecutionController.cs).
            var metodo = typeof(TransformationExecutionController)
                .GetMethod("ExecuteSysmiddleCandidatesAsync", BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.NotNull(metodo);
            Assert.False(metodo!.IsPublic);

            // Nenhum método público do controller tem "Write"/"Publish"/"Author"/"Create" no nome
            // associado a Sysmiddle — os únicos métodos públicos são endpoints HTTP de execução/consulta.
            var metodosPublicosSuspeitos = typeof(TransformationExecutionController)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.Name.Contains("Sysmiddle", StringComparison.OrdinalIgnoreCase))
                .Where(m =>
                    m.Name.Contains("Write", StringComparison.OrdinalIgnoreCase) ||
                    m.Name.Contains("Publish", StringComparison.OrdinalIgnoreCase) ||
                    m.Name.Contains("Author", StringComparison.OrdinalIgnoreCase) ||
                    (m.Name.Contains("Create", StringComparison.OrdinalIgnoreCase) && !m.Name.Contains("CreateDraft", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            Assert.Empty(metodosPublicosSuspeitos);
        }

        // ── 4) RBAC/role — não é vetor adicional hoje (documentado, não é lacuna deste slice) ──

        [Fact]
        public void Nenhum_controller_fiscal_tem_Authorize_hoje_o_gate_unico_e_o_filtro()
        {
            // Confirma o que o design §4 documenta: como nenhum controller tem [Authorize]/
            // enforcement de papel ainda, não existe rota "privilegiada" que bypasse o
            // MappingEngineGuardFilter — o filtro é a única barreira, universal, para todo mundo.
            // Isso NÃO é uma lacuna do Slice 6 (autorização por papel é decisão de produto em
            // aberto, ver docs/architecture/rollout-p2-autenticacao.md) — é uma nota estrutural.
            var controllersFiscais = new[]
            {
                typeof(MappingDraftsController),
                typeof(MappingCompilationController),
                typeof(MappingExplanationController),
            };

            foreach (var controllerType in controllersFiscais)
            {
                var temAuthorize = controllerType.GetCustomAttributes(inherit: true)
                    .Any(a => a.GetType().Name == "AuthorizeAttribute");
                Assert.False(temAuthorize, $"{controllerType.Name} não deveria ter [Authorize] ainda — RBAC é escopo futuro (rollout-p2-autenticacao.md).");
            }
        }

        // ── helpers ──

        private static ActionExecutingContext CriarExecutingContext(Dictionary<string, StringValues> query, string? bodyJson = null)
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Request.QueryString = QueryString.Create(query);

            if (bodyJson is not null)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(bodyJson);
                httpContext.Request.Body = new MemoryStream(bytes);
                httpContext.Request.ContentType = "application/json";
                httpContext.Request.ContentLength = bytes.Length;
            }

            var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());

            return new ActionExecutingContext(
                actionContext,
                new List<IFilterMetadata>(),
                new Dictionary<string, object?>(),
                controller: new object());
        }

        private static ActionExecutedContext CriarExecutedContext()
        {
            var httpContext = new DefaultHttpContext();
            var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
            return new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), controller: new object());
        }

        private static Mapper BuildSysmiddleMapper(string mapperGuid) => new()
        {
            MapperGuid = mapperGuid,
            Name = "Mapper de teste do gate",
            Description = "Descrição",
            InputLayoutGuid = "FLD_IN",
            TargetLayoutGuid = "TAG_OUT",
            DecryptedContent = $"""
                <MapperVO>
                    <MapperGuid>{mapperGuid}</MapperGuid>
                    <Name>Mapper de teste do gate</Name>
                    <InputLayoutGuid>FLD_IN</InputLayoutGuid>
                    <TargetLayoutGuid>TAG_OUT</TargetLayoutGuid>
                    <Rule>
                        <Name>RegraTeste</Name>
                        <Sequence>1</Sequence>
                        <ElementGuid>ATT_1</ElementGuid>
                        <TargetElementGuid>ATT_1</TargetElementGuid>
                        <ContentValue>%beginRuleContent;T.xMun=I.LINHA1/Campo;%endRuleContent;</ContentValue>
                    </Rule>
                </MapperVO>
                """,
        };

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

        private sealed class FakeDraftStore : IMappingDraftStore
        {
            public Dictionary<Guid, MappingDraftDetail> Drafts { get; } = new();
            public Dictionary<(Guid DraftId, Guid RuleId), MappingDraftRuleDetail> Rules { get; } = new();

            public Task<bool> RevisionBelongsToPackageAsync(Guid packageId, Guid revisionId, CancellationToken cancellationToken)
                => Task.FromResult(true);

            public Task<IReadOnlyList<ArtifactFileRef>> GetArtifactFilesForRevisionAsync(Guid revisionId, CancellationToken cancellationToken)
                => Task.FromResult<IReadOnlyList<ArtifactFileRef>>(Array.Empty<ArtifactFileRef>());

            public Task<MappingDraftDetail> CreateDraftAsync(Guid workspaceId, Guid packageId, Guid revisionId, Guid createdByUserId, string engine, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task<MappingDraftDetail?> GetDraftIfMemberAsync(Guid draftId, Guid userId, CancellationToken cancellationToken)
                => Task.FromResult(Drafts.TryGetValue(draftId, out var draft) ? draft : null);

            public Task<MappingDraftRuleDetail?> GetRuleIfMemberAsync(Guid draftId, Guid ruleId, Guid userId, CancellationToken cancellationToken)
                => Task.FromResult(Rules.TryGetValue((draftId, ruleId), out var rule) ? rule : null);

            public Task InsertProposedRulesAsync(Guid draftId, Guid jobId, IReadOnlyList<MappingDraftRuleProposal> proposals, CancellationToken cancellationToken)
                => Task.CompletedTask;

            public Task<UpdateRuleOutcome> UpdateRuleStatusAsync(
                Guid draftId, Guid ruleId, Guid userId, byte[] expectedRowVersion, string newStatus, string? justification,
                IReadOnlyList<string>? editedSourceRefs, IReadOnlyList<string>? editedTargetRefs, string? editedOperation,
                CancellationToken cancellationToken)
                => throw new NotSupportedException();
        }

        private sealed class FakeSuggestionService : IMappingSuggestionService
        {
            public Guid EnqueuedJobId { get; } = Guid.NewGuid();

            public Task<Guid> EnqueueAsync(Guid draftId, Guid workspaceId, Guid revisionId, string engine, CancellationToken cancellationToken)
                => Task.FromResult(EnqueuedJobId);

            public Task<SuggestionJobState?> GetStatusAsync(Guid jobId, CancellationToken cancellationToken)
                => Task.FromResult<SuggestionJobState?>(new SuggestionJobState { JobId = jobId, Status = SuggestionJobStatus.Running });

            public Task<bool> CancelAsync(Guid jobId, CancellationToken cancellationToken)
                => Task.FromResult(true);
        }

        private sealed class FakeCachedMapperService : ICachedMapperService
        {
            public List<Mapper> Mappers { get; } = new();

            public Task<List<Mapper>> GetAllMappersAsync() => Task.FromResult(Mappers);
            public Task<List<Mapper>> GetMappersByInputLayoutGuidAsync(string inputLayoutGuid) => Task.FromResult(new List<Mapper>());
            public Task<List<Mapper>> GetMappersByTargetLayoutGuidAsync(string targetLayoutGuid) => Task.FromResult(new List<Mapper>());
            public Task RefreshCacheFromDatabaseAsync() => Task.CompletedTask;
        }
    }
}
