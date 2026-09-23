using System.Globalization;
using System.Xml.Linq;
using System.Xml.XPath;

using LayoutParserApi.Models.Entities.Fiscal;

namespace LayoutParserApi.Services.Fiscal
{
    /// <summary>
    /// Runner determinístico para <c>engine=tcl</c> no Fiscal Test Lab (issue #421). Interpreta
    /// diretamente as <see cref="MappingDraftRule"/> aceitas/editadas contra o XML de entrada — NÃO
    /// faz round-trip pelo texto TCL (<c>&lt;MAP&gt;&lt;LINE&gt;&lt;FIELD&gt;</c>) gerado por
    /// <see cref="MappingDraftRuleTranspiler.ToTcl"/>, porque esse dialeto é lossy para a operação
    /// <c>conditional</c> (o texto TCL colapsa <c>sourceRef</c> e <c>value</c> literal no mesmo
    /// atributo — ver <c>BuildTclField</c> — perdendo a distinção "copiar campo X" vs "escrever texto
    /// literal X"). Reaproveita o mesmo parsing de contrato JSON de <c>conditions</c>/
    /// <c>transformations</c> usado pelo transpilador (<see cref="MappingDraftRuleTranspiler.ReadConditions"/>
    /// etc., tornados <c>internal</c> para este fim) — mesma fonte de verdade, sem duplicar regras.
    /// <para>
    /// Importante: "TCL" aqui NÃO é a linguagem Tcl (Tool Command Language) real — é o dialeto
    /// declarativo interno <c>&lt;MAP&gt;&lt;LINE&gt;&lt;FIELD op="..." source="..."/&gt;&lt;/LINE&gt;&lt;/MAP&gt;</c>
    /// confirmado em <c>docs/architecture/decisao-dsl-mapper-sysmiddle-2026-08-21.md</c>. Não existe
    /// nenhum interpretador Tcl real neste repositório (o runner Sysmiddle está fora de alcance —
    /// bloqueio de licença documentado). Como as regras já são estruturadas e determinísticas
    /// (mesma fonte usada para compilar o artefato XSLT), dá para "executar" o mapeamento TCL
    /// avaliando XPath contra o XML de entrada — sem precisar de motor Tcl externo nem sandboxing.
    /// </para>
    /// </summary>
    public static class TclRuleApplier
    {
        /// <summary>Operações suportadas — mesmo catálogo do transpilador (mantém paridade de cobertura entre engines).</summary>
        private static readonly HashSet<string> SupportedOperations = new(StringComparer.OrdinalIgnoreCase)
        {
            "copy", "concat", "lookup", "conditional", "constant"
        };

        /// <summary>
        /// Aplica as regras (já filtradas para <c>accepted</c>/<c>edited</c> pelo chamador, mesma
        /// ordem de <see cref="MappingDraftRuleTranspiler.ToTcl"/> — <c>OrderBy(RuleId)</c>) contra
        /// <paramref name="input"/> e devolve o XML de saída, análogo ao que <c>XsltApplier</c> faz
        /// para o engine XSLT.
        /// </summary>
        public static XDocument Apply(IReadOnlyList<MappingDraftRule> rules, string targetRootName, XDocument input)
        {
            ArgumentNullException.ThrowIfNull(rules);
            ArgumentNullException.ThrowIfNull(targetRootName);
            ArgumentNullException.ThrowIfNull(input);

            var root = new XElement(targetRootName);
            foreach (var rule in rules)
            {
                var element = BuildElement(rule, input);
                if (element != null)
                {
                    root.Add(element);
                }
            }

            return new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
        }

        private static XElement? BuildElement(MappingDraftRule rule, XDocument input)
        {
            if (rule.TargetRefs.Count == 0 || !SupportedOperations.Contains(rule.Operation))
            {
                // Mesmo comportamento do transpilador (BuildTclField/BuildXsltRuleElement): regra fora
                // do catálogo determinístico é ignorada na emissão, não derruba o job inteiro.
                return null;
            }

            var targetName = MappingDraftRuleTranspiler.LastSegment(rule.TargetRefs[0]);

            string value;
            try
            {
                value = rule.Operation.ToLowerInvariant() switch
                {
                    "copy" => rule.SourceRefs.Count > 0 ? EvaluateString(input, rule.SourceRefs[0]) : "",
                    "concat" => BuildConcat(rule, input),
                    "lookup" => BuildLookup(rule, input),
                    "conditional" => BuildConditional(rule, input),
                    "constant" => MappingDraftRuleTranspiler.ReadTransformationString(rule, "constant", "value") ?? "",
                    _ => "",
                };
            }
            catch (Exception)
            {
                // Regra com JSON malformado ou XPath inválido — igual ao transpilador, vira omissão
                // silenciosa do campo em vez de derrubar o test-run inteiro (degrade gracioso).
                return null;
            }

            return new XElement(targetName, value);
        }

        private static string BuildConcat(MappingDraftRule rule, XDocument input)
        {
            if (rule.SourceRefs.Count < 2)
            {
                return "";
            }

            var separator = MappingDraftRuleTranspiler.ReadTransformationString(rule, "concat", "separator") ?? "";
            return string.Join(separator, rule.SourceRefs.Select(r => EvaluateString(input, r)));
        }

        private static string BuildLookup(MappingDraftRule rule, XDocument input)
        {
            if (rule.SourceRefs.Count == 0)
            {
                return "";
            }

            var key = EvaluateString(input, rule.SourceRefs[0]);
            var spec = MappingDraftRuleTranspiler.ReadLookupTable(rule);
            var match = spec.Table.FirstOrDefault(kv => kv.Key == key);
            return match.Key != null ? match.Value : (spec.DefaultValue ?? key);
        }

        private static string BuildConditional(MappingDraftRule rule, XDocument input)
        {
            var conditions = MappingDraftRuleTranspiler.ReadConditions(rule);
            foreach (var cond in conditions.Where(c => !c.IsDefault))
            {
                if (EvaluateBoolean(input, cond.TestXPath))
                {
                    return string.IsNullOrEmpty(cond.SourceRef) ? (cond.Value ?? "") : EvaluateString(input, cond.SourceRef);
                }
            }

            var defaultCond = conditions.LastOrDefault(c => c.IsDefault);
            if (defaultCond == null)
            {
                return "";
            }

            return string.IsNullOrEmpty(defaultCond.SourceRef) ? (defaultCond.Value ?? "") : EvaluateString(input, defaultCond.SourceRef);
        }

        /// <summary>
        /// Avalia <paramref name="xpath"/> contra a raiz do documento (mesmo contexto que
        /// <c>xsl:value-of select="..."</c> teria dentro de <c>xsl:template match="/"</c>). Node-set
        /// vira o valor do primeiro nó; escalar vira string diretamente.
        /// </summary>
        private static string EvaluateString(XDocument input, string xpath)
        {
            if (string.IsNullOrWhiteSpace(xpath))
            {
                return "";
            }

            var result = input.XPathEvaluate(xpath);
            if (result is IEnumerable<object> nodes)
            {
                var first = nodes.FirstOrDefault();
                return first switch
                {
                    XElement el => el.Value,
                    XAttribute attr => attr.Value,
                    XText text => text.Value,
                    null => "",
                    _ => first.ToString() ?? "",
                };
            }

            return result switch
            {
                bool b => b ? "true" : "false",
                double d => d.ToString(CultureInfo.InvariantCulture),
                null => "",
                _ => result.ToString() ?? "",
            };
        }

        /// <summary>
        /// Coerção pra booleano seguindo as regras do XPath 1.0 (node-set não-vazio / número != 0 &amp;&amp;
        /// não-NaN / string não-vazia) — aproximação do que <c>xsl:when test="..."</c> faria num
        /// processador XSLT real. Documentada como limitação: não é um processador XSLT completo,
        /// é suficiente para os testes XPath simples usados no contrato de <c>conditions</c> (spec §8).
        /// </summary>
        private static bool EvaluateBoolean(XDocument input, string xpath)
        {
            if (string.IsNullOrWhiteSpace(xpath))
            {
                return false;
            }

            var result = input.XPathEvaluate(xpath);
            return result switch
            {
                IEnumerable<object> nodes => nodes.Any(),
                bool b => b,
                double d => d != 0 && !double.IsNaN(d),
                string s => s.Length > 0,
                _ => false,
            };
        }
    }
}
