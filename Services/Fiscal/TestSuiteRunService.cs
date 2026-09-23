using System.Diagnostics;

using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Services.Interfaces;

namespace LayoutParserApi.Services.Fiscal
{
    /// <summary>
    /// Executa TODAS as fixtures de uma <see cref="TestSuite"/> contra uma <see cref="MappingRelease"/>
    /// específica (issue #423), reaproveitando <see cref="IMappingTestRunService.EvaluateFixtureAsync"/>
    /// fixture-a-fixture — mesmo diff canônico/validação XSD/provenance do Fiscal Test Lab avulso, sem
    /// duplicar lógica.
    /// </summary>
    /// <remarks>
    /// Simplificação deliberada deste corte (issue #423, documentada no PR): a execução é SÍNCRONA —
    /// diferente de <c>POST .../test-runs</c> (que é fire-and-forget com job pollável), aqui o
    /// <c>POST .../test-suites/{suiteId}/run</c> devolve o resultado agregado diretamente no 200. Cada
    /// fixture já é rápida (diff canônico + XSD, sem I/O externo/Ollama) e o volume esperado de fixtures
    /// por suíte é pequeno — não há job/polling equivalente a <c>TestRunJobState</c> aqui. Se o volume de
    /// fixtures crescer a ponto de tornar isso lento, migrar para o mesmo padrão fire-and-forget é a
    /// evolução natural, sem mudar o contrato de <see cref="ITestSuiteStore.RecordRunAsync"/>.
    /// </remarks>
    public interface ITestSuiteRunService
    {
        /// <summary>
        /// Lança <see cref="InvalidOperationException"/> se a suíte/release não pertencerem ao mesmo
        /// workspace/draft, ou se a suíte não tiver nenhuma fixture cadastrada.
        /// </summary>
        Task<TestSuiteRunDetail> RunSuiteAsync(
            Guid workspaceId, Guid draftId, Guid suiteId, Guid releaseId, Guid userId, string correlationId, CancellationToken cancellationToken);
    }

    public sealed class TestSuiteRunService : ITestSuiteRunService
    {
        private readonly ITestSuiteStore _suiteStore;
        private readonly IMappingDraftStore _draftStore;
        private readonly IMappingReleaseStore _releaseStore;
        private readonly IMappingTestRunService _testRunService;
        private readonly ILogger<TestSuiteRunService> _logger;

        public TestSuiteRunService(
            ITestSuiteStore suiteStore,
            IMappingDraftStore draftStore,
            IMappingReleaseStore releaseStore,
            IMappingTestRunService testRunService,
            ILogger<TestSuiteRunService> logger)
        {
            _suiteStore = suiteStore;
            _draftStore = draftStore;
            _releaseStore = releaseStore;
            _testRunService = testRunService;
            _logger = logger;
        }

        public async Task<TestSuiteRunDetail> RunSuiteAsync(
            Guid workspaceId, Guid draftId, Guid suiteId, Guid releaseId, Guid userId, string correlationId, CancellationToken cancellationToken)
        {
            var suite = await _suiteStore.GetSuiteIfMemberAsync(suiteId, userId, cancellationToken);
            if (suite == null || suite.WorkspaceId != workspaceId || suite.DraftId != draftId)
                throw new InvalidOperationException("Suíte não encontrada, ou fora do workspace/draft informados.");

            var draft = await _draftStore.GetDraftIfMemberAsync(draftId, userId, cancellationToken);
            if (draft == null || draft.WorkspaceId != workspaceId)
                throw new InvalidOperationException("Draft não encontrado.");

            var release = await _releaseStore.GetReleaseIfMemberAsync(releaseId, userId, cancellationToken);
            if (release == null || release.WorkspaceId != workspaceId || release.DraftId != draftId)
                throw new InvalidOperationException("Release não encontrada, não compilada ou fora do workspace/draft desta suíte.");

            var fixtures = await _suiteStore.ListFixturesAsync(suiteId, cancellationToken);
            if (fixtures.Count == 0)
                throw new InvalidOperationException("Suíte sem fixtures — adicione ao menos uma antes de rodar.");

            var stopwatch = Stopwatch.StartNew();
            var results = new List<TestSuiteFixtureResult>(fixtures.Count);

            foreach (var fixture in fixtures)
            {
                MappingTestRunSummary summary;
                try
                {
                    summary = await _testRunService.EvaluateFixtureAsync(
                        release, draft, fixture.InputXml, fixture.ExpectedXml, fixture.XsdVersion, cancellationToken);
                }
                catch (Exception ex)
                {
                    // Degrada por fixture (dotnet-standards.md §Resiliência): uma fixture com falha
                    // inesperada não derruba a suíte inteira — vira "falhou", as demais continuam.
                    _logger.LogWarning(ex, "Falha inesperada ao avaliar a fixture {FixtureId} ({FixtureName}) da suíte {SuiteId}.", fixture.FixtureId, fixture.Name, suiteId);
                    results.Add(new TestSuiteFixtureResult(
                        fixture.FixtureId, fixture.Name, Passed: false, XsdValid: false, DivergenceCount: 0,
                        new[] { $"Falha interna ao avaliar a fixture: {ex.Message}" }, Array.Empty<MappingTestRunDivergence>()));
                    continue;
                }

                results.Add(new TestSuiteFixtureResult(
                    fixture.FixtureId, fixture.Name, summary.RequiredGatesPassed, summary.XsdValid,
                    summary.Divergences.Count, summary.XsdErrors, summary.Divergences));
            }

            stopwatch.Stop();

            var run = await _suiteStore.RecordRunAsync(suiteId, releaseId, userId, results, stopwatch.Elapsed.TotalMilliseconds, cancellationToken);

            _logger.LogInformation(
                "Suíte {SuiteId} executada contra release {ReleaseId}: {Passed}/{Total} fixtures passaram (requiredGatesPassed={RequiredGatesPassed}, correlationId={CorrelationId}).",
                suiteId, releaseId, run.Passed, run.TotalFixtures, run.RequiredGatesPassed, correlationId);

            return run;
        }
    }
}
