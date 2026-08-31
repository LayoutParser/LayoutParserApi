using System.Xml.Linq;

using LayoutParserApi.Models.Dtos.Fiscal;
using LayoutParserApi.Models.Entities;
using LayoutParserApi.Services.Interfaces;

using XslSynth.Core;

using MapperRule = XslSynth.Model.MapperRule;
using MapperVo = XslSynth.Model.MapperVo;
using LinkMappingItem = XslSynth.Model.LinkMappingItem;

namespace LayoutParserApi.Services.Fiscal
{
    /// <summary>
    /// Adapter de explicação para mappers Sysmiddle REAIS já publicados (catálogo <c>tbMapper</c>,
    /// Slice 4 — issue #226/#227, design §2.1). Read-only por natureza: nunca gera código, só
    /// TRADUZ o que já está em produção para o contrato canônico <see cref="MappingExplanation"/>.
    ///
    /// <para><b>Fonte:</b> reaproveita <see cref="RealMapperParser"/> (MapperVO real, gramática
    /// decifrada em <c>decisao-dsl-mapper-sysmiddle-2026-08-21.md</c>) + <see cref="DslStructuredParser"/>
    /// (Camada 0 do desenho RAG — já é literalmente um "explicador" da DSL: produz árvore de
    /// decisão com origem/destino/condição/funções por ramo). Não escrevemos um parser de
    /// explicação novo: <see cref="DslStructuredParser"/> já cobre os 3 condicionais + operador
    /// <c>=</c>/<c>!=</c> descritos na decisão de 2026-08-21.</para>
    ///
    /// <para><b>Capabilities.Author é SEMPRE false, hard-coded</b> — garantia central do produto
    /// (spec §4: "Sysmiddle só executa/explica, nunca autoria"). Não vem de config, não é
    /// parametrizável por payload.</para>
    /// </summary>
    public sealed class SysmiddleExplanationAdapter : IMappingExplanationAdapter
    {
        public string Engine => "sysmiddle";

        /// <summary>
        /// Catálogo FECHADO de funções conhecidas do dispatcher <c>RuleInterpretor.ExecuteRuleFunction</c>
        /// (decisão 2026-08-21 §1 — 4 funções confirmadas). Qualquer função fora deste conjunto
        /// existe (é reconhecida como chamada), mas não tem semântica traduzível aqui → <c>opaque</c>.
        /// </summary>
        private static readonly HashSet<string> KnownFunctions = new(StringComparer.OrdinalIgnoreCase)
        {
            "GetLength",
            "GetValueFromContext",
            "GetDictionaryValuesFromElement",
            "GetSumElementValuesFunction",
        };

        private static readonly EngineCapabilities FixedCapabilities =
            new(Execute: true, Explain: true, Author: false, Compile: false, Publish: false);

        private readonly ICachedMapperService _cachedMapperService;
        private readonly ILogger<SysmiddleExplanationAdapter> _logger;

        public SysmiddleExplanationAdapter(ICachedMapperService cachedMapperService, ILogger<SysmiddleExplanationAdapter> logger)
        {
            _cachedMapperService = cachedMapperService;
            _logger = logger;
        }

        public async Task<MappingExplanation?> ExplainAsync(MappingExplanationRequest request, CancellationToken cancellationToken)
        {
            // Sysmiddle não tem versionamento explícito — só "current" é aceito (design §0).
            if (!string.Equals(request.Version, "current", StringComparison.OrdinalIgnoreCase))
                return null;

            List<Mapper> mappers;
            try
            {
                mappers = await _cachedMapperService.GetAllMappersAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao consultar catálogo de mappers Sysmiddle para explicação de {MapperGuid}.", request.MappingId);
                throw;
            }

            var mapper = mappers.FirstOrDefault(m => string.Equals(m.MapperGuid, request.MappingId, StringComparison.OrdinalIgnoreCase));
            if (mapper == null || string.IsNullOrWhiteSpace(mapper.DecryptedContent))
                return null;

            MapperVo mapperVo;
            try
            {
                mapperVo = new RealMapperParser().Parse(XDocument.Parse(mapper.DecryptedContent));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MapperVO {MapperGuid} não pôde ser parseado — explicação degrada para 0 regras.", request.MappingId);
                return BuildExplanation(request, mapper, rules: Array.Empty<ExplainedRule>(),
                    limitations: new[] { "MapperVO não pôde ser parseado — verifique o conteúdo descriptografado." });
            }

            var rules = new List<ExplainedRule>();
            rules.AddRange(mapperVo.LinkMappings.Select(ToExplainedRule));
            rules.AddRange(mapperVo.Rules.SelectMany(ToExplainedRules));

            return BuildExplanation(request, mapper, rules, limitations: Array.Empty<string>());
        }

        private static MappingExplanation BuildExplanation(
            MappingExplanationRequest request, Mapper mapper, IReadOnlyList<ExplainedRule> rules, IReadOnlyList<string> limitations)
        {
            var opaqueCount = rules.Count(r => r.SupportLevel == MappingExplanationSupportLevel.Opaque);

            return new MappingExplanation(
                MappingId: request.MappingId,
                Version: "current",
                Engine: "sysmiddle",
                Capabilities: FixedCapabilities,
                SourceSchema: new SchemaRef(mapper.InputLayoutGuidFromXml ?? mapper.InputLayoutGuid, mapper.Name),
                TargetSchema: new SchemaRef(mapper.TargetLayoutGuidFromXml ?? mapper.TargetLayoutGuid, mapper.Description),
                Rules: rules,
                Description: mapper.Description,
                Limitations: limitations,
                OpaqueRuleCount: opaqueCount);
        }

        /// <summary>Mapeamento direto campo→campo (sem DSL) — sempre <c>authoritative</c>, é dado de vinculação puro.</summary>
        private static ExplainedRule ToExplainedRule(LinkMappingItem link)
        {
            var ruleId = link.ElementGuid ?? $"link:{link.Name}";
            var target = link.TargetLeafName ?? link.TargetGuid ?? "?";
            var source = link.InputGuid ?? link.Name ?? "?";

            return new ExplainedRule(
                RuleId: ruleId,
                SourceRefs: new[] { source },
                TargetRefs: new[] { target },
                Condition: null,
                Operations: new[] { "copy" },
                Cardinality: "1:1",
                Evidence: new[] { new EvidenceRef("sysmiddle-link-mapping", link.Name ?? ruleId) },
                HumanDescription: $"Copia o valor de \"{source}\" diretamente para \"{target}\".",
                TechnicalDetail: null,
                SupportLevel: MappingExplanationSupportLevel.Authoritative);
        }

        /// <summary>
        /// Uma <see cref="MapperRule"/> (DSL) pode gerar múltiplos <see cref="ExplainedRule"/> — um
        /// por ramo da árvore de decisão (<see cref="StructuredBranch"/>), já que cada ramo tem
        /// condição/origem/destino próprios.
        /// </summary>
        private static IEnumerable<ExplainedRule> ToExplainedRules(MapperRule rule)
        {
            XslSynth.Prompting.StructuredRule? structured;
            try
            {
                structured = new DslStructuredParser().Parse(rule);
            }
            catch (Exception)
            {
                structured = null;
            }

            if (structured == null)
            {
                // DSL fora do subconjunto reconhecido — nunca inventa, marca como opaque inteira.
                yield return OpaqueRuleFallback(rule);
                yield break;
            }

            if (structured.Branches.Count == 0)
            {
                yield return OpaqueRuleFallback(rule);
                yield break;
            }

            for (var i = 0; i < structured.Branches.Count; i++)
            {
                var branch = structured.Branches[i];
                var ruleId = (rule.ElementGuid ?? rule.Name ?? "rule") + $":{i}";
                var unknownFunctions = branch.Functions.Where(f => !KnownFunctions.Contains(f)).ToList();
                var supportLevel = unknownFunctions.Count > 0
                    ? MappingExplanationSupportLevel.Opaque
                    : MappingExplanationSupportLevel.Authoritative;

                var condition = branch.Condition == "true" ? null : branch.Condition;
                var operations = branch.Functions.Count > 0 ? branch.Functions : new List<string> { "assign" };

                yield return new ExplainedRule(
                    RuleId: ruleId,
                    SourceRefs: branch.Sources.Select(s => $"I.{s}").ToList(),
                    TargetRefs: new[] { $"T.{branch.Target}" },
                    Condition: condition,
                    Operations: operations,
                    Cardinality: structured.LoopType is null ? "1:1" : "1:N",
                    Evidence: new[] { new EvidenceRef("sysmiddle-rule", rule.Name ?? ruleId) },
                    HumanDescription: DescribeBranch(rule, branch, condition),
                    TechnicalDetail: Truncate(rule.ContentValue),
                    SupportLevel: supportLevel);
            }
        }

        private static ExplainedRule OpaqueRuleFallback(MapperRule rule)
        {
            var ruleId = rule.ElementGuid ?? rule.Name ?? Guid.NewGuid().ToString();
            return new ExplainedRule(
                RuleId: ruleId,
                SourceRefs: Array.Empty<string>(),
                TargetRefs: rule.TargetPath is null ? Array.Empty<string>() : new[] { $"T.{rule.TargetPath}" },
                Condition: null,
                Operations: Array.Empty<string>(),
                Cardinality: "1:1",
                Evidence: new[] { new EvidenceRef("sysmiddle-rule", rule.Name ?? ruleId) },
                HumanDescription: "Regra reconhecida no mapper, mas fora da gramática DSL suportada por este explicador.",
                TechnicalDetail: Truncate(rule.ContentValue),
                SupportLevel: MappingExplanationSupportLevel.Opaque);
        }

        private static string DescribeBranch(MapperRule rule, XslSynth.Prompting.StructuredBranch branch, string? condition)
        {
            var sourcesText = branch.Sources.Count == 0 ? "um valor calculado" : string.Join(", ", branch.Sources);
            var basis = condition is null
                ? $"Preenche \"{branch.Target}\" a partir de {sourcesText}."
                : $"Quando {condition}, preenche \"{branch.Target}\" a partir de {sourcesText}.";
            return branch.Functions.Count == 0
                ? basis
                : basis + $" Usa a(s) função(ões): {string.Join(", ", branch.Functions)}.";
        }

        /// <summary>Trecho técnico truncado — nunca payload fiscal real, só a DSL/configuração da regra.</summary>
        private static string? Truncate(string? content)
        {
            if (string.IsNullOrWhiteSpace(content)) return null;
            const int max = 400;
            return content.Length <= max ? content : content[..max] + "…";
        }
    }
}
