using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Services.Interfaces;

namespace LayoutParserApi.Services.Fiscal
{
    /// <summary>
    /// Implementação de <see cref="IMappingCompileService"/> — Slice 5 (issue #231). Transpilação é
    /// determinística (<see cref="MappingDraftRuleTranspiler"/>, sem I/O externo/Ollama) — o job
    /// fire-and-forget existe pela observabilidade/consistência de padrão com o Slice 3
    /// (<c>IMappingSuggestionService</c>), não porque a compilação em si seja lenta.
    /// </summary>
    public sealed class MappingCompileService : IMappingCompileService
    {
        private readonly ILogger<MappingCompileService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;

        // Estado do job em memória — observável via GetStatusAsync, mesmo padrão de MappingSuggestionService.
        private static readonly ConcurrentDictionary<Guid, CompileJobState> Jobs = new();

        public MappingCompileService(ILogger<MappingCompileService> logger, IServiceScopeFactory scopeFactory)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
        }

        public async Task<Guid> EnqueueAsync(Guid workspaceId, Guid draftId, Guid userId, string correlationId, CancellationToken cancellationToken)
        {
            MappingDraftDetail? draft;
            using (var scope = _scopeFactory.CreateScope())
            {
                var draftStore = scope.ServiceProvider.GetRequiredService<IMappingDraftStore>();
                draft = await draftStore.GetDraftIfMemberAsync(draftId, userId, cancellationToken);
            }

            if (draft == null || draft.WorkspaceId != workspaceId)
                throw new InvalidOperationException("Draft não encontrado ou fora do workspace.");

            var processableRules = draft.Rules
                .Where(r => r.Status is MappingDraftRuleStatus.Accepted or MappingDraftRuleStatus.Edited)
                .OrderBy(r => r.RuleId)
                .ToList();

            var rulesSnapshotHash = ComputeRulesSnapshotHash(processableRules);

            var jobId = Guid.NewGuid();
            var state = new CompileJobState { JobId = jobId, Status = CompileJobStatus.Queued };
            Jobs[jobId] = state;

            // ✅ Fire-and-forget real (dotnet-standards.md §Background work): nunca propaga exceção
            // para o chamador do POST .../compile, que já retornou 202 antes deste ponto.
            _ = Task.Run(async () =>
            {
                state.Status = CompileJobStatus.Running;
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    var rules = processableRules.Select(ToEntity).ToList();
                    var sourceSchema = new SchemaRef("origem");
                    // Nome de elemento XML válido — GUID puro pode começar com dígito (inválido como
                    // NCName), por isso o prefixo "root" em vez de usar draft.PackageId cru.
                    var targetSchema = new SchemaRef($"root{draft.PackageId:N}");

                    var result = draft.Engine.Equals("tcl", StringComparison.OrdinalIgnoreCase)
                        ? MappingDraftRuleTranspiler.ToTcl(rules, sourceSchema, targetSchema)
                        : MappingDraftRuleTranspiler.ToXslt(rules, sourceSchema, targetSchema);

                    var artifactKind = draft.Engine.Equals("tcl", StringComparison.OrdinalIgnoreCase) ? "tcl" : "xslt";
                    var artifact = new MappingReleaseArtifact(
                        artifactKind, result.Content, ComputeContentHash(result.Content), DateTimeOffset.UtcNow);

                    var diagnostics = result.Diagnostics
                        .Select(d => new MappingReleaseCompileDiagnostic(d.RuleId, d.Severity, d.Message))
                        .ToList();

                    using var scope = _scopeFactory.CreateScope();
                    var releaseStore = scope.ServiceProvider.GetRequiredService<IMappingReleaseStore>();
                    var release = await releaseStore.CreateOrGetCompiledReleaseAsync(
                        workspaceId, draftId, draft.Engine, rulesSnapshotHash,
                        processableRules.Select(r => r.RuleId).ToList(),
                        new[] { artifact }, diagnostics, correlationId, jobId, cancellationToken);

                    state.ReleaseId = release.ReleaseId;
                    state.Status = CompileJobStatus.Completed;
                    _logger.LogInformation(
                        "Compilação concluída (draft={DraftId}, release={ReleaseId}, diagnósticos={DiagnosticsCount}, correlationId={CorrelationId}).",
                        draftId, release.ReleaseId, diagnostics.Count, correlationId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Falha não tratada no job de compilação {JobId} (draft={DraftId}, correlationId={CorrelationId}).", jobId, draftId, correlationId);
                    state.Status = CompileJobStatus.Failed;
                    state.Error = "Falha interna ao compilar o draft";
                }
                finally
                {
                    stopwatch.Stop();
                    state.DurationMs = stopwatch.Elapsed.TotalMilliseconds;
                }
            }, CancellationToken.None);

            return jobId;
        }

        public Task<CompileJobState?> GetStatusAsync(Guid jobId, CancellationToken cancellationToken)
            => Task.FromResult(Jobs.TryGetValue(jobId, out var state) ? state : null);

        private static MappingDraftRule ToEntity(MappingDraftRuleDetail detail) => new()
        {
            RuleId = detail.RuleId,
            DraftId = detail.DraftId,
            SourceRefs = detail.SourceRefs,
            TargetRefs = detail.TargetRefs,
            Operation = detail.Operation,
            ConditionsJson = detail.ConditionsJson,
            TransformationsJson = detail.TransformationsJson,
            Cardinality = detail.Cardinality,
            Evidence = detail.Evidence,
            Confidence = detail.Confidence,
            Status = detail.Status,
            OpenQuestions = detail.OpenQuestions,
            CreatedAt = detail.CreatedAt,
        };

        private static string ComputeRulesSnapshotHash(IReadOnlyList<MappingDraftRuleDetail> rules)
        {
            // Hash do conjunto (RuleId + ETag) — reflete tanto "quais regras" quanto "última edição
            // de cada uma"; reenviar após editar uma regra gera um snapshot diferente (não idempotente
            // com o compilado anterior, corretamente).
            var joined = string.Join('|', rules.Select(r => $"{r.RuleId}:{r.ETag}").OrderBy(s => s));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined))).ToLowerInvariant();
        }

        private static string ComputeContentHash(string content)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }
}
