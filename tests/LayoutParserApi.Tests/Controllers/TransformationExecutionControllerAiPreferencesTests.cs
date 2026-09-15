using LayoutParserApi.Controllers;
using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Transformation.LowCode;
using LayoutParserApi.Services.Transformation.Ai;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Tests.Controllers
{
    /// <summary>
    /// Issue #322: preferências de usuário além do prompt customizado (idioma, nível de detalhe da
    /// explicação, engine padrão TCL/XSLT). Persistência real em <see cref="SqlAiUserSessionStore"/>
    /// (<c>tbLpAiUserSession</c>) — não há banco disponível no ambiente de teste, então a store aponta
    /// para um host inexistente e degrada graciosamente (mesmo padrão de
    /// <c>SqlAiUserSessionStoreRetentionTests</c>): <c>SetPreferencesAsync</c> não lança,
    /// <c>GetPreferencesAsync</c> devolve <c>null</c> e o endpoint aplica os defaults de
    /// <see cref="AiUserPreferenceDefaults"/>. Os testes aqui cobrem o contrato do endpoint
    /// (validação 422, fail-closed 404, formato da resposta) — não o round-trip real de persistência,
    /// que exigiria SQL Server.
    /// </summary>
    public class TransformationExecutionControllerAiPreferencesTests
    {
        private sealed class FakeCurrentUser : ICurrentUser
        {
            public string? Name { get; set; }
            public IReadOnlyList<string> Roles { get; set; } = Array.Empty<string>();
            public bool IsAuthenticated => Name != null;
            public bool IsInRole(string role) => Roles.Contains(role, StringComparer.OrdinalIgnoreCase);
            public Guid? UserId => null;
        }

        private static (TransformationExecutionController Controller, FakeCurrentUser User, AiUserInstructionStore PromptStore) BuildController()
        {
            var user = new FakeCurrentUser();
            var promptStore = new AiUserInstructionStore();

            var configValues = new Dictionary<string, string?>
            {
                ["IdentityDatabase:Server"] = "host-inexistente-lpapi-teste.invalid,1",
                ["IdentityDatabase:Database"] = "db",
                ["IdentityDatabase:UserId"] = "user",
                ["IdentityDatabase:Password"] = "pwd",
            };
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();

            var sessionStore = new SqlAiUserSessionStore(
                NullLogger<SqlAiUserSessionStore>.Instance,
                configuration,
                Options.Create(new AiUserSessionHistoryOptions()));

            var controller = new TransformationExecutionController(
                NullLogger<TransformationExecutionController>.Instance,
                pipelineService: null!,
                validatorService: null!,
                learningService: null!,
                autoGenerator: null!,
                lowCode: null!,
                lowCodeAuto: null!,
                layoutDb: null!,
                lowCodeOptions: Options.Create(new LowCodeRunnerOptions()),
                aiCandidateService: null!,
                aiFallbackGate: null!,
                aiUserInstructionStore: promptStore,
                aiUserSessionStore: sessionStore,
                currentUser: user,
                mapperDb: null!,
                layoutParser: null!,
                fieldMappingComposition: null!,
                scopeFactory: null!,
                canaryAlert: new LayoutParserApi.Services.Security.CanaryAlertService(
                    NullLogger<LayoutParserApi.Services.Security.CanaryAlertService>.Instance),
                fieldCorrectionStore: null!,
                trainingDataCapture: null!);

            return (controller, user, promptStore);
        }

        [Fact]
        public async Task GetAiPreferences_sem_preferencia_salva_aplica_defaults()
        {
            var (controller, user, _) = BuildController();
            user.Name = "alice";

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = Assert.IsType<OkObjectResult>(await controller.GetAiPreferences(cts.Token));

            var value = result.Value!;
            var type = value.GetType();
            Assert.Equal(AiUserPreferenceDefaults.DefaultLanguage, type.GetProperty("preferredLanguage")!.GetValue(value));
            Assert.Equal(AiUserPreferenceDefaults.DefaultExplanationDetailLevel, type.GetProperty("preferredExplanationDetailLevel")!.GetValue(value));
            Assert.Equal(AiUserPreferenceDefaults.DefaultTransformationEngine, type.GetProperty("defaultTransformationEngine")!.GetValue(value));
        }

        [Fact]
        public async Task GetAiPreferences_inclui_customPromptInstruction_do_store_em_memoria()
        {
            var (controller, user, promptStore) = BuildController();
            user.Name = "bob";
            promptStore.Set("bob", "instrução customizada do bob");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = Assert.IsType<OkObjectResult>(await controller.GetAiPreferences(cts.Token));

            var value = result.Value!;
            var custom = value.GetType().GetProperty("customPromptInstruction")!.GetValue(value);
            Assert.Equal("instrução customizada do bob", custom);
        }

        [Fact]
        public async Task GetAiPreferences_sem_identidade_devolve_404()
        {
            var (controller, user, _) = BuildController();
            user.Name = null;

            var result = await controller.GetAiPreferences(CancellationToken.None);

            Assert.IsType<NotFoundResult>(result);
        }

        [Fact]
        public async Task SetAiPreferences_sem_identidade_devolve_404()
        {
            var (controller, user, _) = BuildController();
            user.Name = null;

            var result = await controller.SetAiPreferences(new SetAiPreferencesRequest(), CancellationToken.None);

            Assert.IsType<NotFoundResult>(result);
        }

        [Fact]
        public async Task SetAiPreferences_engine_invalido_devolve_422()
        {
            var (controller, user, _) = BuildController();
            user.Name = "carol";

            var request = new SetAiPreferencesRequest { DefaultTransformationEngine = "cobol" };
            var result = await controller.SetAiPreferences(request, CancellationToken.None);

            var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(result);
            Assert.Equal(422, unprocessable.StatusCode);
        }

        [Fact]
        public async Task SetAiPreferences_detail_level_invalido_devolve_422()
        {
            var (controller, user, _) = BuildController();
            user.Name = "dave";

            var request = new SetAiPreferencesRequest { PreferredExplanationDetailLevel = "verbose" };
            var result = await controller.SetAiPreferences(request, CancellationToken.None);

            var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(result);
            Assert.Equal(422, unprocessable.StatusCode);
        }

        [Theory]
        [InlineData(AiUserPreferenceDefaults.TransformationEngineTcl)]
        [InlineData(AiUserPreferenceDefaults.TransformationEngineXslt)]
        public async Task SetAiPreferences_engine_valido_e_aceito(string engine)
        {
            var (controller, user, _) = BuildController();
            user.Name = "erin";

            var request = new SetAiPreferencesRequest { DefaultTransformationEngine = engine };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = await controller.SetAiPreferences(request, cts.Token);

            var ok = Assert.IsType<OkObjectResult>(result);
            var saved = ok.Value!.GetType().GetProperty("saved")!.GetValue(ok.Value);
            Assert.Equal(true, saved);
        }

        [Fact]
        public async Task SetAiPreferences_degrada_graciosamente_quando_sql_inacessivel()
        {
            // A store aponta para host inexistente (ver BuildController) — o endpoint não pode lançar
            // nem devolver erro de infra por causa disso (mesmo princípio de resiliência do resto da store).
            var (controller, user, _) = BuildController();
            user.Name = "frank";

            var request = new SetAiPreferencesRequest { PreferredLanguage = "en-US" };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = await controller.SetAiPreferences(request, cts.Token);

            Assert.IsType<OkObjectResult>(result);
        }
    }
}
