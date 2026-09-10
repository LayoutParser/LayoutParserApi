using System.Reflection;

using LayoutParserApi.Controllers;
using LayoutParserApi.Models;
using LayoutParserApi.Models.Database;
using LayoutParserApi.Models.Fiscal;
using LayoutParserApi.Models.Transformation;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Transformation.Ai;
using LayoutParserApi.Services.Transformation.LowCode;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Tests.Controllers
{
    /// <summary>
    /// Issue #345 (ADR docs/architecture/adr-contrato-correcao-guiada-humano-2026-09-08.md): cobre
    /// (1) a gravação best-effort de <c>tbFieldCorrectionContext</c> em <c>execute-candidates</c>
    /// (via reflection sobre o método privado, mesma técnica já usada por
    /// <see cref="TransformationExecutionControllerUserIsolationTests"/> para
    /// <c>TryEnqueueAiCandidate</c> — rodar o método público inteiro exigiria o runner x86 real) e
    /// (2) o endpoint público <c>POST field-correction</c> ponta-a-ponta contra um
    /// <see cref="IFieldCorrectionStore"/> fake em memória.
    /// </summary>
    public class TransformationExecutionControllerFieldCorrectionTests
    {
        private sealed class FakeCurrentUser : ICurrentUser
        {
            public string? Name { get; set; } = "tester";
            public IReadOnlyList<string> Roles { get; set; } = Array.Empty<string>();
            public bool IsAuthenticated => UserId != null;
            public bool IsInRole(string role) => Roles.Contains(role, StringComparer.OrdinalIgnoreCase);
            public Guid? UserId { get; set; } = Guid.NewGuid();
        }

        /// <summary>Fake em memória — nunca toca em SQL real, cobre os 3 métodos de <see cref="IFieldCorrectionStore"/>.</summary>
        private sealed class FakeFieldCorrectionStore : IFieldCorrectionStore
        {
            public FieldCorrectionContext? SavedContext { get; private set; }
            public bool ThrowOnGetContext { get; set; }
            public List<(FieldCorrectionReportInput Input, Guid ReportedByUserId)> CreatedReports { get; } = new();

            public Task SaveContextAsync(FieldCorrectionContext context, CancellationToken cancellationToken)
            {
                SavedContext = context;
                return Task.CompletedTask;
            }

            public Task<FieldCorrectionContext?> GetContextAsync(string documentId, CancellationToken cancellationToken)
            {
                if (ThrowOnGetContext)
                    throw new InvalidOperationException("IdentityDatabase indisponível (simulado)");

                return Task.FromResult(SavedContext != null && SavedContext.DocumentId == documentId ? SavedContext : null);
            }

            public Task<Guid> CreateReportAsync(FieldCorrectionReportInput input, Guid reportedByUserId, CancellationToken cancellationToken)
            {
                CreatedReports.Add((input, reportedByUserId));
                return Task.FromResult(Guid.NewGuid());
            }
        }

        private static (TransformationExecutionController Controller, FakeFieldCorrectionStore Store, FakeCurrentUser User) BuildController()
        {
            var store = new FakeFieldCorrectionStore();
            var user = new FakeCurrentUser();

            // scopeFactory real (necessário: TryPersistFieldCorrectionContext abre um IServiceScope
            // próprio dentro do Task.Run, mesmo padrão de TryEnqueueAiCandidate) resolvendo o MESMO
            // fake singleton, para o teste poder inspecionar o que foi gravado.
            var services = new ServiceCollection();
            services.AddSingleton<IFieldCorrectionStore>(store);
            var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

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
                aiCandidateService: new NoopAiCandidateService(),
                aiFallbackGate: new NoopAiFallbackSuppressionGate(),
                aiUserInstructionStore: new AiUserInstructionStore(),
                aiUserSessionStore: null!,
                currentUser: user,
                mapperDb: null!,
                layoutParser: null!,
                fieldMappingComposition: null!,
                scopeFactory: scopeFactory,
                canaryAlert: new LayoutParserApi.Services.Security.CanaryAlertService(
                    NullLogger<LayoutParserApi.Services.Security.CanaryAlertService>.Instance),
                fieldCorrectionStore: store);

            return (controller, store, user);
        }

        private sealed class NoopAiCandidateService : IAiTransformationCandidateService
        {
            public Task EnqueueAsync(
                string userId, string ticket, string layoutName, Guid layoutGuid, string mapperGuid,
                string inputContent, string? groundTruthXml, CancellationToken cancellationToken,
                IReadOnlyList<LayoutParserApi.Models.Entities.ParsedField>? parsedFields = null) => Task.CompletedTask;

            public Task<AiCandidateStatus> GetStatusAsync(string userId, string ticket, CancellationToken cancellationToken) =>
                Task.FromResult(new AiCandidateStatus { Status = AiCandidateStatus.StatusNotFound });
        }

        private sealed class NoopAiFallbackSuppressionGate : IAiFallbackSuppressionGate
        {
            public bool IsInCooldown(Guid layoutGuid, out DateTimeOffset retryAt)
            {
                retryAt = default;
                return false;
            }

            public void RegisterFailure(Guid layoutGuid, TimeSpan cooldown) { }
            public void ClearCooldown(Guid layoutGuid) { }
        }

        // --- TryPersistFieldCorrectionContext (best-effort, chamado de dentro de execute-candidates) ---

        [Fact]
        public async Task TryPersistFieldCorrectionContext_grava_gabarito_sysmiddle_quando_existe()
        {
            var (controller, store, _) = BuildController();

            var request = new TransformationRequest { InputContent = "linha-txt", LayoutName = "LAYOUT_X" };
            var layoutGuid = Guid.NewGuid();
            var layoutRecord = new LayoutRecord { LayoutGuid = layoutGuid, Name = request.LayoutName };
            var mapperGuid = Guid.NewGuid().ToString();
            var candidates = new List<TransformationCandidate>
            {
                new TransformationCandidate
                {
                    CandidateId = $"sysmiddle-{mapperGuid}",
                    Pathway = "sysmiddle",
                    TransformedXml = "<xml>gabarito</xml>"
                }
            };
            var documentId = DocumentIdCalculator.Calculate(request.InputContent, layoutGuid.ToString());

            var method = typeof(TransformationExecutionController)
                .GetMethod("TryPersistFieldCorrectionContext", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("Método TryPersistFieldCorrectionContext não encontrado.");

            method.Invoke(controller, new object?[] { request, layoutRecord, candidates, documentId, layoutGuid.ToString() });

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (store.SavedContext == null && DateTime.UtcNow < deadline)
                await Task.Delay(10);

            Assert.NotNull(store.SavedContext);
            Assert.Equal(documentId, store.SavedContext!.DocumentId);
            Assert.Equal(mapperGuid, store.SavedContext.MapperGuid);
            Assert.Equal("<xml>gabarito</xml>", store.SavedContext.GroundTruthXml);
            Assert.Equal(layoutGuid.ToString(), store.SavedContext.LayoutGuid);
            Assert.Equal("LAYOUT_X", store.SavedContext.LayoutName);
        }

        [Fact]
        public async Task TryPersistFieldCorrectionContext_sem_candidato_sysmiddle_grava_sem_gabarito()
        {
            var (controller, store, _) = BuildController();

            var request = new TransformationRequest { InputContent = "<xml/>", LayoutName = "LAYOUT_Y" };
            var layoutGuid = Guid.NewGuid();
            var layoutRecord = new LayoutRecord { LayoutGuid = layoutGuid, Name = request.LayoutName };
            var candidates = new List<TransformationCandidate>(); // entrada XML — sem pathway sysmiddle
            var documentId = DocumentIdCalculator.Calculate(request.InputContent, layoutGuid.ToString());

            var method = typeof(TransformationExecutionController)
                .GetMethod("TryPersistFieldCorrectionContext", BindingFlags.NonPublic | BindingFlags.Instance)!;
            method.Invoke(controller, new object?[] { request, layoutRecord, candidates, documentId, layoutGuid.ToString() });

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (store.SavedContext == null && DateTime.UtcNow < deadline)
                await Task.Delay(10);

            Assert.NotNull(store.SavedContext);
            Assert.Null(store.SavedContext!.MapperGuid);
            Assert.Null(store.SavedContext.GroundTruthXml);
        }

        // --- POST field-correction ---

        [Fact]
        public async Task ReportFieldCorrection_documento_valido_retorna_202_e_grava_pending()
        {
            var (controller, store, user) = BuildController();
            var context = new FieldCorrectionContext(
                "doc_abc123", "mapper-1", "MeuMapper", "layout-guid-1", "LAYOUT_X",
                "<xml>entrada</xml>", "<xml>gabarito</xml>", DateTimeOffset.UtcNow);
            await store.SaveContextAsync(context, CancellationToken.None);

            var request = new FieldCorrectionRequest
            {
                DocumentId = "doc_abc123",
                CandidateId = "tclxsl-1",
                FieldPath = "/nfeProc/NFe/infNFe/ide/nNF",
                ObservedValue = "001",
                ExpectedValue = "1",
                Justification = "Não deveria ter zero à esquerda"
            };

            var result = await controller.ReportFieldCorrection(request, CancellationToken.None);

            var accepted = Assert.IsType<AcceptedResult>(result);
            Assert.Single(store.CreatedReports);
            var (input, reportedBy) = store.CreatedReports[0];
            Assert.Equal("doc_abc123", input.DocumentId);
            Assert.Equal("001", input.ObservedValue);
            Assert.Equal("1", input.ExpectedValue);
            Assert.Equal(user.UserId, reportedBy);
        }

        [Fact]
        public async Task ReportFieldCorrection_documentId_sem_contexto_retorna_404()
        {
            var (controller, _, _) = BuildController();
            var request = new FieldCorrectionRequest
            {
                DocumentId = "doc_inexistente",
                CandidateId = "tclxsl-1",
                FieldPath = "/a/b",
                ObservedValue = "x",
                ExpectedValue = "y"
            };

            var result = await controller.ReportFieldCorrection(request, CancellationToken.None);

            Assert.IsType<NotFoundObjectResult>(result);
        }

        [Fact]
        public async Task ReportFieldCorrection_sem_identidade_resolvida_retorna_404_failclosed()
        {
            var (controller, _, user) = BuildController();
            user.UserId = null; // identidade não resolvida (TrustedIdentityMiddleware não confiou na origem)

            var request = new FieldCorrectionRequest
            {
                DocumentId = "doc_qualquer",
                CandidateId = "tclxsl-1",
                FieldPath = "/a/b",
                ObservedValue = "x",
                ExpectedValue = "y"
            };

            var result = await controller.ReportFieldCorrection(request, CancellationToken.None);

            Assert.IsType<NotFoundResult>(result); // sem corpo — distinto do 404 "contexto expirado" acima
        }

        [Theory]
        [InlineData("", "cand", "/a", "obs", "exp")]
        [InlineData("doc_1", "", "/a", "obs", "exp")]
        [InlineData("doc_1", "cand", "", "obs", "exp")]
        [InlineData("doc_1", "cand", "/a", "", "exp")]
        [InlineData("doc_1", "cand", "/a", "obs", "")]
        public async Task ReportFieldCorrection_campo_obrigatorio_ausente_retorna_400(
            string documentId, string candidateId, string fieldPath, string observedValue, string expectedValue)
        {
            var (controller, _, _) = BuildController();
            var request = new FieldCorrectionRequest
            {
                DocumentId = documentId,
                CandidateId = candidateId,
                FieldPath = fieldPath,
                ObservedValue = observedValue,
                ExpectedValue = expectedValue
            };

            var result = await controller.ReportFieldCorrection(request, CancellationToken.None);

            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        public async Task ReportFieldCorrection_IdentityDatabase_indisponivel_degrada_para_404_sem_lancar()
        {
            var (controller, store, _) = BuildController();
            store.ThrowOnGetContext = true;

            var request = new FieldCorrectionRequest
            {
                DocumentId = "doc_qualquer",
                CandidateId = "tclxsl-1",
                FieldPath = "/a/b",
                ObservedValue = "x",
                ExpectedValue = "y"
            };

            // Não pode lançar — resiliência (dotnet-standards.md): dependência externa indisponível
            // degrada para uma resposta clara, nunca derruba o request.
            var result = await controller.ReportFieldCorrection(request, CancellationToken.None);

            Assert.IsType<NotFoundObjectResult>(result);
        }
    }
}
