namespace LayoutParserApi.Services.Interfaces
{
    /// <summary>Status observável de um job de compilação (Slice 5 — issue #231, mesmo padrão de <c>SuggestionJobStatus</c>).</summary>
    public static class CompileJobStatus
    {
        public const string Queued = "queued";
        public const string Running = "running";
        public const string Completed = "completed";
        public const string Failed = "failed";
    }

    public sealed class CompileJobState
    {
        public Guid JobId { get; set; }
        public string Status { get; set; } = CompileJobStatus.Queued;
        public Guid? ReleaseId { get; set; }
        public string? Error { get; set; }
        public double? DurationMs { get; set; }
    }

    /// <summary>
    /// Orquestra o job de compilação determinística <c>MappingDraftRule[] → XSLT/TCL</c> (Slice 5 —
    /// issue #231), via <see cref="MappingDraftRuleTranspiler" />. Fire-and-forget via
    /// <see cref="Microsoft.Extensions.DependencyInjection.IServiceScopeFactory" />, mesmo padrão de
    /// <c>IMappingSuggestionService</c>.
    /// </summary>
    public interface IMappingCompileService
    {
        /// <summary>Enfileira o job. Idempotente por (draftId, hash do snapshot de regras accepted/edited).</summary>
        Task<Guid> EnqueueAsync(Guid workspaceId, Guid draftId, Guid userId, string correlationId, CancellationToken cancellationToken);

        Task<CompileJobState?> GetStatusAsync(Guid jobId, CancellationToken cancellationToken);
    }
}
