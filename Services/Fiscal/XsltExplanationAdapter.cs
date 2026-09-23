using System.Xml.Linq;

using LayoutParserApi.Models.Dtos.Fiscal;
using LayoutParserApi.Services.Interfaces;

namespace LayoutParserApi.Services.Fiscal
{
    /// <summary>
    /// Adapter de explicação para <c>engine=xslt</c> (Slice 4 — issue #226/#227, design §2.3).
    ///
    /// <para><b>Hoje:</b> não existe <c>MappingRelease</c>/artefato XSLT compilado associado a um
    /// Draft (Slice 5 introduz a compilação). <see cref="ExplainAsync"/> portanto SEMPRE retorna
    /// <c>supportLevel=unsupported</c> com <c>limitations</c> explicando o motivo — nunca inventa
    /// uma explicação de código que não existe.</para>
    ///
    /// <para><b>Parser real (100% novo, testável isoladamente):</b> <see cref="ExplainXsltDocument"/>
    /// navega a árvore XSLT via <c>System.Xml.Linq</c> (XSLT é XML válido) — cobre
    /// <c>xsl:template</c>/<c>xsl:value-of</c>/<c>xsl:for-each</c>/<c>xsl:if</c>/<c>xsl:choose</c>/
    /// <c>xsl:when</c>/<c>xsl:variable</c>. Extensões fora dessa lista fechada (outro elemento no
    /// namespace <c>xsl:</c>, ou qualquer elemento de OUTRO namespace tipo <c>msxsl:</c>) viram
    /// <c>opaque</c> — reconhecidas como "existem", sem semântica traduzível.</para>
    /// </summary>
    public sealed class XsltExplanationAdapter : IMappingExplanationAdapter
    {
        public string Engine => "xslt";

        private static readonly XNamespace XslNs = "http://www.w3.org/1999/XSL/Transform";

        private static readonly HashSet<string> KnownXslElements = new(StringComparer.Ordinal)
        {
            "template", "value-of", "for-each", "if", "choose", "when", "otherwise", "variable", "stylesheet", "text",
        };

        private static readonly EngineCapabilities FixedCapabilities =
            new(Execute: true, Explain: true, Author: true, Compile: false, Publish: false);

        private readonly IMappingDraftStore _store;
        private readonly ILogger<XsltExplanationAdapter> _logger;

        public XsltExplanationAdapter(IMappingDraftStore store, ILogger<XsltExplanationAdapter> logger)
        {
            _store = store;
            _logger = logger;
        }

        /// <summary>
        /// Dependência real: mesmo <see cref="IMappingDraftStore"/> (SQL) usado pelo Tcl adapter —
        /// ver <see cref="TclExplanationAdapter.CheckAvailabilityAsync"/> para o racional completo.
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
                _logger.LogWarning(ex, "Gate de capacidade (#90): MappingDraftStore indisponível para engine=xslt.");
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
                _logger.LogError(ex, "Falha ao consultar draft {DraftId} para explicação XSLT.", draftId);
                throw;
            }

            if (draft == null || draft.WorkspaceId != request.WorkspaceId || !string.Equals(draft.Engine, "xslt", StringComparison.OrdinalIgnoreCase))
                return null;

            // Slice 4: nenhum MappingRelease/artefato XSLT compilado existe ainda para um Draft
            // (Slice 5 introduz a compilação real). Retorna unsupported honesto, não inventa.
            return new MappingExplanation(
                MappingId: request.MappingId,
                Version: "draft",
                Engine: "xslt",
                Capabilities: FixedCapabilities,
                SourceSchema: null,
                TargetSchema: null,
                Rules: Array.Empty<ExplainedRule>(),
                Description: null,
                Limitations: new[]
                {
                    "Nenhum artefato XSLT compilado está associado a este draft ainda — a compilação " +
                    "(MappingRelease) é introduzida no Slice 5. Use engine=tcl para ver as regras estruturadas " +
                    "propostas para este mesmo draft.",
                },
                OpaqueRuleCount: 0);
        }

        /// <summary>
        /// Parser real de árvore XSLT → contrato canônico. Público/estático para ser testável
        /// isoladamente (design §2.3) mesmo sem uma fonte real ligada ainda ao endpoint.
        /// Nunca lança para XML malformado ou extensão desconhecida — degrada para poucas regras +
        /// limitations.
        /// </summary>
        public static MappingExplanation ExplainXsltDocument(string mappingId, string version, string xsltContent)
        {
            var limitations = new List<string>();
            XDocument doc;
            try
            {
                doc = XDocument.Parse(xsltContent);
            }
            catch (Exception ex)
            {
                return new MappingExplanation(
                    mappingId, version, "xslt", FixedCapabilities, null, null,
                    Array.Empty<ExplainedRule>(), null,
                    new[] { $"XSLT ilegível: {ex.Message}" }, 0);
            }

            var rules = new List<ExplainedRule>();
            var templates = doc.Descendants(XslNs + "template").ToList();

            var index = 0;
            foreach (var template in templates)
            {
                var match = (string?)template.Attribute("match");
                var name = (string?)template.Attribute("name");
                var templateLabel = match ?? name ?? $"template#{index}";

                foreach (var stmt in template.Descendants())
                {
                    var rule = TryExplainStatement(stmt, templateLabel, ref index);
                    if (rule != null)
                        rules.Add(rule);
                }
                index++;
            }

            if (rules.Count == 0)
                limitations.Add("Nenhum elemento XSLT reconhecido (value-of/for-each/if/choose) foi encontrado nos templates.");

            var opaqueCount = rules.Count(r => r.SupportLevel == MappingExplanationSupportLevel.Opaque);

            return new MappingExplanation(
                mappingId, version, "xslt", FixedCapabilities, null, null,
                rules, null, limitations, opaqueCount);
        }

        private static ExplainedRule? TryExplainStatement(XElement el, string templateLabel, ref int index)
        {
            if (el.Name.Namespace != XslNs)
            {
                // Extensão fora do namespace xsl: (msxsl:, extensões de terceiro) — existe, mas opaco.
                index++;
                return OpaqueElement(el, templateLabel, index);
            }

            var localName = el.Name.LocalName;
            if (!KnownXslElements.Contains(localName))
            {
                index++;
                return OpaqueElement(el, templateLabel, index);
            }

            index++;
            var ruleId = $"{templateLabel}:{localName}:{index}";

            return localName switch
            {
                "value-of" => Explained(ruleId, (string?)el.Attribute("select"), "value-of",
                    $"Emite o valor de \"{(string?)el.Attribute("select")}\" no template \"{templateLabel}\"."),
                "for-each" => Explained(ruleId, (string?)el.Attribute("select"), "for-each",
                    $"Repete para cada nó de \"{(string?)el.Attribute("select")}\" no template \"{templateLabel}\"."),
                "if" => Explained(ruleId, (string?)el.Attribute("test"), "if",
                    $"Condicional: só executa quando \"{(string?)el.Attribute("test")}\" é verdadeiro.", isCondition: true),
                "when" => Explained(ruleId, (string?)el.Attribute("test"), "choose/when",
                    $"Ramo de choose: executa quando \"{(string?)el.Attribute("test")}\" é verdadeiro.", isCondition: true),
                "variable" => Explained(ruleId, (string?)el.Attribute("select"), "variable",
                    $"Declara a variável \"{(string?)el.Attribute("name")}\" no template \"{templateLabel}\"."),
                _ => null, // template/stylesheet/choose/otherwise/text: estrutural, não vira regra própria.
            };
        }

        private static ExplainedRule Explained(string ruleId, string? select, string operation, string description, bool isCondition = false)
        {
            return new ExplainedRule(
                RuleId: ruleId,
                SourceRefs: string.IsNullOrWhiteSpace(select) ? Array.Empty<string>() : new[] { select! },
                TargetRefs: Array.Empty<string>(),
                Condition: isCondition ? select : null,
                Operations: new[] { operation },
                Cardinality: operation == "for-each" ? "1:N" : "1:1",
                Evidence: Array.Empty<EvidenceRef>(),
                HumanDescription: description,
                TechnicalDetail: null,
                SupportLevel: MappingExplanationSupportLevel.Authoritative);
        }

        private static ExplainedRule OpaqueElement(XElement el, string templateLabel, int index)
        {
            var ruleId = $"{templateLabel}:opaque:{index}";
            return new ExplainedRule(
                RuleId: ruleId,
                SourceRefs: Array.Empty<string>(),
                TargetRefs: Array.Empty<string>(),
                Condition: null,
                Operations: Array.Empty<string>(),
                Cardinality: "1:1",
                Evidence: Array.Empty<EvidenceRef>(),
                HumanDescription: $"Elemento \"{el.Name}\" encontrado no template \"{templateLabel}\", fora da lista de elementos XSLT suportados por este explicador.",
                TechnicalDetail: el.ToString().Length > 400 ? el.ToString()[..400] + "…" : el.ToString(),
                SupportLevel: MappingExplanationSupportLevel.Opaque);
        }
    }
}
