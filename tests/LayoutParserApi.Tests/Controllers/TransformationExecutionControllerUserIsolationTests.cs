using System.Reflection;

using LayoutParserApi.Controllers;
using LayoutParserApi.Models;
using LayoutParserApi.Models.Database;
using LayoutParserApi.Models.Transformation;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Transformation.LowCode;
using LayoutParserApi.Services.Transformation.Ai;

using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Tests.Controllers
{
    /// <summary>
    /// Issue #93: os endpoints <c>execute-candidates</c>, <c>ia-status</c> e <c>execute-lowcode</c>
    /// deixaram de exigir o papel "admin" ([Authorize(Roles = "admin")] → [Authorize]) — agora
    /// qualquer usuário autenticado pode chamá-los. O isolamento entre usuários (issue #92) passa a
    /// ser a ÚNICA barreira que impede um usuário ler/afetar o ticket de outro.
    ///
    /// <para>Gap encontrado pelo @lp-qa ao verificar a #92: nenhum teste no nível do
    /// <see cref="TransformationExecutionController"/> confirmava que <c>CurrentUserId</c> (derivado
    /// de <see cref="ICurrentUser.Name"/>) é de fato o valor que chega em
    /// <see cref="IAiTransformationCandidateService.GetStatusAsync"/>/<see cref="IAiTransformationCandidateService.EnqueueAsync"/>.
    /// O QA provou o buraco mutando o controller (trocando <c>CurrentUserId</c> por um valor fixo) e
    /// viu a suíte inteira (337/337) continuar verde. Os testes abaixo fecham esse buraco: usam um
    /// spy de <see cref="IAiTransformationCandidateService"/> que CAPTURA o <c>userId</c> recebido, e
    /// comparam contra o nome do usuário fake — não apenas "não lançou exceção".</para>
    /// </summary>
    public class TransformationExecutionControllerUserIsolationTests
    {
        // --- fakes ---

        private sealed class FakeCurrentUser : ICurrentUser
        {
            public string? Name { get; set; }
            public IReadOnlyList<string> Roles { get; set; } = Array.Empty<string>();
            public bool IsAuthenticated => Name != null;
            public bool IsInRole(string role) => Roles.Contains(role, StringComparer.OrdinalIgnoreCase);
            public Guid? UserId => null;
        }

        /// <summary>Spy: captura o userId recebido em cada chamada, sem executar lógica real de IA.</summary>
        private sealed class SpyAiCandidateService : IAiTransformationCandidateService
        {
            public string? LastEnqueueUserId { get; private set; }
            public string? LastGetStatusUserId { get; private set; }
            public string? LastGetStatusTicket { get; private set; }

            public Task EnqueueAsync(
                string userId, string ticket, string layoutName, Guid layoutGuid, string mapperGuid,
                string inputContent, string? groundTruthXml, CancellationToken cancellationToken,
                IReadOnlyList<Models.Entities.ParsedField>? parsedFields = null)
            {
                LastEnqueueUserId = userId;
                return Task.CompletedTask;
            }

            public Task<AiCandidateStatus> GetStatusAsync(string userId, string ticket, CancellationToken cancellationToken)
            {
                LastGetStatusUserId = userId;
                LastGetStatusTicket = ticket;
                return Task.FromResult(new AiCandidateStatus { Status = AiCandidateStatus.StatusNotFound });
            }
        }

        /// <summary>Spy do circuito de proteção do fallback automático de IA (nunca em cooldown por padrão).</summary>
        private sealed class SpyAiFallbackSuppressionGate : IAiFallbackSuppressionGate
        {
            public bool IsInCooldown(Guid layoutGuid, out DateTimeOffset retryAt)
            {
                retryAt = default;
                return false;
            }

            public void RegisterFailure(Guid layoutGuid, TimeSpan cooldown) { }

            public void ClearCooldown(Guid layoutGuid) { }
        }

        /// <summary>
        /// Constrói o controller real. Os demais serviços concretos (pipeline/validator/learning/
        /// low-code/low-code-auto) recebem <c>null!</c> deliberadamente: o construtor do controller só
        /// os armazena, nunca os invoca, e os testes abaixo não exercitam nenhum caminho que os toque
        /// (GetAiCandidateStatus só usa _aiCandidateService/_currentUser; o teste de
        /// TryEnqueueAiCandidate invoca o método privado diretamente via reflection).
        /// </summary>
        private static (TransformationExecutionController Controller, SpyAiCandidateService Spy, FakeCurrentUser User) BuildController()
        {
            var spy = new SpyAiCandidateService();
            var user = new FakeCurrentUser();

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
                aiCandidateService: spy,
                aiFallbackGate: new SpyAiFallbackSuppressionGate(),
                currentUser: user,
                mapperDb: null!,
                layoutParser: null!,
                fieldMappingComposition: null!,
                scopeFactory: null!);

            return (controller, spy, user);
        }

        [Fact]
        public async Task GetAiCandidateStatus_propaga_CurrentUserId_para_o_servico()
        {
            var (controller, spy, user) = BuildController();
            user.Name = "alice";

            await controller.GetAiCandidateStatus("ticket-123", CancellationToken.None);

            Assert.Equal("alice", spy.LastGetStatusUserId);
            Assert.Equal("ticket-123", spy.LastGetStatusTicket);
        }

        [Fact]
        public async Task GetAiCandidateStatus_usuarios_diferentes_propagam_ids_diferentes()
        {
            var (controller, spy, user) = BuildController();

            user.Name = "alice";
            await controller.GetAiCandidateStatus("ticket-x", CancellationToken.None);
            Assert.Equal("alice", spy.LastGetStatusUserId);

            user.Name = "bob";
            await controller.GetAiCandidateStatus("ticket-x", CancellationToken.None);
            Assert.Equal("bob", spy.LastGetStatusUserId);
        }

        /// <summary>
        /// TryEnqueueAiCandidate é privado, chamado de dentro de ExecuteTransformationCandidates
        /// como <c>TryEnqueueAiCandidate(request, layoutRecord, candidates, isXmlInput, CurrentUserId)</c>.
        /// Rodar o método público inteiro exigiria o pathway sysmiddle real (LowCodeAutoTransformationService,
        /// que dispara um runner x86 externo) só para produzir o candidato-gabarito que
        /// AiCandidateDispatchPlan.TryBuild exige — fora do alcance de um teste unitário. Em vez disso,
        /// este teste invoca o método privado via reflection passando o MESMO valor que a linha de
        /// produção passa (a property <c>CurrentUserId</c>, também lida via reflection, não reimplementada
        /// aqui) — cobre a mesma classe de regressão que o QA demonstrou (CurrentUserId virar um valor
        /// fixo), porque o valor esperado da asserção vem do usuário fake, não da property.
        /// </summary>
        [Fact]
        public async Task TryEnqueueAiCandidate_propaga_CurrentUserId_para_EnqueueAsync()
        {
            var (controller, spy, user) = BuildController();
            user.Name = "carol";

            var request = new TransformationRequest
            {
                InputContent = "linha-posicional-de-teste",
                LayoutName = "LAYOUT_TESTE",
                LayoutGuid = null
            };
            var layoutGuid = Guid.NewGuid();
            var layoutRecord = new LayoutRecord { LayoutGuid = layoutGuid, Name = request.LayoutName };

            // Candidato sysmiddle bem-sucedido — é o gabarito que AiCandidateDispatchPlan.TryBuild exige
            // para não retornar null (ver Services/Transformation/Ai/AiCandidateDispatchPlan.cs).
            var candidates = new List<TransformationCandidate>
            {
                new TransformationCandidate
                {
                    CandidateId = $"sysmiddle-{Guid.NewGuid()}",
                    Pathway = "sysmiddle",
                    TransformedXml = "<xml>gabarito</xml>"
                }
            };

            var currentUserIdProperty = typeof(TransformationExecutionController)
                .GetProperty("CurrentUserId", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("Property CurrentUserId não encontrada — o controller mudou de forma incompatível com este teste.");
            var currentUserId = (string)currentUserIdProperty.GetValue(controller)!;
            Assert.Equal("carol", currentUserId); // sanidade: a property em si já reflete o usuário fake

            var method = typeof(TransformationExecutionController)
                .GetMethod("TryEnqueueAiCandidate", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("Método TryEnqueueAiCandidate não encontrado — o controller mudou de forma incompatível com este teste.");

            // ✅ Correção pós-review da Quinn (2026-08-29): TryEnqueueAiCandidate deixou de ser
            // "await"ado no caminho síncrono do controller — o ParseAsync/EnqueueAsync agora rodam
            // dentro de um Task.Run fire-and-forget (nunca atrasa a resposta síncrona). O
            // method.Invoke abaixo retorna antes do job terminar, então o teste faz polling
            // (com teto de sanidade) em vez de assumir conclusão síncrona.
            method.Invoke(controller, new object?[] { request, layoutRecord, candidates, false, currentUserId });

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (spy.LastEnqueueUserId == null && DateTime.UtcNow < deadline)
                await Task.Delay(10);

            Assert.Equal("carol", spy.LastEnqueueUserId);
        }

        // --- Fallback automático de IA (design-fallback-ia-automatico-2026-08-16.md) ---
        // TryEnqueueAiFallback é privado — mesma técnica de reflection do teste acima, pelo mesmo
        // motivo (exercitar o pathway sysmiddle real fugiria do escopo de um teste unitário).

        private static List<PathwayDiagnostic> InvokeTryEnqueueAiFallback(
            TransformationExecutionController controller, TransformationRequest request, LayoutRecord layoutRecord,
            bool isXmlInput, IEnumerable<FailureKind> failureKinds, List<string> warnings, string userId)
        {
            var bag = new System.Collections.Concurrent.ConcurrentBag<FailureKind>(failureKinds);
            var diagnostics = new System.Collections.Concurrent.ConcurrentBag<PathwayDiagnostic>();
            var method = typeof(TransformationExecutionController)
                .GetMethod("TryEnqueueAiFallback", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("Método TryEnqueueAiFallback não encontrado — o controller mudou de forma incompatível com este teste.");

            method.Invoke(controller, new object?[] { request, layoutRecord, isXmlInput, bag, warnings, diagnostics, userId });
            return diagnostics.ToList();
        }

        [Fact]
        public void TryEnqueueAiFallback_EstadoA_nao_modelado_dispara_o_fallback()
        {
            var (controller, spy, user) = BuildController();
            user.Name = "dave";

            var layoutGuid = Guid.NewGuid();
            var request = new TransformationRequest
            {
                InputContent = "linha-posicional-sem-mapper",
                LayoutName = "LAYOUT_SEM_MAPPER",
                LayoutGuid = layoutGuid.ToString()
            };
            var layoutRecord = new LayoutRecord { LayoutGuid = layoutGuid, Name = request.LayoutName };
            var warnings = new List<string>();

            // Estado A: nenhum pathway falhou por infra — só "não aplicável"/"sem heurística".
            var diagnostics = InvokeTryEnqueueAiFallback(
                controller, request, layoutRecord, isXmlInput: false,
                failureKinds: new[] { FailureKind.NotApplicable, FailureKind.NotApplicable },
                warnings, "dave");

            Assert.Equal("dave", spy.LastEnqueueUserId);
            Assert.Contains(warnings, w => w.Contains("fallback automático de IA enfileirado", StringComparison.OrdinalIgnoreCase));
            var diag = Assert.Single(diagnostics);
            Assert.Equal("ai-fallback", diag.Pathway);
            Assert.Equal("candidate_generated", diag.Status);
        }

        [Fact]
        public void TryEnqueueAiFallback_EstadoB_falha_de_infra_NAO_dispara_o_fallback()
        {
            var (controller, spy, user) = BuildController();
            user.Name = "erin";

            var layoutGuid = Guid.NewGuid();
            var request = new TransformationRequest
            {
                InputContent = "linha-posicional-com-mapper-mas-runner-fora-do-ar",
                LayoutName = "LAYOUT_COM_MAPPER",
                LayoutGuid = layoutGuid.ToString()
            };
            var layoutRecord = new LayoutRecord { LayoutGuid = layoutGuid, Name = request.LayoutName };
            var warnings = new List<string>();

            // Estado B: pelo menos um pathway falhou por infra — mapper existe, IA não deve tentar
            // recriar algo que já é a fonte de verdade (regressão explícita do caso já diagnosticado
            // em diagnostico-mapper-nao-encontrado-producao-2026-08-15.md). Nenhum diagnóstico próprio
            // de "ai-fallback" é emitido aqui — o item failed do pathway que quebrou já é o sinal.
            var diagnostics = InvokeTryEnqueueAiFallback(
                controller, request, layoutRecord, isXmlInput: false,
                failureKinds: new[] { FailureKind.ExecutionInfraError, FailureKind.NotApplicable },
                warnings, "erin");

            Assert.Null(spy.LastEnqueueUserId);
            Assert.DoesNotContain(warnings, w => w.Contains("fallback automático de IA enfileirado", StringComparison.OrdinalIgnoreCase));
            Assert.Empty(diagnostics);
        }

        // --- TAREFA 3 (regressão geral): os 3 endpoints deixaram de exigir o papel "admin" ---

        [Theory]
        [InlineData(nameof(TransformationExecutionController.ExecuteTransformationCandidates))]
        [InlineData(nameof(TransformationExecutionController.GetAiCandidateStatus))]
        [InlineData(nameof(TransformationExecutionController.ExecuteLowCode))]
        public void Endpoint_exige_autenticacao_mas_nao_mais_o_papel_admin(string methodName)
        {
            var method = typeof(TransformationExecutionController)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .First(m => m.Name == methodName);

            var authorize = method.GetCustomAttribute<AuthorizeAttribute>();

            Assert.NotNull(authorize);
            Assert.Null(authorize!.Roles); // antes era "admin" (issue #32) — issue #93 abriu para qualquer autenticado
        }

        [Fact]
        public void Outros_endpoints_do_controller_nao_ganharam_Authorize()
        {
            // Confirma que só os 3 endpoints acima mudaram — os demais (execute, validate,
            // learn-from-examples, run-test) continuam sem [Authorize] próprio.
            var outrosEndpoints = new[]
            {
                nameof(TransformationExecutionController.ExecuteTransformation),
                nameof(TransformationExecutionController.ValidateTransformation),
                nameof(TransformationExecutionController.LearnFromExamples),
                nameof(TransformationExecutionController.RunTransformationTest),
            };

            foreach (var methodName in outrosEndpoints)
            {
                var method = typeof(TransformationExecutionController)
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .First(m => m.Name == methodName);

                Assert.Null(method.GetCustomAttribute<AuthorizeAttribute>());
            }
        }
    }
}
