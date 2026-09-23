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
        if (direct is not null) return $"<xsl:value-of select=\"{direct}\"/>";

        // Expressão composta (Concat/Substring/GetLength) — regra de negócio "prefixo + truncamento"
        // (ex.: xJust da inutilização) declarada NO PRÓPRIO mapeador. Sem isto a regra era perdida
        // (caía no LLM/stub) mesmo estando explícita no DSL — issue #438.
        var expr = ExprXPath(rhs, bindings);
        return expr is null ? null : $"<xsl:value-of select=\"{AttrEscape(expr)}\"/>";
    }

    // ── Expressões compostas ────────────────────────────────────────────────
    // Subconjunto DETERMINÍSTICO e semanticamente exato do DSL real (confirmado no dataset
    // sysmiddle-dsl-dataset): Concat(a, b, ...) → concat(); Substring(x, ini0, len) → substring(x, ini0+1, len)
    // (o DSL é 0-based, XPath é 1-based); GetLength(x) → string-length(x). Qualquer outra função
    // (Trim, Replace, PadLeft…) devolve null — "não inventa o raro" (Trim ≠ normalize-space, p.ex.).

    private static readonly Regex CallRx =
        new(@"^(Concat|Substring|GetLength)\s*\((.*)\)$", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex QuotedLiteral = new(@"^'([^'\\]*)'$", RegexOptions.Compiled);
    private static readonly Regex IntArg = new(@"^'?([0-9]+)'?$", RegexOptions.Compiled);

    /// <summary>Traduz uma expressão DSL composta para XPath 1.0. null = fora do subconjunto suportado.</summary>
    internal static string? ExprXPath(string expr, IReadOnlyDictionary<string, string> bindings)
    {
        expr = expr.Trim();

        var lit = QuotedLiteral.Match(expr);
        if (lit.Success)
            return "'" + lit.Groups[1].Value + "'"; // sem aspa simples/barra dentro (regex garante)

        var path = ResolvePath(expr, bindings);
        if (path is not null) return path;

        var call = CallRx.Match(expr);
        if (!call.Success || !ParensBalancedAsSingleCall(expr)) return null;

        var name = call.Groups[1].Value.ToLowerInvariant();
        var args = SplitTopLevel(call.Groups[2].Value);
        if (args is null) return null;

        switch (name)
        {
            case "concat":
            {
                if (args.Count == 0) return null;
                var parts = new List<string>(args.Count);
                foreach (var a in args)
                {
                    var p = ExprXPath(a, bindings);
                    if (p is null) return null;
                    parts.Add(p);
                }
                // XPath concat() exige >= 2 argumentos.
                return parts.Count == 1 ? $"string({parts[0]})" : $"concat({string.Join(",", parts)})";
            }
            case "substring":
            {
                if (args.Count != 3) return null;
                var src = ExprXPath(args[0], bindings);
                var ini = IntArg.Match(args[1].Trim());
                var len = IntArg.Match(args[2].Trim());
                if (src is null || !ini.Success || !len.Success) return null;
                return $"substring({src},{int.Parse(ini.Groups[1].Value) + 1},{len.Groups[1].Value})";
            }
            case "getlength":
            {
                if (args.Count != 1) return null;
                var src = ExprXPath(args[0], bindings);
                return src is null ? null : $"string-length({src})";
            }
            default:
                return null;
        }
    }

    /// <summary>Garante que o ')' final fecha o '(' da chamada externa (e não de um argumento).</summary>
    private static bool ParensBalancedAsSingleCall(string s)
    {
        var open = s.IndexOf('(');
        if (open < 0) return false;
        var depth = 0;
        var inQuote = false;
        for (var i = open; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\'') { inQuote = !inQuote; continue; }
            if (inQuote) continue;
            if (c == '(') depth++;
            else if (c == ')')
            {
                depth--;
                if (depth == 0) return i == s.Length - 1;
            }
        }
        return false;
    }

    /// <summary>Divide "a, 'b,c', f(x,y)" em argumentos no nível 0, respeitando aspas e parênteses.</summary>
    private static List<string>? SplitTopLevel(string s)
    {
        var args = new List<string>();
        var depth = 0;
        var inQuote = false;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\'') { inQuote = !inQuote; continue; }
            if (inQuote) continue;
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 0)
            {
                args.Add(s[start..i]);
                start = i + 1;
            }
            if (depth < 0) return null;
        }
        if (inQuote || depth != 0) return null;
        args.Add(s[start..]);
        return args.Any(a => a.Trim().Length == 0) ? null : args;
    }

    private static string AttrEscape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

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
