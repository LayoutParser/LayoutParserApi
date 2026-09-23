using System.Text.Json;

using LayoutParserApi.Models.Dtos.Fiscal;
using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Services.Interfaces;

namespace LayoutParserApi.Services.Fiscal
{
    /// <summary>
    /// Adapter de explicação para <c>engine=tcl</c> (Slice 4 — issue #226/#227, design §2.2).
    ///
    /// <para><b>Modo 1 (hoje):</b> não existe ainda TCL real gerado (Slice 5) — opera sobre
    /// <see cref="MappingDraft"/>/<see cref="MappingDraftRule"/> (Slice 3), que já é uma
    /// representação estruturada quase 1:1 com <see cref="ExplainedRule"/>. Não precisa de parser
    /// AST novo — é tradução de campo, não interpretação de código.</para>
    ///
    /// <para><b>Modo 2 (futuro, Slice 5):</b> quando o TCL gerado existir de verdade, este adapter
    /// ganha uma segunda fonte (AST dedicado, não regex) — fora do escopo deste slice.</para>
    /// </summary>
    public sealed class TclExplanationAdapter : IMappingExplanationAdapter
    {
        public string Engine => "tcl";

        private static readonly EngineCapabilities FixedCapabilities =
            new(Execute: false, Explain: true, Author: true, Compile: false, Publish: false);

        private readonly IMappingDraftStore _store;
        private readonly ILogger<TclExplanationAdapter> _logger;

        public TclExplanationAdapter(IMappingDraftStore store, ILogger<TclExplanationAdapter> logger)
        {
            _store = store;
            _logger = logger;
        }

        /// <summary>
        /// Dependência real: <see cref="IMappingDraftStore"/> (SQL). Consulta um Guid inexistente de
        /// propósito — exercita o round-trip real sem depender de haver draft cadastrado. Timeout
        /// curto (issue #90), nunca lança.
        /// </summary>
        public async Task<CapabilityHealth> CheckAvailabilityAsync(CancellationToken cancellationToken)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

            try
            {
                await _store.GetDraftIfMemberAsync(Guid.Empty, Guid.Empty, timeoutCts.Token);
                return new CapabilityHealth(CapabilityStatus.Healthy, "MappingDraftStore respondeu.");
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return new CapabilityHealth(CapabilityStatus.Unavailable, "MappingDraftStore não respondeu dentro do timeout (3s).");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Gate de capacidade (#90): MappingDraftStore indisponível para engine=tcl.");
                return new CapabilityHealth(CapabilityStatus.Unavailable, $"MappingDraftStore falhou: {ex.Message}");
            }
        }

        public async Task<MappingExplanation?> ExplainAsync(MappingExplanationRequest request, CancellationToken cancellationToken)
        {
            if (!Guid.TryParse(request.MappingId, out var draftId))
                return null;

            if (!string.Equals(request.Version, "draft", StringComparison.OrdinalIgnoreCase))
                return null;

            MappingDraftDetail? draft;
            try
            {
                draft = await _store.GetDraftIfMemberAsync(draftId, request.UserId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao consultar draft {DraftId} para explicação TCL.", draftId);
                throw;
            }

            if (draft == null || draft.WorkspaceId != request.WorkspaceId || !string.Equals(draft.Engine, "tcl", StringComparison.OrdinalIgnoreCase))
                return null;

            var rules = draft.Rules.Select(ToExplainedRule).ToList();
            var opaqueCount = rules.Count(r => r.SupportLevel == MappingExplanationSupportLevel.Opaque);

            return new MappingExplanation(
                MappingId: request.MappingId,
                Version: "draft",
                Engine: "tcl",
                Capabilities: FixedCapabilities,
                SourceSchema: null,
                TargetSchema: null,
                Rules: rules,
                Description: null,
                Limitations: new[]
                {
                    "Draft ainda não compilado — regras refletem a proposta em revisão humana, não código executável (Slice 5 introduz a compilação real).",
                },
                OpaqueRuleCount: opaqueCount);
        }

        /// <summary>
        /// Tradução quase-direta: <see cref="MappingDraftRule"/> já tem sourceRefs/targetRefs/
        /// operation/condition/evidence — o <c>SupportLevel</c> é o único campo que exige lógica,
        /// derivado do <c>Status</c> humano (spec §8/design §1: nunca "authoritative" antes de
        /// revisão humana).
        /// </summary>
        private static ExplainedRule ToExplainedRule(MappingDraftRuleDetail rule)
        {
            var supportLevel = rule.Status switch
            {
                MappingDraftRuleStatus.Accepted or MappingDraftRuleStatus.Edited or MappingDraftRuleStatus.Validated
                    => MappingExplanationSupportLevel.Authoritative,
                MappingDraftRuleStatus.Proposed => MappingExplanationSupportLevel.BestEffort,
                MappingDraftRuleStatus.NeedsInput => MappingExplanationSupportLevel.Opaque,
                _ => MappingExplanationSupportLevel.Unsupported, // rejected/superseded
            };

            var condition = ExtractFirstCondition(rule.ConditionsJson);
            var evidence = rule.Evidence.Select(e => new EvidenceRef(e.Kind, e.Reference)).ToList();

            return new ExplainedRule(
                RuleId: rule.RuleId.ToString(),
                SourceRefs: rule.SourceRefs,
                TargetRefs: rule.TargetRefs,
                Condition: condition,
                Operations: new[] { rule.Operation },
                Cardinality: rule.Cardinality,
                Evidence: evidence,
                HumanDescription: DescribeRule(rule, condition),
                TechnicalDetail: Truncate(rule.TransformationsJson),
                SupportLevel: supportLevel);
        }

        private static string DescribeRule(MappingDraftRuleDetail rule, string? condition)
        {
            var sources = rule.SourceRefs.Count == 0 ? "nenhuma origem declarada" : string.Join(", ", rule.SourceRefs);
            var targets = rule.TargetRefs.Count == 0 ? "nenhum destino declarado" : string.Join(", ", rule.TargetRefs);
            var basis = $"Operação \"{rule.Operation}\" de {sources} para {targets} (status: {rule.Status}, confiança: {rule.Confidence}).";
            return condition is null ? basis : $"Quando {condition}: {basis}";
        }

        /// <summary>Extrai a primeira condição legível do JSON estruturado, sem interpretar operação — só apresentação.</summary>
        private static string? ExtractFirstCondition(string conditionsJson)
        {
            if (string.IsNullOrWhiteSpace(conditionsJson) || conditionsJson.Trim() == "[]")
                return null;

            try
            {
                using var doc = JsonDocument.Parse(conditionsJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                    return Truncate(doc.RootElement[0].ToString());
            }
            catch (JsonException)
            {
                // JSON malformado: não inventa condição — degrada para null, não lança.
            }

            return null;
        }

        private static string? Truncate(string? content)
        {
            if (string.IsNullOrWhiteSpace(content)) return null;
            const int max = 400;
            return content.Length <= max ? content : content[..max] + "…";
        }
    }
}
