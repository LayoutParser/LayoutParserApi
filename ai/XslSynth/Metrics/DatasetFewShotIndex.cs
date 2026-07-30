using System.Text.RegularExpressions;

namespace XslSynth.Metrics;

/// <summary>Um exemplo few-shot recuperado para o job de métricas em lote: o par TCL→XSLT
/// mais parecido com o caso de teste, mais o score de similaridade que gerou a recuperação.</summary>
public sealed record DatasetFewShotMatch(DatasetPair Pair, double Similarity);

/// <summary>
/// Índice de recuperação por similaridade TF-IDF/cosseno sobre o dataset held-out
/// (<c>dataset_pairs_filtered_v2.jsonl</c>) — reproduz em C#, dentro do projeto (não mais
/// solto em Python no scratchpad), a mesma técnica do spike de 2026-07-29
/// (ver memória <c>rag-spike-cpu-throughput-2026-07-29</c>): TF-IDF sobre os tokens do
/// TCL de entrada, held-out por CASO (nunca recupera a si mesmo).
///
/// Diferente do <see cref="XslSynth.Synthesis.FewShotIndex"/> (que indexa <c>MapperRule</c>
/// da DSL Sysmiddle por traço estrutural): aqui a unidade é o PAR inteiro
/// TCL-schema→XSLT-completo do dataset de fine-tuning, e a similaridade é lexical sobre
/// o esquema TCL (nomes de campo/bloco), não sobre traços de regra condicional.
/// </summary>
public sealed class DatasetFewShotIndex
{
    private readonly IReadOnlyList<DatasetPair> _pairs;
    private readonly Dictionary<string, Dictionary<string, double>> _tfidfPorId; // Id → (token → peso tf-idf)
    private readonly Dictionary<string, double> _normaPorId;                      // Id → norma L2 do vetor

    private static readonly Regex TokenRx = new(@"[A-Za-z][A-Za-z0-9_]{1,}", RegexOptions.Compiled);

    private DatasetFewShotIndex(IReadOnlyList<DatasetPair> pairs,
        Dictionary<string, Dictionary<string, double>> tfidf, Dictionary<string, double> normas)
    {
        _pairs = pairs;
        _tfidfPorId = tfidf;
        _normaPorId = normas;
    }

    public int Count => _pairs.Count;

    /// <summary>Constrói o índice TF-IDF sobre TODO o corpus (IDF é global — o held-out
    /// acontece na RECUPERAÇÃO, excluindo o próprio caso, não na construção do vocabulário;
    /// mesmo princípio do spike anterior).</summary>
    public static DatasetFewShotIndex Build(IReadOnlyList<DatasetPair> pairs)
    {
        var tokensPorId = pairs.ToDictionary(p => p.Id, p => Tokenize(p.InputMapTcl));

        // DF: em quantos documentos cada token aparece.
        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var tokens in tokensPorId.Values)
            foreach (var t in tokens.Keys)
                df[t] = df.GetValueOrDefault(t) + 1;

        var n = pairs.Count;
        var tfidf = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        var normas = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var (id, tf) in tokensPorId)
        {
            var vetor = new Dictionary<string, double>(StringComparer.Ordinal);
            var totalTokens = tf.Values.Sum();
            foreach (var (token, freq) in tf)
            {
                // idf suavizado (+1 no denominador e no log) — evita divisão por zero e
                // domínio explosivo de tokens raríssimos num corpus pequeno (54 docs).
                var idf = Math.Log((double)(n + 1) / (df[token] + 1)) + 1;
                var tfNorm = (double)freq / Math.Max(1, totalTokens);
                vetor[token] = tfNorm * idf;
            }
            tfidf[id] = vetor;
            normas[id] = Math.Sqrt(vetor.Values.Sum(v => v * v));
        }

        return new DatasetFewShotIndex(pairs, tfidf, normas);
    }

    /// <summary>
    /// Recupera até <paramref name="k"/> pares mais parecidos com o caso de teste, por
    /// cosseno TF-IDF sobre o TCL de entrada — SEMPRE excluindo o próprio caso do pool
    /// (regra de held-out, igual ao spike anterior). Preferência leve por mesmo
    /// <c>doc_type</c> é IMPLÍCITA na similaridade lexical (schemas do mesmo tipo de
    /// documento compartilham nomes de campo/bloco), não um filtro rígido — assim um CTe
    /// sem par do mesmo tipo ainda recebe o few-shot mais próximo disponível.
    /// </summary>
    public IReadOnlyList<DatasetFewShotMatch> Retrieve(DatasetPair query, int k = 3)
    {
        if (k <= 0) return Array.Empty<DatasetFewShotMatch>();

        return _pairs
            .Where(p => p.Id != query.Id)
            .Select(p => new DatasetFewShotMatch(p, Cosine(query.Id, p.Id)))
            .Where(m => m.Similarity > 0)
            .OrderByDescending(m => m.Similarity)
            .ThenBy(m => m.Pair.Id, StringComparer.Ordinal) // desempate determinístico
            .Take(k)
            .ToList();
    }

    private double Cosine(string idA, string idB)
    {
        if (!_tfidfPorId.TryGetValue(idA, out var a) || !_tfidfPorId.TryGetValue(idB, out var b)) return 0;
        var normaA = _normaPorId[idA];
        var normaB = _normaPorId[idB];
        if (normaA == 0 || normaB == 0) return 0;

        // Itera o menor vetor — corpus pequeno, mas mantém o hábito de custo controlado.
        var (menor, maior) = a.Count <= b.Count ? (a, b) : (b, a);
        var dot = menor.Sum(kv => maior.TryGetValue(kv.Key, out var v) ? kv.Value * v : 0);
        return dot / (normaA * normaB);
    }

    private static Dictionary<string, int> Tokenize(string texto)
    {
        var dict = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in TokenRx.Matches(texto))
        {
            var t = m.Value.ToLowerInvariant();
            dict[t] = dict.GetValueOrDefault(t) + 1;
        }
        return dict;
    }
}
