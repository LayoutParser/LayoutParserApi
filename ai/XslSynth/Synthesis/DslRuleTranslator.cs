using System.Text;
using System.Text.RegularExpressions;
using XslSynth.Core;
using XslSynth.Model;

namespace XslSynth.Synthesis;

/// <summary>De onde veio a tradução de uma regra.</summary>
/// <remarks>
/// <c>DslInterpreter</c> = interpretador determinístico de blocos multi-saída
/// (padrão dominante <c>if(IsNullOrEmpty(#.x)!=True()) begin T.path = ... end</c>);
/// <c>MockFallback</c> = mini-tradutor de 1-saída; <c>Ollama</c> = LLM local.
/// </remarks>
public enum TranslationSource { Ollama, DslInterpreter, MockFallback, Untranslated }

/// <summary>Resultado da tradução DSL→XSLT de UMA regra.</summary>
/// <param name="Rule">Regra de origem.</param>
/// <param name="TargetPath">Caminho de destino (da DSL <c>T.</c>), ou null.</param>
/// <param name="BodyXsl">Corpo XSLT que produz o valor (instruções, sem wrapper).</param>
/// <param name="Source">Ollama | MockFallback | Untranslated.</param>
public sealed record RuleTranslation(MapperRule Rule, string? TargetPath, string BodyXsl, TranslationSource Source);

/// <summary>
/// Traduz regras em DSL Sysmiddle → fragmentos XSLT. Estratégia em camadas:
///   1. Ollama (LLM local) — se disponível e retornar XSLT que COMPILA;
///   2. fallback DETERMINÍSTICO (mini-tradutor dos padrões comuns) — offline;
///   3. stub comentado — se nem o LLM nem o fallback resolverem (regra fica visível
///      no candidato p/ cobertura, mas honestamente marcada como NÃO traduzida).
///
/// Convenção da DSL passada ao LLM:
///   T.&lt;path&gt;      = elemento de saída (caminho completo na NF-e)
///   I.LINHA&lt;n&gt;/&lt;campo&gt; = valor de input (linha/campo)
///   #.tmp / $.var  = variáveis intermediárias
///   funções: Substring(x,ini,len), GetLength(x), FormaterDecimal(x,casas), if(c) begin..end
/// </summary>
public sealed class DslRuleTranslator
{
    private readonly OllamaClient? _ollama;
    private readonly Action<string> _log;

    public DslRuleTranslator(OllamaClient? ollama, Action<string>? log = null)
    {
        _ollama = ollama;
        _log = log ?? Console.WriteLine;
    }

    public bool OllamaEnabled => _ollama is not null;

    public async Task<RuleTranslation> TranslateAsync(MapperRule rule, CancellationToken ct = default)
    {
        var target = rule.TargetPath;
        var dsl = rule.ContentValue ?? "";

        // 1. Ollama (se habilitado).
        if (_ollama is not null)
        {
            var raw = await _ollama.GenerateAsync(BuildPrompt(rule, dsl), ct);
            var body = ExtractXsl(raw);
            if (!string.IsNullOrWhiteSpace(body))
            {
                // out var err só é avaliado aqui (evita CS0165 do curto-circuito do &&).
                if (XsltFragment.Compiles(body, out var err))
                    return new RuleTranslation(rule, target, body, TranslationSource.Ollama);
                _log($"[dsl] regra '{rule.Name}': XSLT do LLM não compilou ({err}); tentando fallback.");
            }
        }

        // 2. Fallback determinístico.
        var det = TranslateDeterministic(dsl);
        if (det is not null && XsltFragment.Compiles(det, out _))
            return new RuleTranslation(rule, target, det, TranslationSource.MockFallback);

        // 3. Stub honesto.
        var stub = $"<xsl:comment> regra '{rule.Name}' NAO traduzida (DSL: {Escape(Truncate(dsl, 120))}) </xsl:comment>";
        return new RuleTranslation(rule, target, stub, TranslationSource.Untranslated);
    }

    // ── Prompt ──────────────────────────────────────────────────────────────

    private static string BuildPrompt(MapperRule rule, string dsl)
    {
        var leaf = rule.TargetPath is { } p ? Xslt.Segments(p).LastOrDefault() ?? "valor" : "valor";
        var sb = new StringBuilder();
        sb.AppendLine("Você é especialista em XSLT 1.0. Traduza a REGRA em DSL Sysmiddle abaixo");
        sb.AppendLine($"para o CORPO XSLT que produz o conteúdo do elemento <{leaf}>.");
        sb.AppendLine();
        sb.AppendLine("Convenção da DSL:");
        sb.AppendLine("  T.<path>           = elemento de SAÍDA (ignore o T.; gere só o conteúdo da folha)");
        sb.AppendLine("  I.LINHA<n>/<campo> = valor de INPUT → traduza para o XPath  LINHA<n>/<campo>");
        sb.AppendLine("  #.tmp / $.var      = variáveis → use $tmp / $var (o stylesheet as declara)");
        sb.AppendLine("  Substring(x,i,l)   = substring(x, i+1, l)   (XPath é 1-based)");
        sb.AppendLine("  GetLength(x)       = string-length(x)");
        sb.AppendLine("  FormaterDecimal(x,c)= format-number(x, '0.00' com c casas)");
        sb.AppendLine("  if(cond) begin ... end = <xsl:if test=\"cond\"> ... </xsl:if>");
        sb.AppendLine();
        sb.AppendLine("Regras de resposta:");
        sb.AppendLine("  • Responda APENAS com o fragmento XSLT (instruções xsl:), SEM o elemento");
        sb.AppendLine("    externo, SEM <xsl:stylesheet>, SEM explicação, SEM cercas de código.");
        sb.AppendLine("  • Namespace: xsl = http://www.w3.org/1999/XSL/Transform");
        sb.AppendLine();
        sb.AppendLine($"Regra: {rule.Name}");
        sb.AppendLine("DSL:");
        sb.AppendLine(dsl);
        return sb.ToString();
    }

    /// <summary>Extrai o XSLT da resposta do LLM (tira cercas ```; desembrulha stylesheet/folha).</summary>
    internal static string ExtractXsl(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var fenced = Regex.Match(raw, "```(?:xml|xslt)?\\s*(.*?)```", RegexOptions.Singleline);
        var text = (fenced.Success ? fenced.Groups[1].Value : raw).Trim();

        // Se veio um stylesheet inteiro, pega o miolo do primeiro template.
        var tpl = Regex.Match(text, "<xsl:template[^>]*>(.*)</xsl:template>", RegexOptions.Singleline);
        if (tpl.Success) text = tpl.Groups[1].Value.Trim();
        return text;
    }

    // ── Fallback determinístico (mini-tradutor de padrões conhecidos) ─────────

    private static readonly Regex LiteralStr =
        new(@"T\.[A-Za-z0-9_/]+\s*=\s*'([^']*)'\s*;", RegexOptions.Compiled);
    private static readonly Regex LiteralNum =
        new(@"T\.[A-Za-z0-9_/]+\s*=\s*([0-9]+(?:\.[0-9]+)?)\s*;", RegexOptions.Compiled);
    private static readonly Regex SubstringVar =
        new(@"T\.[A-Za-z0-9_/]+\s*=\s*Substring\(\s*\$\.([A-Za-z0-9_]+)\s*,\s*([0-9]+)\s*,\s*([0-9]+)\s*\)\s*;",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DecimalFromInput =
        new(@"T\.[A-Za-z0-9_/]+\s*=\s*FormaterDecimal\(\s*I\.([A-Za-z0-9_]+)/([A-Za-z0-9_]+)\s*,\s*([0-9]+)\s*\)\s*;",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SimpleIfInput =
        new(@"#\.[A-Za-z0-9_]+\s*=\s*I\.([A-Za-z0-9_]+)/([A-Za-z0-9_]+)\s*;\s*if\s*\(\s*#\.[A-Za-z0-9_]+\s*(>|>=|<|<=|==)\s*([0-9]+)\s*\)\s*begin\s*T\.[A-Za-z0-9_/]+\s*=\s*#\.[A-Za-z0-9_]+\s*;\s*end",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>Traduz os padrões DSL mais comuns. null se não reconhecer.</summary>
    internal static string? TranslateDeterministic(string dsl)
    {
        var code = StripMarkers(dsl);

        var m = LiteralStr.Match(code);
        if (m.Success) return $"<xsl:text>{Escape(m.Groups[1].Value)}</xsl:text>";

        m = LiteralNum.Match(code);
        if (m.Success) return $"<xsl:text>{m.Groups[1].Value}</xsl:text>";

        m = SubstringVar.Match(code);
        if (m.Success)
        {
            var v = XsltFragment.SanitizeVar(m.Groups[1].Value);
            var ini = int.Parse(m.Groups[2].Value) + 1; // DSL 0-based → XPath 1-based
            var len = m.Groups[3].Value;
            return $"<xsl:value-of select=\"substring(${v}, {ini}, {len})\"/>";
        }

        m = DecimalFromInput.Match(code);
        if (m.Success)
        {
            var path = $"{m.Groups[1].Value}/{m.Groups[2].Value}";
            var casas = int.Parse(m.Groups[3].Value);
            var fmt = casas > 0 ? "0." + new string('0', casas) : "0";
            return $"<xsl:value-of select=\"format-number({path}, '{fmt}')\"/>";
        }

        m = SimpleIfInput.Match(code);
        if (m.Success)
        {
            var path = $"{m.Groups[1].Value}/{m.Groups[2].Value}";
            var op = EscapeOp(m.Groups[3].Value);
            var num = m.Groups[4].Value;
            return $"<xsl:if test=\"{path} {op} {num}\"><xsl:value-of select=\"{path}\"/></xsl:if>";
        }

        return null;
    }

    private static string StripMarkers(string dsl) =>
        dsl.Replace("%beginRuleContent;", "").Replace("%endRuleContent;", "").Trim();

    private static string EscapeOp(string op) => op switch
    {
        ">" => "&gt;", "<" => "&lt;", ">=" => "&gt;=", "<=" => "&lt;=", "==" => "=", _ => op
    };

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Truncate(string s, int n) =>
        s.Length <= n ? s : s[..n] + "…";
}
