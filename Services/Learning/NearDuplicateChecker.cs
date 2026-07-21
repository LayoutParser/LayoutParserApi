using System.Text.RegularExpressions;
using LayoutParserApi.Services.Learning.Models;

namespace LayoutParserApi.Services.Learning
{
    /// <summary>
    /// Checagem de near-duplicate (item 4.4 do dispatch de IA,
    /// docs/architecture/ai-roadmap-dispatch.md, 2026-07-21) - requisito de design
    /// OBRIGATÓRIO para QUALQUER saída "sintética" que entrar no índice RAG (item 4.1/4.2),
    /// em qualquer nível de sofisticação futuro (não só o Nível 1 de hoje). Este projeto é
    /// material de TCC, que circula mais solto que dado real controlado - "sintético" não
    /// pode mascarar valor real copiado verbatim de um documento de cliente.
    ///
    /// Técnica: similaridade de Jaccard sobre SHINGLES de palavras (n-gramas de N palavras
    /// consecutivas, após normalização - minúsculas, acentos preservados, pontuação fora).
    /// Escolha deliberada: 100% determinístico, zero dependência externa/modelo de embedding
    /// (o servidor de produção não tem GPU - ver memória de @lp-architect
    /// production-server-hardware - e mesmo em CPU um embedding decente seria custo
    /// desproporcional para um filtro de primeira linha). Não é o estado da arte em detecção
    /// de plágio, mas é auditável, rápido e suficiente para pegar o caso que importa aqui:
    /// texto sintético que reproduz frases/trechos GRANDES de um documento real verbatim ou
    /// quase verbatim (que é exatamente o que shingles de 3+ palavras capturam bem).
    /// </summary>
    public class NearDuplicateChecker
    {
        private static readonly Regex NaoPalavra = new(@"[^\p{L}\p{Nd}]+", RegexOptions.Compiled);

        /// <summary>Tamanho do shingle (nº de palavras consecutivas por token de comparação). 3 é um equilíbrio comum (bigrama é frouxo demais, shingles grandes demais viram sensíveis a qualquer edição pontual).</summary>
        public int ShingleSize { get; init; } = 3;

        /// <summary>Similaridade acima da qual um candidato é tratado como near-duplicate. Conservador de propósito (falso positivo aqui só custa uma revisão manual a mais; falso negativo vaza dado real).</summary>
        public double LimiarPadrao { get; init; } = 0.6;

        /// <summary>Similaridade de Jaccard (0..1) entre dois textos, sobre o conjunto de shingles de palavras.</summary>
        public double Similarity(string a, string b)
        {
            var shinglesA = Shingles(a);
            var shinglesB = Shingles(b);
            if (shinglesA.Count == 0 || shinglesB.Count == 0) return 0.0;

            var intersecao = shinglesA.Intersect(shinglesB).Count();
            var uniao = shinglesA.Union(shinglesB).Count();
            return uniao == 0 ? 0.0 : (double)intersecao / uniao;
        }

        /// <summary>
        /// Compara <paramref name="candidato"/> (texto "sintético" recém-gerado) contra CADA
        /// item de <paramref name="corpusReal"/> e devolve a maior similaridade encontrada.
        /// Degrade gracioso: corpus vazio/nulo → MaiorSimilaridade=0, EhNearDuplicate=false
        /// (nada para comparar - não é o mesmo que "aprovado", é "não verificável").
        /// </summary>
        public NearDuplicateResult CheckAgainstCorpus(
            string candidato, IEnumerable<string>? corpusReal, double? limiar = null)
        {
            var result = new NearDuplicateResult();
            if (string.IsNullOrWhiteSpace(candidato) || corpusReal is null)
                return result;

            var shinglesCandidato = Shingles(candidato);
            if (shinglesCandidato.Count == 0) return result;

            var idx = -1;
            var melhor = 0.0;
            var i = 0;
            foreach (var real in corpusReal)
            {
                var shinglesReal = Shingles(real);
                if (shinglesReal.Count > 0)
                {
                    var intersecao = shinglesCandidato.Intersect(shinglesReal).Count();
                    var uniao = shinglesCandidato.Union(shinglesReal).Count();
                    var sim = uniao == 0 ? 0.0 : (double)intersecao / uniao;
                    if (sim > melhor)
                    {
                        melhor = sim;
                        idx = i;
                    }
                }
                i++;
            }

            result.MaiorSimilaridade = melhor;
            result.IndiceMaisParecido = idx;
            result.EhNearDuplicate = melhor >= (limiar ?? LimiarPadrao);
            return result;
        }

        private HashSet<string> Shingles(string? texto)
        {
            if (string.IsNullOrWhiteSpace(texto)) return new HashSet<string>(StringComparer.Ordinal);

            var palavras = NaoPalavra.Split(texto.ToLowerInvariant())
                .Where(p => p.Length > 0)
                .ToArray();

            if (palavras.Length < ShingleSize)
                // Texto curto demais para formar um shingle completo: usa o texto inteiro
                // como shingle único (comparação ainda funciona, só fica mais grosseira).
                return palavras.Length == 0
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal) { string.Join(' ', palavras) };

            var shingles = new HashSet<string>(StringComparer.Ordinal);
            for (var s = 0; s <= palavras.Length - ShingleSize; s++)
                shingles.Add(string.Join(' ', palavras.Skip(s).Take(ShingleSize)));
            return shingles;
        }
    }
}
