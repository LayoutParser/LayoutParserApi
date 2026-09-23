using LayoutParserApi.Models.Entities.Fiscal;

namespace LayoutParserApi.Services.Interfaces
{
    /// <summary>Suíte pronta para resposta HTTP — inclui o ETag (base64 do ROWVERSION), mesmo padrão de <see cref="MappingReleaseDetail"/>.</summary>
    public sealed record TestSuiteDetail(
        Guid SuiteId,
        Guid WorkspaceId,
        Guid DraftId,
        string Name,
        string? Description,
        Guid CreatedByUserId,
        DateTimeOffset CreatedAt,
        string ETag);

    /// <summary>Uma fixture da suíte — retornada em <c>GET .../test-suites/{suiteId}/fixtures</c>.</summary>
    public sealed record TestSuiteFixtureDetail(
        Guid FixtureId,
        Guid SuiteId,
        string Name,
        string InputXml,
        string ExpectedXml,
        string? XsdVersion,
        int SortOrder,
        DateTimeOffset CreatedAt);

    /// <summary>Uma execução de suíte já persistida — retornada em <c>GET .../test-suites/{suiteId}/runs</c> (histórico).</summary>
    public sealed record TestSuiteRunDetail(
        Guid RunId,
        Guid SuiteId,
        Guid ReleaseId,
        Guid ExecutedByUserId,
        DateTimeOffset ExecutedAt,
        int TotalFixtures,
        int Passed,
        int Failed,
        bool RequiredGatesPassed,
        double DurationMs,
        IReadOnlyList<TestSuiteFixtureResult> FixtureResults);

    /// <summary>
    /// Acesso a dado de <see cref="TestSuite"/>/<see cref="TestSuiteFixture"/>/<see cref="TestSuiteRun"/>
    /// (issue #423). Mesmo padrão ADO.NET cru dos demais stores fiscais (<c>IdentityDatabase:*</c>).
    /// </summary>
    public interface ITestSuiteStore
    {
        Task<TestSuiteDetail> CreateSuiteAsync(
            Guid workspaceId, Guid draftId, string name, string? description, Guid createdByUserId, CancellationToken cancellationToken);

        Task<TestSuiteDetail?> GetSuiteIfMemberAsync(Guid suiteId, Guid userId, CancellationToken cancellationToken);

        Task<IReadOnlyList<TestSuiteDetail>> ListByDraftAsync(Guid draftId, CancellationToken cancellationToken);

        Task<TestSuiteFixtureDetail> AddFixtureAsync(
            Guid suiteId, string name, string inputXml, string expectedXml, string? xsdVersion, CancellationToken cancellationToken);

        Task<IReadOnlyList<TestSuiteFixtureDetail>> ListFixturesAsync(Guid suiteId, CancellationToken cancellationToken);

        Task<TestSuiteRunDetail> RecordRunAsync(
            Guid suiteId, Guid releaseId, Guid executedByUserId,
            IReadOnlyList<TestSuiteFixtureResult> fixtureResults, double durationMs, CancellationToken cancellationToken);

        /// <summary>Histórico paginado, mais recente primeiro — o que permite a comparação de regressão ao longo do tempo pedida pela issue #423.</summary>
        Task<(IReadOnlyList<TestSuiteRunDetail> Items, int TotalCount)> ListRunsAsync(
            Guid suiteId, int page, int pageSize, CancellationToken cancellationToken);
    }
}
