namespace LayoutParserApi.Services.Interfaces
{
    /// <summary>Status observável de um job de sugestão (Slice 3 — issue #230, spec §8: "jobs de IA são assíncronos, idempotentes, canceláveis e observáveis").</summary>
    public static class SuggestionJobStatus
    {
        public const string Queued = "queued";
        public const string Running = "running";
        public const string Completed = "completed";
        public const string Failed = "failed";
        public const string Canceled = "canceled";
    }

    public sealed class SuggestionJobState
    {
        public Guid JobId { get; set; }
        public string Status { get; set; } = SuggestionJobStatus.Queued;
        public int RulesCreated { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    /// Orquestra o job de sugestão de regras via IA (Slice 3 — issue #230). Upstream do
    /// <c>RepairOrchestrator</c>: lê artefatos (spec/xsd/sample) da revisão e propõe
    /// <c>MappingDraftRule</c> candidatas — nunca código executável. Fire-and-forget via
    /// <see cref="Microsoft.Extensions.DependencyInjection.IServiceScopeFactory"/>, mesmo padrão de
    /// <c>AiTransformationCandidateService</c>.
    /// </summary>
    public interface IMappingSuggestionService
    {
        /// <summary>
        /// Enfileira o job. Idempotente por (draftId, hash dos artefatos da revisão): reenviar não
        /// duplica um job já em execução para o mesmo conteúdo-fonte — devolve o job existente.
        /// </summary>
        Task<Guid> EnqueueAsync(Guid draftId, Guid workspaceId, Guid revisionId, string engine, CancellationToken cancellationToken);

        Task<SuggestionJobState?> GetStatusAsync(Guid jobId, CancellationToken cancellationToken);

        /// <summary>Cancelamento cooperativo — o loop observa o token e para no próximo ponto de checagem.</summary>
        Task<bool> CancelAsync(Guid jobId, CancellationToken cancellationToken);
    }
}
