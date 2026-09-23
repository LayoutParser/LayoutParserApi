using System.Xml.Linq;

namespace XslSynth.Metrics;

/// <summary>Resultado da validação de UM candidato XSLT gerado pelo LLM contra o gabarito.</summary>
public sealed record ValidationResult(
    bool WellFormedXml,
    double TagOverlapRatio,
    double TextSimilarityRatio,
    bool? XsdValid, // null = sem oráculo XSD plugável para este caso (ver limitação abaixo)
    string? ParseError,
    // Campos do gabarito (folhas literais + xsl:attribute) e quantos o candidato reproduz IDÊNTICOS
    // (mesmo conteúdo XSLT normalizado). Loose = casa por "pai/nome" (ignora a casca acima);
    // Strict = casa pelo caminho completo (penaliza raiz/namespace faltando). Issue #438.
    int FieldsTotal = 0, int FieldsIdenticalLoose = 0, int FieldsIdenticalStrict = 0);

/// <summary>
/// Validação do candidato gerado no job de métricas em lote.
///
/// LIMITAÇÃO HONESTA (documentada, não escondida): este dataset mistura NFe/CTe/MDFe em
/// operações variadas (emissão, cancelamento, consulta de status, inutilização…) cada uma
/// com sua própria raiz e XSD SEFAZ específico (cancCTe, consStatServCte, evento, etc.) —
/// mapear "qual XSD valida qual caso" exigiria um catálogo caso-a-caso que não existe hoje
/// neste projeto (os XSDs plugados em <see cref="XslSynth.Core.XsdValidator"/> cobrem hoje
/// só o leiaute NF-e de emissão completo, usado no fluxo --generate). Por isso XsdValid fica
/// <c>null</c> quando não há XSD certo disponível — em vez de inventar uma validação que
/// daria falso-negativo sistemático. A métrica que SEMPRE roda (mesma do spike anterior) é
/// a similaridade estrutural: TagOverlapRatio (Jaccard de nomes de elemento) e
/// TextSimilarityRatio (razão baseada em LCS, aproximação do difflib.SequenceMatcher.ratio
/// usado no spike Python — ver rag-spike-cpu-throughput-2026-07-29).
/// </summary>
public static class OutputValidator
{
    private const int MaxCharsParaLcs = 4000; // custo O(n*m) da LCS — teto de segurança

    public static ValidationResult Validate(string generatedXslt, string expectedXslt)
    {
        bool wellFormed;
        string? parseError = null;
        XElement? generatedRoot = null;
        try
        {
            generatedRoot = XDocument.Parse(generatedXslt).Root;
            wellFormed = generatedRoot is not null;
        }
        catch (Exception ex)
        {
            wellFormed = false;
            parseError = ex.Message;
        }

        var tagOverlap = TagOverlapRatio(generatedXslt, expectedXslt);
        var textSim = TextSimilarityRatio(generatedXslt, expectedXslt);

        var (total, loose, strict) = FieldMatch(generatedXslt, expectedXslt);
        return new ValidationResult(wellFormed, tagOverlap, textSim, XsdValid: null, parseError, total, loose, strict);
    }

    /// <summary>
    /// Compara campo a campo (folhas literais e xsl:attribute) o candidato com o gabarito.
    /// Assinatura de um campo = conteúdo XSLT dele com espaços normalizados. Tolera XML malformado (0/0/0).
    /// </summary>
    internal static (int Total, int Loose, int Strict) FieldMatch(string generatedXslt, string expectedXslt)
    {
        var exp = Fields(expectedXslt);
        var gen = Fields(generatedXslt);
        if (exp is null || gen is null) return (exp?.Count ?? 0, 0, 0);

        var genFull = new HashSet<string>(gen.Select(f => f.Path + "=" + f.Sig), StringComparer.Ordinal);
        var genLoose = new HashSet<string>(gen.Select(f => f.Tail + "=" + f.Sig), StringComparer.Ordinal);
        return (exp.Count,
            exp.Count(f => genLoose.Contains(f.Tail + "=" + f.Sig)),
            exp.Count(f => genFull.Contains(f.Path + "=" + f.Sig)));
    }

    private static List<(string Path, string Tail, string Sig)>? Fields(string xslt)
    {
        XElement? root;
        try { root = XDocument.Parse(xslt).Root; }
        catch { return null; }
        if (root is null) return null;

        XNamespace xsl = "http://www.w3.org/1999/XSL/Transform";
        var result = new List<(string, string, string)>();

        static string Sig(IEnumerable<XNode> nodes) =>
            System.Text.RegularExpressions.Regex.Replace(
                string.Concat(nodes.Select(n => n.ToString(SaveOptions.DisableFormatting))), @">\s+<|\s+", m => m.Value.StartsWith(">") ? "><" : " ").Trim();

        // Ancestrais literais (sem namespace de prefixo xsl) até o elemento.
        static List<string> Anc(XElement e, XNamespace xsl) =>
            e.AncestorsAndSelf().Where(a => a.Name.Namespace != xsl).Select(a => a.Name.LocalName).Reverse().ToList();

        foreach (var el in root.Descendants())
        {
            if (el.Name == xsl + "attribute" && (string?)el.Attribute("name") is { } an && el.Parent is { } owner && owner.Name.Namespace != xsl)
            {
                var path = string.Join('/', Anc(owner, xsl).Append("@" + an));
                result.Add((path, Tail(path), Sig(el.Nodes())));
            }
            else if (el.Name.Namespace != xsl && !el.Elements().Any(c => c.Name.Namespace != xsl))
            {
                var path = string.Join('/', Anc(el, xsl));
                result.Add((path, Tail(path), Sig(el.Nodes())));
            }
        }
        return result;

        static string Tail(string path)
        {
            var segs = path.Split('/');
            return string.Join('/', segs.Skip(Math.Max(0, segs.Length - 2)));
        }
    }

    /// <summary>Jaccard sobre o conjunto de nomes de elemento (local name) presentes em cada XML.
    /// Tolera XML malformado (regex sobre o texto) — não derruba a métrica quando o parse falha.</summary>
    private static double TagOverlapRatio(string a, string b)
    {
        var tagsA = ExtractTagNames(a);
        var tagsB = ExtractTagNames(b);
        if (tagsA.Count == 0 && tagsB.Count == 0) return 0;
        var inter = tagsA.Count(tagsB.Contains);
        var uniao = tagsA.Union(tagsB).Count();
        return uniao == 0 ? 0 : (double)inter / uniao;
    }

    private static HashSet<string> ExtractTagNames(string xml)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(xml, @"<\s*([A-Za-z_][\w:.-]*)"))
        {
            var nome = m.Groups[1].Value;
            var semPrefixo = nome.Contains(':') ? nome[(nome.IndexOf(':') + 1)..] : nome;
            set.Add(semPrefixo);
        }
        return set;
    }

    /// <summary>Razão de similaridade textual baseada em LCS (2*|LCS| / (|a|+|b|)) — mesma
    /// forma de cálculo do difflib.SequenceMatcher.ratio() do Python (aproximação: LCS em vez
    /// do algoritmo de blocos casados do Ratcliff/Obershelp, mais barato de implementar e
    /// suficiente para o sinal desejado). Textos maiores que <see cref="MaxCharsParaLcs"/> são
    /// truncados para manter o custo O(n*m) controlado no lote inteiro.</summary>
    private static double TextSimilarityRatio(string a, string b)
    {
        var sa = Truncate(Normalize(a));
        var sb = Truncate(Normalize(b));
        if (sa.Length == 0 && sb.Length == 0) return 1.0;
        if (sa.Length == 0 || sb.Length == 0) return 0.0;

        var lcs = LcsLength(sa, sb);
        return 2.0 * lcs / (sa.Length + sb.Length);
    }

    private static string Normalize(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();

    private static string Truncate(string s) => s.Length <= MaxCharsParaLcs ? s : s[..MaxCharsParaLcs];

    private static int LcsLength(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
                curr[j] = a[i - 1] == b[j - 1] ? prev[j - 1] + 1 : Math.Max(prev[j], curr[j - 1]);
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}
