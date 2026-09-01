using System.Text;
using System.Text.Json;
using System.Xml.Linq;

using LayoutParserApi.Models.Entities.Fiscal;

namespace LayoutParserApi.Services.Fiscal
{
    /// <summary>Referência mínima a um schema alvo — só o necessário pra rotular o XSLT/TCL gerado.</summary>
    public sealed record SchemaRef(string Name, string? Namespace = null);

    /// <summary>
    /// Diagnóstico estruturado de uma regra que NÃO pôde ser transpilada (operação fora do catálogo
    /// suportado, ou payload de <c>conditions</c>/<c>transformations</c> malformado). Nunca lançamos
    /// exceção pra isso — a regra fica documentada no resultado, não falha silenciosamente.
    /// </summary>
    public sealed record TranspileDiagnostic(Guid RuleId, string Severity, string Message);

    /// <summary>Resultado da transpilação — conteúdo gerado + diagnósticos de regras não cobertas.</summary>
    public sealed record TranspileResult(string Content, IReadOnlyList<TranspileDiagnostic> Diagnostics)
    {
        public bool HasDiagnostics => Diagnostics.Count > 0;
    }

    /// <summary>
    /// Transpilador determinístico <see cref="MappingDraftRule"/> → XSLT/TCL (Slice 5 — issue #231).
    /// Decisão de arquitetura (ver design 2026-08-31): NÃO reusa <c>RepairOrchestrator</c>/
    /// <c>DeterministicXslTranspiler</c> — entrada aqui já é regra estruturada e aceita por humano
    /// (<see cref="MappingDraftRuleStatus.Accepted"/>/<see cref="MappingDraftRuleStatus.Edited"/>),
    /// sem ambiguidade a resolver com IA. Só processa regras nesses dois status — qualquer outra
    /// (<c>proposed</c>/<c>rejected</c>/<c>needs_input</c>/<c>validated</c>/<c>superseded</c>) é
    /// ignorada silenciosamente na emissão (não é erro: reflete decisão humana ainda não tomada
    /// ou já negativa).
    /// </summary>
    public static class MappingDraftRuleTranspiler
    {
        private static readonly string[] ProcessableStatuses =
        {
            MappingDraftRuleStatus.Accepted,
            MappingDraftRuleStatus.Edited
        };

        /// <summary>Operações suportadas pelo catálogo determinístico desta etapa (design §"Achados", item 1).</summary>
        private static readonly HashSet<string> SupportedOperations = new(StringComparer.OrdinalIgnoreCase)
        {
            "copy", "concat", "lookup", "conditional", "constant"
        };

        // ---------------------------------------------------------------
        // XSLT
        // ---------------------------------------------------------------

        /// <summary>
        /// Gera um XSLT 1.0 determinístico a partir das regras aceitas/editadas. Cada elemento de
        /// saída carrega o atributo customizado <c>lp:ruleId</c> (namespace
        /// <c>urn:layoutparser:provenance</c>) apontando de volta pra <see cref="MappingDraftRule.RuleId"/>
        /// — é o mecanismo de rastreabilidade pedido na spec §11 (diagnóstico sintático ligado à regra).
        /// Um comentário XML equivalente também é emitido antes de cada template, pra legibilidade
        /// humana quando o XSLT for inspecionado fora de um parser (atributo é o canal "de máquina").
        /// </summary>
        public static TranspileResult ToXslt(IReadOnlyList<MappingDraftRule> rules, SchemaRef sourceSchema, SchemaRef targetSchema)
        {
            ArgumentNullException.ThrowIfNull(rules);
            ArgumentNullException.ThrowIfNull(sourceSchema);
            ArgumentNullException.ThrowIfNull(targetSchema);

            var diagnostics = new List<TranspileDiagnostic>();
            var processable = rules.Where(r => ProcessableStatuses.Contains(r.Status, StringComparer.OrdinalIgnoreCase)).ToList();

            XNamespace xsl = "http://www.w3.org/1999/XSL/Transform";
            XNamespace lp = ProvenanceNamespace;

            var root = new XElement(xsl + "stylesheet",
                new XAttribute("version", "1.0"),
                new XAttribute(XNamespace.Xmlns + "xsl", xsl.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "lp", lp.NamespaceName),
                new XComment($" Gerado deterministicamente por MappingDraftRuleTranspiler — source={sourceSchema.Name} target={targetSchema.Name} "),
                new XElement(xsl + "output", new XAttribute("method", "xml"), new XAttribute("indent", "yes")),
                new XElement(xsl + "template", new XAttribute("match", "/"),
                    processable.Count == 0
                        ? new XElement(xsl + "text", "")
                        : new XElement(targetSchema.Name)));

            var templateRoot = root.Elements(xsl + "template").First().Elements().FirstOrDefault();

            foreach (var rule in processable)
            {
                var element = BuildXsltRuleElement(xsl, lp, rule, diagnostics);
                if (element == null)
                {
                    continue;
                }

                templateRoot?.Add(new XComment($" MappingDraftRule {rule.RuleId} "));
                templateRoot?.Add(element);
            }

            var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
            return new TranspileResult(doc.ToString(SaveOptions.None), diagnostics);
        }

        private static XElement? BuildXsltRuleElement(XNamespace xsl, XNamespace lp, MappingDraftRule rule, List<TranspileDiagnostic> diagnostics)
        {
            if (rule.TargetRefs.Count == 0)
            {
                diagnostics.Add(new TranspileDiagnostic(rule.RuleId, "error", "Regra aceita sem targetRefs — nada a emitir."));
                return null;
            }

            var targetName = LastSegment(rule.TargetRefs[0]);

            if (!SupportedOperations.Contains(rule.Operation))
            {
                diagnostics.Add(new TranspileDiagnostic(
                    rule.RuleId, "error",
                    $"Operação '{rule.Operation}' fora do catálogo determinístico suportado ({string.Join(", ", SupportedOperations)}). " +
                    "Precisa virar 'needs_input' no Slice 3 pra reentrar com decisão humana."));
                return null;
            }

            XElement content;
            try
            {
                content = rule.Operation.ToLowerInvariant() switch
                {
                    "copy" => BuildCopy(xsl, rule),
                    "concat" => BuildConcat(xsl, rule),
                    "lookup" => BuildLookup(xsl, rule),
                    "conditional" => BuildConditional(xsl, rule),
                    "constant" => BuildConstant(xsl, rule),
                    _ => throw new InvalidOperationException("Operação inesperada — não deveria chegar aqui.")
                };
            }
            catch (JsonException ex)
            {
                diagnostics.Add(new TranspileDiagnostic(rule.RuleId, "error", $"JSON inválido em conditions/transformations: {ex.Message}"));
                return null;
            }
            catch (TranspileRuleException ex)
            {
                diagnostics.Add(new TranspileDiagnostic(rule.RuleId, "error", ex.Message));
                return null;
            }

            var element = new XElement(targetName, content);
            element.SetAttributeValue(lp + "ruleId", rule.RuleId.ToString());
            return element;
        }

        private static XElement BuildCopy(XNamespace xsl, MappingDraftRule rule)
        {
            if (rule.SourceRefs.Count == 0)
            {
                throw new TranspileRuleException("Operação 'copy' exige ao menos 1 sourceRef.");
            }

            return new XElement(xsl + "value-of", new XAttribute("select", rule.SourceRefs[0]));
        }

        private static XElement BuildConcat(XNamespace xsl, MappingDraftRule rule)
        {
            if (rule.SourceRefs.Count < 2)
            {
                throw new TranspileRuleException("Operação 'concat' exige ao menos 2 sourceRefs.");
            }

            var separator = ReadTransformationString(rule, "concat", "separator") ?? "";
            var args = string.Join(", ", rule.SourceRefs.Select(r => r));
            var select = separator.Length == 0
                ? $"concat({args})"
                : $"concat({string.Join($", {BuildXPathStringLiteral(separator)}, ", rule.SourceRefs)})";

            return new XElement(xsl + "value-of", new XAttribute("select", select));
        }

        private static XElement BuildLookup(XNamespace xsl, MappingDraftRule rule)
        {
            if (rule.SourceRefs.Count == 0)
            {
                throw new TranspileRuleException("Operação 'lookup' exige 1 sourceRef.");
            }

            var lookup = ReadLookupTable(rule);
            var choose = new XElement(xsl + "choose");
            foreach (var (key, value) in lookup.Table)
            {
                choose.Add(new XElement(xsl + "when",
                    new XAttribute("test", $"{rule.SourceRefs[0]} = {BuildXPathStringLiteral(key)}"),
                    new XElement(xsl + "text", value)));
            }

            choose.Add(new XElement(xsl + "otherwise",
                lookup.DefaultValue != null
                    ? new XElement(xsl + "text", lookup.DefaultValue)
                    : new XElement(xsl + "value-of", new XAttribute("select", rule.SourceRefs[0]))));

            return choose;
        }

        private static XElement BuildConditional(XNamespace xsl, MappingDraftRule rule)
        {
            var conditions = ReadConditions(rule);
            if (conditions.Count == 0)
            {
                throw new TranspileRuleException("Operação 'conditional' exige ao menos 1 entrada em conditions.");
            }

            var choose = new XElement(xsl + "choose");
            foreach (var cond in conditions)
            {
                choose.Add(new XElement(xsl + "when",
                    new XAttribute("test", cond.TestXPath),
                    string.IsNullOrEmpty(cond.SourceRef)
                        ? new XElement(xsl + "text", cond.Value ?? "")
                        : new XElement(xsl + "value-of", new XAttribute("select", cond.SourceRef))));
            }

            var defaultCond = conditions.LastOrDefault(c => c.IsDefault);
            choose.Add(new XElement(xsl + "otherwise",
                defaultCond != null && !string.IsNullOrEmpty(defaultCond.SourceRef)
                    ? new XElement(xsl + "value-of", new XAttribute("select", defaultCond.SourceRef))
                    : new XElement(xsl + "text", defaultCond?.Value ?? "")));

            return choose;
        }

        private static XElement BuildConstant(XNamespace xsl, MappingDraftRule rule)
        {
            var value = ReadTransformationString(rule, "constant", "value")
                ?? throw new TranspileRuleException("Operação 'constant' exige transformations[0].value.");

            return new XElement(xsl + "text", value);
        }

        // ---------------------------------------------------------------
        // TCL — <MAP><LINE><FIELD>
        // ---------------------------------------------------------------

        /// <summary>
        /// Gera TCL determinístico no formato <c>&lt;MAP&gt;&lt;LINE&gt;&lt;FIELD&gt;</c> (formato real
        /// confirmado em <c>docs/architecture/decisao-dsl-mapper-sysmiddle-2026-08-21.md</c>). Cada
        /// <c>&lt;FIELD&gt;</c> carrega <c>ruleId="..."</c> pra rastreabilidade — mesmo princípio do
        /// atributo <c>lp:ruleId</c> do XSLT, adaptado ao dialeto TCL (sem namespace formal).
        /// </summary>
        public static TranspileResult ToTcl(IReadOnlyList<MappingDraftRule> rules, SchemaRef sourceSchema, SchemaRef targetSchema)
        {
            ArgumentNullException.ThrowIfNull(rules);
            ArgumentNullException.ThrowIfNull(sourceSchema);
            ArgumentNullException.ThrowIfNull(targetSchema);

            var diagnostics = new List<TranspileDiagnostic>();
            var processable = rules.Where(r => ProcessableStatuses.Contains(r.Status, StringComparer.OrdinalIgnoreCase)).ToList();

            var sb = new StringBuilder();
            sb.AppendLine("<MAP>");
            sb.AppendLine($"\t<LINE identifier=\"{Escape(targetSchema.Name)}\" name=\"{Escape(targetSchema.Name)}\">");

            foreach (var rule in processable)
            {
                var field = BuildTclField(rule, diagnostics);
                if (field != null)
                {
                    sb.AppendLine($"\t\t{field}");
                }
            }

            sb.AppendLine("\t</LINE>");
            sb.AppendLine("</MAP>");

            return new TranspileResult(sb.ToString(), diagnostics);
        }

        private static string? BuildTclField(MappingDraftRule rule, List<TranspileDiagnostic> diagnostics)
        {
            if (rule.TargetRefs.Count == 0)
            {
                diagnostics.Add(new TranspileDiagnostic(rule.RuleId, "error", "Regra aceita sem targetRefs — nada a emitir."));
                return null;
            }

            if (!SupportedOperations.Contains(rule.Operation))
            {
                diagnostics.Add(new TranspileDiagnostic(
                    rule.RuleId, "error",
                    $"Operação '{rule.Operation}' fora do catálogo determinístico suportado ({string.Join(", ", SupportedOperations)})."));
                return null;
            }

            var targetName = LastSegment(rule.TargetRefs[0]);
            string source;
            string op = rule.Operation.ToLowerInvariant();

            try
            {
                source = op switch
                {
                    "copy" => rule.SourceRefs.Count > 0
                        ? rule.SourceRefs[0]
                        : throw new TranspileRuleException("Operação 'copy' exige ao menos 1 sourceRef."),
                    "concat" => rule.SourceRefs.Count >= 2
                        ? string.Join("+", rule.SourceRefs)
                        : throw new TranspileRuleException("Operação 'concat' exige ao menos 2 sourceRefs."),
                    "lookup" => rule.SourceRefs.Count > 0
                        ? rule.SourceRefs[0]
                        : throw new TranspileRuleException("Operação 'lookup' exige 1 sourceRef."),
                    "conditional" => ReadConditions(rule).Count > 0
                        ? string.Join("|", ReadConditions(rule).Select(c => $"{c.TestXPath}=>{c.SourceRef ?? c.Value}"))
                        : throw new TranspileRuleException("Operação 'conditional' exige ao menos 1 entrada em conditions."),
                    "constant" => ReadTransformationString(rule, "constant", "value")
                        ?? throw new TranspileRuleException("Operação 'constant' exige transformations[0].value."),
                    _ => throw new InvalidOperationException("Operação inesperada.")
                };
            }
            catch (JsonException ex)
            {
                diagnostics.Add(new TranspileDiagnostic(rule.RuleId, "error", $"JSON inválido em conditions/transformations: {ex.Message}"));
                return null;
            }
            catch (TranspileRuleException ex)
            {
                diagnostics.Add(new TranspileDiagnostic(rule.RuleId, "error", ex.Message));
                return null;
            }

            var lookupSuffix = op == "lookup" ? $" lookupTable=\"{Escape(SerializeLookupTable(rule))}\"" : "";

            return $"<FIELD name=\"{Escape(targetName)}\" op=\"{Escape(op)}\" source=\"{Escape(source)}\" ruleId=\"{rule.RuleId}\"{lookupSuffix}/>";
        }

        // ---------------------------------------------------------------
        // JSON contract — conditions/transformations (livre por operação, spec §8)
        // ---------------------------------------------------------------

        /// <summary>
        /// Contrato de <c>TransformationsJson</c> aceito: array de objetos
        /// <c>{"type":"concat","separator":"..."}</c> / <c>{"type":"constant","value":"..."}</c> /
        /// <c>{"type":"lookup","table":{"chave":"valor"},"default":"..."}</c>. Só o objeto com
        /// <c>type</c> correspondente à operação é lido — os demais são ignorados.
        /// </summary>
        private static string? ReadTransformationString(MappingDraftRule rule, string type, string property)
        {
            using var doc = JsonDocument.Parse(rule.TransformationsJson ?? "[]");
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var t) && string.Equals(t.GetString(), type, StringComparison.OrdinalIgnoreCase)
                    && item.TryGetProperty(property, out var value))
                {
                    return value.GetString();
                }
            }

            return null;
        }

        private sealed record LookupTableSpec(IReadOnlyList<(string Key, string Value)> Table, string? DefaultValue);

        private static LookupTableSpec ReadLookupTable(MappingDraftRule rule)
        {
            using var doc = JsonDocument.Parse(rule.TransformationsJson ?? "[]");
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var t) || !string.Equals(t.GetString(), "lookup", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!item.TryGetProperty("table", out var table))
                {
                    throw new TranspileRuleException("Operação 'lookup' exige transformations[0].table.");
                }

                var entries = table.EnumerateObject().Select(p => (p.Name, p.Value.GetString() ?? "")).ToList();
                var defaultValue = item.TryGetProperty("default", out var d) ? d.GetString() : null;
                return new LookupTableSpec(entries, defaultValue);
            }

            throw new TranspileRuleException("Operação 'lookup' exige um item {\"type\":\"lookup\"} em transformations.");
        }

        private static string SerializeLookupTable(MappingDraftRule rule)
        {
            var spec = ReadLookupTable(rule);
            return string.Join(";", spec.Table.Select(kv => $"{kv.Key}={kv.Value}"));
        }

        /// <summary>
        /// Contrato de <c>ConditionsJson</c> aceito: array de
        /// <c>{"testXPath":"...", "sourceRef":"...", "value":"...", "default":true}</c>. Exatamente
        /// um item deve ter <c>"default":true</c> pra virar o ramo <c>xsl:otherwise</c>/fallback.
        /// </summary>
        private sealed record ConditionSpec(string TestXPath, string? SourceRef, string? Value, bool IsDefault);

        private static List<ConditionSpec> ReadConditions(MappingDraftRule rule)
        {
            using var doc = JsonDocument.Parse(rule.ConditionsJson ?? "[]");
            var result = new List<ConditionSpec>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var testXPath = item.TryGetProperty("testXPath", out var tp) ? tp.GetString() ?? "" : "";
                var sourceRef = item.TryGetProperty("sourceRef", out var sr) ? sr.GetString() : null;
                var value = item.TryGetProperty("value", out var v) ? v.GetString() : null;
                var isDefault = item.TryGetProperty("default", out var def) && def.ValueKind == JsonValueKind.True;

                if (string.IsNullOrEmpty(testXPath) && !isDefault)
                {
                    throw new TranspileRuleException("Cada entrada de conditions (exceto a default) exige testXPath.");
                }

                result.Add(new ConditionSpec(testXPath, sourceRef, value, isDefault));
            }

            return result;
        }

        // ---------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------

        public const string ProvenanceNamespace = "urn:layoutparser:provenance";

        private static string LastSegment(string reference)
        {
            var trimmed = reference.TrimEnd('/');
            var idx = trimmed.LastIndexOfAny(new[] { '/', ':' });
            return idx >= 0 ? trimmed[(idx + 1)..] : trimmed;
        }

        /// <summary>
        /// Constrói um literal de string XPath 1.0 seguro para <paramref name="value"/>. XPath 1.0 não
        /// tem sintaxe de escape de aspas dentro de um literal — o serializer (<see cref="XAttribute"/>)
        /// já cuida do escaping de entidades XML (<c>&amp;</c>, <c>&lt;</c> etc.) na saída, então NÃO
        /// escapamos aqui (escapar manualmente antes gera dupla-codificação, ex.: <c>&amp;apos;</c> vira
        /// <c>&amp;amp;apos;</c>). O problema real é sintático: um <c>'</c> dentro de um literal
        /// delimitado por <c>'</c> fecha a string prematuramente. Resolvido com a técnica padrão de
        /// XPath 1.0 — trocar o delimitador pra <c>"</c> quando o valor só tem apóstrofo, ou fatiar em
        /// <c>concat()</c> quando o valor tem os dois tipos de aspas.
        /// </summary>
        private static string BuildXPathStringLiteral(string value)
        {
            if (!value.Contains('\''))
            {
                return $"'{value}'";
            }

            if (!value.Contains('"'))
            {
                return $"\"{value}\"";
            }

            // Contém apóstrofo E aspas duplas — nenhum delimitador único serve; fatia em concat()
            // alternando trechos entre apóstrofos (delimitados por ") e o próprio apóstrofo (literal '"'"').
            var parts = new List<string>();
            var segments = value.Split('\'');
            for (var i = 0; i < segments.Length; i++)
            {
                if (segments[i].Length > 0)
                {
                    parts.Add($"\"{segments[i]}\"");
                }

                if (i < segments.Length - 1)
                {
                    parts.Add("\"'\"");
                }
            }

            return parts.Count == 0 ? "''" : $"concat({string.Join(", ", parts)})";
        }

        private static string Escape(string value) => value
            .Replace("&", "&amp;")
            .Replace("\"", "&quot;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");

        private sealed class TranspileRuleException : Exception
        {
            public TranspileRuleException(string message) : base(message)
            {
            }
        }
    }
}
