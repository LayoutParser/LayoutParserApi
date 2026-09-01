namespace LayoutParserApi.Services.Interfaces
{
    /// <summary>Status observável de um job de test-run (Slice 5 — issue #231, mesmo padrão de <c>SuggestionJobStatus</c>/<c>CompileJobStatus</c>).</summary>
    public static class TestRunJobStatus
    {
        public const string Queued = "queued";
        public const string Running = "running";
        public const string Completed = "completed";
        public const string Failed = "failed";
    }

    public sealed class TestRunJobState
    {
        public Guid JobId { get; set; }
        public string Status { get; set; } = TestRunJobStatus.Queued;
        public Guid? ReleaseId { get; set; }
        public bool? RequiredGatesPassed { get; set; }
        public string? Error { get; set; }
        public double? DurationMs { get; set; }
    }

    /// <summary>
    /// Executa o Fiscal Test Lab: aplica o artefato XSLT compilado sobre um XML de entrada e compara
    /// contra um gabarito via <see cref="XslSynth.Core.CanonicalDiffer"/> + validação XSD via
    /// <see cref="LayoutParserApi.Services.XmlAnalysis.XsdValidationService"/> (Slice 5 — issue #231).
    /// Fire-and-forget via <see cref="Microsoft.Extensions.DependencyInjection.IServiceScopeFactory"/>,
    /// mesmo padrão de <see cref="IMappingCompileService"/>.
    /// </summary>
    public interface IMappingTestRunService
    {
        Task<Guid> EnqueueAsync(
            Guid workspaceId,
            Guid draftId,
            Guid releaseId,
            Guid userId,
            string inputXml,
            string expectedXml,
            string? xsdVersion,
            string correlationId,
            CancellationToken cancellationToken);

        Task<TestRunJobState?> GetStatusAsync(Guid jobId, CancellationToken cancellationToken);
    }
}
