namespace LayoutParserApi.Models.Entities.Fiscal
{
    /// <summary>
    /// Suíte de teste versionada do Fiscal Test Lab (issue #423) — um conjunto NOMEADO de fixtures
    /// (pares XML input/gabarito) associado a um <see cref="MappingDraft"/>, executável em bloco
    /// contra uma <see cref="MappingRelease"/> específica. Resolve o gap descrito na issue: hoje
    /// <c>createTestRun</c> só aceita UM par por chamada, sem nenhum agrupamento nem histórico de
    /// execução — cada test-run é avulso, sem comparação de regressão entre versões do mapping.
    /// </summary>
    public class TestSuite
    {
        public Guid SuiteId { get; set; }

        public Guid WorkspaceId { get; set; }

        public Guid DraftId { get; set; }

        public string Name { get; set; } = string.Empty;

        public string? Description { get; set; }

        public Guid CreatedByUserId { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public byte[] RowVersion { get; set; } = Array.Empty<byte>();
    }

    /// <summary>
    /// Uma fixture (par XML input/gabarito) de uma <see cref="TestSuite"/> — mesmo par que
    /// <c>POST .../test-runs</c> já aceita avulso, agora nomeado e agrupado. <see cref="SortOrder"/>
    /// só determina a ordem de exibição/execução; não tem efeito no resultado agregado.
    /// </summary>
    public class TestSuiteFixture
    {
        public Guid FixtureId { get; set; }

        public Guid SuiteId { get; set; }

        public string Name { get; set; } = string.Empty;

        public string InputXml { get; set; } = string.Empty;

        public string ExpectedXml { get; set; } = string.Empty;

        public string? XsdVersion { get; set; }

        public int SortOrder { get; set; }

        public DateTimeOffset CreatedAt { get; set; }
    }

    /// <summary>Resultado de UMA fixture dentro de uma <see cref="TestSuiteRun"/> — projeção mínima de <see cref="MappingTestRunSummary"/>, persistida como parte do JSON agregado da execução.</summary>
    public sealed record TestSuiteFixtureResult(
        Guid FixtureId,
        string FixtureName,
        bool Passed,
        bool XsdValid,
        int DivergenceCount,
        IReadOnlyList<string> XsdErrors,
        IReadOnlyList<MappingTestRunDivergence> Divergences);

    /// <summary>
    /// Registro imutável de UMA execução de <see cref="TestSuite"/> contra UMA <see cref="MappingRelease"/>
    /// específica (issue #423) — o histórico que permite comparar regressão entre versões do mapping
    /// ao longo do tempo. Uma linha por execução; nunca atualizada depois de criada.
    /// </summary>
    public class TestSuiteRun
    {
        public Guid RunId { get; set; }

        public Guid SuiteId { get; set; }

        public Guid ReleaseId { get; set; }

        public Guid ExecutedByUserId { get; set; }

        public DateTimeOffset ExecutedAt { get; set; }

        public int TotalFixtures { get; set; }

        public int Passed { get; set; }

        public int Failed { get; set; }

        /// <summary>Gate agregado: <c>true</c> somente se TODAS as fixtures passaram (diff canônico vazio + XSD válido).</summary>
        public bool RequiredGatesPassed { get; set; }

        public double DurationMs { get; set; }

        /// <summary>JSON de <see cref="TestSuiteFixtureResult"/>[] — resultado por fixture, na mesma ordem executada.</summary>
        public IReadOnlyList<TestSuiteFixtureResult> FixtureResults { get; set; } = Array.Empty<TestSuiteFixtureResult>();
    }
}
