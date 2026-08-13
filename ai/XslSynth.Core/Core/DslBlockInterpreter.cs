using System.Text.RegularExpressions;
using XslSynth.Model;
using XslSynth.Synthesis;

namespace XslSynth.Core;

/// <summary>
/// Interpretador DETERMINÍSTICO (sem IA) do padrão DSL Sysmiddle DOMINANTE nas regras
/// reais de NF-e: uma única <see cref="MapperRule"/> emite VÁRIAS saídas, cada uma no
/// formato "emite só se a origem não estiver vazia":
///
/// <code>
///   #.vBC = I.LINHA050/BaseDeCalculoDoICMS;          ← binding temp → input
///   ...
///   if(IsNullOrEmpty(#.vBC) != True())
///   begin
///       T.enviNFe/NFe/infNFe/total/ICMSTot/vBC = FormaterDecimal(#.vBC, 2);
///   end
/// </code>
///
/// Cada bloco vira UMA <see cref="RuleTranslation"/> ancorada no seu <c>T.</c> real:
/// <code>&lt;xsl:if test="string(LINHA050/BaseDeCalculoDoICMS)!=''"&gt;
///        &lt;xsl:value-of select="format-number(LINHA050/BaseDeCalculoDoICMS,'0.00')"/&gt;
///      &lt;/xsl:if&gt;</code>
///
/// Blocos que fogem do padrão (condição composta com <c>&amp;&amp;</c>, atribuição a temp
/// dentro do <c>begin</c>, aninhamento, <c>else</c>) NÃO são reconhecidos aqui — ficam
/// para o LLM/expansões futuras. É o limite honesto: traduz o comum, não inventa o raro.
/// </summary>
public sealed class DslBlockInterpreter
{
    // #.tmp = I.LINHA050/Campo;
    private static readonly Regex BindTemp =
        new(@"#\.([A-Za-z0-9_]+)\s*=\s*I\.([A-Za-z0-9_/]+)\s*;", RegexOptions.Compiled);

    // if(IsNullOrEmpty(<op>) != True()) begin T.<path> = <rhs>; end
    // <op> e <rhs> aceitam temp (#.x), input (I.LINHA/x) ou literal ('...').
    private static readonly Regex GuardedEmit = new(
        @"if\s*\(\s*IsNullOrEmpty\(\s*(#\.[A-Za-z0-9_]+|I\.[A-Za-z0-9_/]+)\s*\)\s*!=\s*True\(\)\s*\)\s*" +
        @"begin\s*T\.([A-Za-z0-9_/]+)\s*=\s*(.+?)\s*;\s*end",
        RegexOptions.Compiled | RegexOptions.Singleline);

    // T.<path> = <rhs>;   (atribuição direta, sem guarda) — buscada no que sobra.
    private static readonly Regex DirectEmit =
        new(@"T\.([A-Za-z0-9_/]+)\s*=\s*(.+?)\s*;", RegexOptions.Compiled | RegexOptions.Singleline);

    // FormaterDecimal(<op>, n)
    private static readonly Regex FmtDecimal =
        new(@"^FormaterDecimal\(\s*(#\.[A-Za-z0-9_]+|I\.[A-Za-z0-9_/]+)\s*,\s*([0-9]+)\s*\)$",
            RegexOptions.Compiled);

    /// <summary>Traduz uma regra em N emissões XSLT (vazio = padrão não reconhecido).</summary>
    public IReadOnlyList<RuleTranslation> Interpret(MapperRule rule)
    {
        var code = StripMarkers(rule.ContentValue ?? "");
        if (code.Length == 0) return Array.Empty<RuleTranslation>();

        // Tabela de símbolos: #.temp → caminho de input (LINHA.../Campo).
        var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in BindTemp.Matches(code))
            bindings[m.Groups[1].Value] = m.Groups[2].Value; // "LINHA050/Campo"

        var emissions = new List<RuleTranslation>();

        // 1. Blocos guardados (o padrão dominante). Removidos do código depois.
        var consumed = new List<(int Start, int Len)>();
        foreach (Match m in GuardedEmit.Matches(code))
        {
            var body = BuildEmission(
                guardOperand: m.Groups[1].Value, targetPath: m.Groups[2].Value,
                rhs: m.Groups[3].Value.Trim(), bindings);
            if (body is not null && XsltFragment.Compiles(body, out _))
            {
                emissions.Add(new RuleTranslation(rule, m.Groups[2].Value, body, TranslationSource.DslInterpreter));
                consumed.Add((m.Index, m.Length));
            }
        }

        // 2. Atribuições diretas T.path = rhs; no que NÃO foi consumido pelos blocos.
        var remainder = Blank(code, consumed);
        foreach (Match m in DirectEmit.Matches(remainder))
        {
            var body = BuildEmission(guardOperand: null, targetPath: m.Groups[1].Value,
                rhs: m.Groups[2].Value.Trim(), bindings);
            if (body is not null && XsltFragment.Compiles(body, out _))
                emissions.Add(new RuleTranslation(rule, m.Groups[1].Value, body, TranslationSource.DslInterpreter));
        }

        return emissions;
    }

    /// <summary>Monta o corpo XSLT de uma emissão (com guarda opcional de não-vazio).</summary>
    private static string? BuildEmission(string? guardOperand, string targetPath, string rhs,
        IReadOnlyDictionary<string, string> bindings)
    {
        var value = ValueXsl(rhs, bindings);
        if (value is null) return null; // rhs fora do subconjunto suportado

        if (guardOperand is null) return value;

        var guardPath = ResolvePath(guardOperand, bindings);
        return guardPath is null
            ? value // guarda sobre literal: sempre emite
            : $"<xsl:if test=\"string({guardPath})!=''\">{value}</xsl:if>";
    }

    /// <summary>rhs → fragmento que produz o valor. null se não reconhecer o rhs.</summary>
    private static string? ValueXsl(string rhs, IReadOnlyDictionary<string, string> bindings)
    {
        // Literal 'texto'
        if (rhs.Length >= 2 && rhs[0] == '\'' && rhs[^1] == '\'')
            return $"<xsl:text>{Escape(rhs[1..^1])}</xsl:text>";

        // FormaterDecimal(op, n)
        var fmt = FmtDecimal.Match(rhs);
        if (fmt.Success)
        {
            var path = ResolvePath(fmt.Groups[1].Value, bindings);
            if (path is null) return null;
            var casas = int.Parse(fmt.Groups[2].Value);
            var pattern = casas > 0 ? "0." + new string('0', casas) : "0";
            return $"<xsl:value-of select=\"format-number({path}, '{pattern}')\"/>";
        }

        // Cópia direta de um operando (#.temp ou I.LINHA/x)
        var direct = ResolvePath(rhs, bindings);
        return direct is null ? null : $"<xsl:value-of select=\"{direct}\"/>";
    }

    /// <summary>Resolve um operando (#.temp | I.LINHA/x) para um XPath. null se for literal/desconhecido.</summary>
    private static string? ResolvePath(string operand, IReadOnlyDictionary<string, string> bindings)
    {
        if (operand.StartsWith("I.", StringComparison.Ordinal))
            return operand[2..]; // "LINHA050/Campo"

        if (operand.StartsWith("#.", StringComparison.Ordinal))
        {
            var name = operand[2..];
            return bindings.TryGetValue(name, out var path) ? path : Symbol(name);
        }
        return null; // literal '...'
    }

    private static string StripMarkers(string dsl) =>
        dsl.Replace("%beginRuleContent;", "").Replace("%endRuleContent;", "").Trim();

    /// <summary>Zera (com espaços) os trechos já consumidos, preservando os offsets.</summary>
    private static string Blank(string code, IReadOnlyList<(int Start, int Len)> spans)
    {
        if (spans.Count == 0) return code;
        var chars = code.ToCharArray();
        foreach (var (start, len) in spans)
            for (var i = start; i < start + len && i < chars.Length; i++)
                chars[i] = ' ';
        return new string(chars);
    }

    /// <summary>Token XPath simbólico p/ temp sem binding (compila, não resolve ao input real).</summary>
    private static string Symbol(string name) => XsltFragment.SanitizeVar(name);

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
