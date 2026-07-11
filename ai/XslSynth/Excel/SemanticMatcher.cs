using System.Text;
using System.Text.RegularExpressions;

namespace XslSynth.Excel;

// ─────────────────────────────────────────────────────────────────────────────
// SemanticMatcher — camada 2 do catálogo: casamento SEMÂNTICO e determinístico
// entre a coluna Descrição da spec Excel e a xs:documentation (PT-BR) do XSD.
//
// Sem LLM: score de Dice ponderado por IDF sobre tokens normalizados (fold de
// acentos, minúsculas, stopwords PT removidas). O IDF privilegia tokens raros
// ("suframa", "marketplace") sobre genéricos ("valor", "codigo") — que é o que
// desempata "Valor do ICMS ST retido" entre as dezenas de campos "Valor…".
//
// O chamador restringe o conjunto de candidatos (janela de ordem, subárvore do
// grupo MOC) — quanto menor a janela, maior a precisão. Exige margem sobre o
// vice para não "chutar" entre irmãos parecidos (cUF de ide × cUF de refNF).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Melhor candidato semântico e a qualidade do casamento.</summary>
/// <param name="Node">Nó vencedor do XSD (melhor nó do NOME vencedor).</param>
/// <param name="Score">Score (0..1): containment da descrição × precisão do nó.</param>
/// <param name="Margin">Distância para o segundo NOME de folha (homônimos de variantes não competem).</param>
/// <param name="TiedCount">Nós homônimos empatados no topo (variantes do choice com a mesma doc).</param>
public sealed record SemanticMatch(XsdLeiauteNode Node, double Score, double Margin, int TiedCount);

/// <summary>Casador semântico determinístico (tokens + IDF). Estático e sem estado.</summary>
public static class SemanticMatcher
{
    private static readonly Regex NonAlnum = new("[^a-z0-9]+", RegexOptions.Compiled);

    // Stopwords PT-BR (após fold/minúsculas) — palavras estruturais sem semântica.
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "de", "da", "do", "das", "dos", "a", "o", "as", "os", "e", "em", "no", "na",
        "nos", "nas", "para", "por", "com", "ao", "aos", "um", "uma", "que", "se",
        "ser", "sao", "ou", "sua", "seu", "the", "of", "etc"
    };

    // Sinônimos/abreviações do vocabulário NF-e (spec Excel × xs:documentation):
    // a spec escreve "Base Calculo", o XSD escreve "BC"; expandir aproxima os dois lados.
    private static readonly Dictionary<string, string[]> Sinonimos = new(StringComparer.Ordinal)
    {
        ["bc"] = ["base", "calculo"],
        ["vlr"] = ["valor"],
        ["qtde"] = ["quantidade"],
        ["cod"] = ["codigo"],
        ["num"] = ["numero"],
        ["perc"] = ["percentual"],
        ["aliq"] = ["aliquota"]
    };

    /// <summary>Limiar mínimo de score para aceitar um casamento.</summary>
    public const double MinScore = 0.45;

    /// <summary>Margem mínima sobre o segundo NOME (evita chute entre irmãos parecidos).</summary>
    public const double MinMargin = 0.08;

    // Boilerplate recorrente nas xs:documentation que só dilui o casamento
    // ("alterado para aceitar de 0 a 4 casas decimais…", exemplos "ex.: 2012-…").
    private static readonly Regex Boilerplate = new(
        @"(alterado para aceitar.*$)|(alter[aá]vel se necess[aá]rio.*$)|(\bex\.?\s*:\s.*$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>
    /// Tokeniza um texto PT-BR: fold de acentos, minúsculas, remove stopwords,
    /// descarta tokens só-dígitos e expande sinônimos.
    /// "Número do Documento Fiscal" → {numero, documento, fiscal}.
    /// </summary>
    public static IReadOnlyList<string> Tokens(string text)
    {
        var folded = Fold(text).ToLowerInvariant();
        var result = new List<string>();
        foreach (var t in NonAlnum.Split(folded))
        {
            if (t.Length <= 1 || Stopwords.Contains(t)) continue;
            if (t.All(char.IsDigit)) continue;   // "2012", "13", "00" — sem semântica
            if (!result.Contains(t)) result.Add(t);
            if (Sinonimos.TryGetValue(t, out var syns))
                foreach (var s in syns)
                    if (!result.Contains(s)) result.Add(s);
        }
        return result;
    }

    /// <summary>
    /// IDF por token sobre o corpus de nós do XSD (documentation + nome).
    /// Tokens raros pesam mais; tokens onipresentes ("valor") quase nada.
    /// </summary>
    public static Dictionary<string, double> BuildIdf(IReadOnlyList<XsdLeiauteNode> nodes)
    {
        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var n in nodes)
        {
            foreach (var t in NodeTokens(n))
                df[t] = df.TryGetValue(t, out var c) ? c + 1 : 1;
        }
        var total = Math.Max(nodes.Count, 1);
        return df.ToDictionary(
            kv => kv.Key,
            kv => Math.Log((double)(total + 1) / (kv.Value + 1)) + 0.1,
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Melhor candidato para a descrição dentro do conjunto dado. A MARGEM é
    /// calculada entre NOMES de folha distintos: variantes homônimas do choice
    /// (vBC em ICMS00/10/20…) têm a mesma doc e não competem entre si — o vice
    /// real é o próximo nome. Null quando score/margem não atingem o limiar —
    /// preferimos "não resolvido" honesto a chute.
    /// </summary>
    public static SemanticMatch? Best(
        string descricao,
        string? contextoBloco,
        IReadOnlyList<XsdLeiauteNode> candidates,
        IReadOnlyDictionary<string, double> idf,
        double minScore = MinScore,
        double minMargin = MinMargin)
    {
        var descTokens = Tokens(descricao);
        if (descTokens.Count == 0 || candidates.Count == 0) return null;
        var ctxTokens = contextoBloco is null ? [] : Tokens(contextoBloco);

        // Score máximo por NOME de folha (e o melhor nó de cada nome).
        var porNome = new Dictionary<string, (XsdLeiauteNode Node, double Score, int Ties)>(StringComparer.Ordinal);
        foreach (var node in candidates)
        {
            var s = Score(descTokens, ctxTokens, node, idf);
            if (s <= 0) continue;
            if (!porNome.TryGetValue(node.Name, out var cur) || s > cur.Score + 1e-9)
                porNome[node.Name] = (node, s, 1);
            else if (Math.Abs(s - cur.Score) <= 1e-9)
                porNome[node.Name] = (cur.Node, cur.Score, cur.Ties + 1);
        }
        if (porNome.Count == 0) return null;

        (XsdLeiauteNode Node, double Score, int Ties) best = default;
        double second = 0;
        foreach (var v in porNome.Values)
        {
            if (v.Score > best.Score) { second = best.Score; best = v; }
            else if (v.Score > second) { second = v.Score; }
        }

        if (best.Node is null || best.Score < minScore) return null;
        var margin = best.Score - second;
        if (margin < minMargin) return null;
        return new SemanticMatch(best.Node, best.Score, margin, best.Ties);
    }

    /// <summary>
    /// Score = containment da descrição (quanto dela o nó cobre, ponderado por
    /// IDF; nome do elemento também conta como alvo) × raiz da precisão da DOC
    /// (quanto da doc a descrição cobre — o nome NÃO entra no denominador, para
    /// não punir docs certeiras). Tolera typos do XSD (ex.: "diferemento") com
    /// edit-distance ≤ 1 em tokens longos. Contexto do bloco = só BÔNUS pequeno.
    /// </summary>
    public static double Score(
        IReadOnlyList<string> descTokens,
        IReadOnlyList<string> ctxTokens,
        XsdLeiauteNode node,
        IReadOnlyDictionary<string, double> idf)
    {
        if (descTokens.Count == 0) return 0;
        var docTokens = DocTokens(node);
        var nome = node.Name.ToLowerInvariant();
        var docSet = new HashSet<string>(docTokens, StringComparer.Ordinal);

        // Containment: quanto da DESCRIÇÃO o nó (doc + nome) cobre.
        double common = 0, descTotal = 0;
        foreach (var t in descTokens)
        {
            var w = IdfOf(idf, t);
            descTotal += w;
            if (docSet.Contains(t) || t == nome || FuzzyIn(t, docTokens)) common += w;
        }
        if (descTotal <= 0 || common <= 0) return 0;

        // Precisão: quanto da DOC a descrição cobre (doc curta e certeira vence).
        double docTotal = 0, docCommon = 0;
        var descSet = new HashSet<string>(descTokens, StringComparer.Ordinal);
        foreach (var t in docTokens)
        {
            var w = IdfOf(idf, t);
            docTotal += w;
            if (descSet.Contains(t) || FuzzyIn(t, descTokens)) docCommon += w;
        }

        var containment = common / descTotal;
        var precision = docTotal > 0 ? Math.Sqrt(docCommon / docTotal) : 0.55; // sem doc: neutro baixo
        var score = containment * precision;

        // Bônus de contexto do bloco (desambigua irmãos, não domina).
        if (ctxTokens.Count > 0)
        {
            var ctxHit = ctxTokens.Count(t => docSet.Contains(t));
            score += 0.05 * ctxHit / ctxTokens.Count;
        }
        return Math.Min(score, 1.0);
    }

    /// <summary>Tokens da documentation do nó, sem boilerplate (cacheado).</summary>
    public static IReadOnlyList<string> DocTokens(XsdLeiauteNode node)
    {
        if (_docTokenCache.TryGetValue(node.XPath, out var cached)) return cached;
        var doc = Boilerplate.Replace(node.Documentation, " ");
        var tokens = Tokens(doc);
        _docTokenCache[node.XPath] = tokens;
        return tokens;
    }

    private static readonly Dictionary<string, IReadOnlyList<string>> _docTokenCache =
        new(StringComparer.Ordinal);

    /// <summary>Tokens de um nó: documentation (sem boilerplate) + nome local minúsculo.</summary>
    public static IReadOnlyList<string> NodeTokens(XsdLeiauteNode node)
    {
        var tokens = new List<string>(DocTokens(node));
        var name = node.Name.ToLowerInvariant();
        if (name.Length > 1 && !tokens.Contains(name)) tokens.Add(name);
        return tokens;
    }

    /// <summary>
    /// Match tolerante a typo do XSD ("diferemento" × "diferimento"):
    /// edit-distance ≤ 1, só para tokens ≥ 8 chars (evita falsos positivos).
    /// </summary>
    private static bool FuzzyIn(string token, IReadOnlyList<string> pool)
    {
        if (token.Length < 8) return false;
        foreach (var p in pool)
        {
            if (p.Length < 8 || Math.Abs(p.Length - token.Length) > 1) continue;
            if (EditDistanceAtMost1(token, p)) return true;
        }
        return false;
    }

    private static bool EditDistanceAtMost1(string a, string b)
    {
        if (a.Length == b.Length)
        {
            var diff = 0;
            for (var i = 0; i < a.Length; i++)
                if (a[i] != b[i] && ++diff > 1) return false;
            return true;
        }
        // Comprimentos diferem em 1: uma inserção/deleção.
        var (s, l) = a.Length < b.Length ? (a, b) : (b, a);
        var si = 0; var li = 0; var edits = 0;
        while (si < s.Length && li < l.Length)
        {
            if (s[si] == l[li]) { si++; li++; continue; }
            if (++edits > 1) return false;
            li++;   // pula o char extra do maior
        }
        return true;
    }

    private static double IdfOf(IReadOnlyDictionary<string, double> idf, string token) =>
        idf.TryGetValue(token, out var v) ? v : 2.0;   // token inédito = raro = peso alto

    // Fold de acentos com mapa explícito — o projeto roda com
    // InvariantGlobalization=true, onde String.Normalize vira no-op (mesmo
    // racional do TclGenerator).
    private static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            sb.Append(ch switch
            {
                'á' or 'à' or 'â' or 'ã' or 'ä' or 'å' => 'a',
                'Á' or 'À' or 'Â' or 'Ã' or 'Ä' or 'Å' => 'A',
                'é' or 'è' or 'ê' or 'ë' => 'e',
                'É' or 'È' or 'Ê' or 'Ë' => 'E',
                'í' or 'ì' or 'î' or 'ï' => 'i',
                'Í' or 'Ì' or 'Î' or 'Ï' => 'I',
                'ó' or 'ò' or 'ô' or 'õ' or 'ö' => 'o',
                'Ó' or 'Ò' or 'Ô' or 'Õ' or 'Ö' => 'O',
                'ú' or 'ù' or 'û' or 'ü' => 'u',
                'Ú' or 'Ù' or 'Û' or 'Ü' => 'U',
                'ç' => 'c', 'Ç' => 'C',
                'ñ' => 'n', 'Ñ' => 'N',
                _ => ch
            });
        }
        return sb.ToString();
    }
}
