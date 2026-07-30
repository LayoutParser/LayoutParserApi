using System.Xml.Linq;

namespace XslSynth.Metrics;

/// <summary>Resultado da validação de UM candidato XSLT gerado pelo LLM contra o gabarito.</summary>
public sealed record ValidationResult(
    bool WellFormedXml,
    double TagOverlapRatio,
    double TextSimilarityRatio,
    bool? XsdValid, // null = sem oráculo XSD plugável para este caso (ver limitação abaixo)
    string? ParseError);

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

        return new ValidationResult(wellFormed, tagOverlap, textSim, XsdValid: null, parseError);
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
