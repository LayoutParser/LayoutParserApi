using LayoutParserApi.Services.Transformation.Models;

namespace LayoutParserApi.Services.Transformation
{
    /// <summary>
    /// Serviço para comparar padrões aprendidos e sugerir melhorias
    /// </summary>
    public class PatternComparisonService
    {
        private readonly ILogger<PatternComparisonService> _logger;

        public PatternComparisonService(ILogger<PatternComparisonService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Compara padrão gerado com padrões aprendidos e calcula similaridade
        /// </summary>
        public double CalculateSimilarity(LearnedPattern generated, LearnedPattern learned)
        {
            if (generated.Type != learned.Type)
                return 0.0;

            var similarity = 0.0;

            // Comparar padrões
            similarity += CalculatePatternSimilarity(generated.Pattern, learned.Pattern) * 0.4;

            // Comparar metadados
            similarity += CalculateMetadataSimilarity(generated.Metadata, learned.Metadata) * 0.3;

            // Considerar confiança
            similarity += (generated.Confidence + learned.Confidence) / 2.0 * 0.3;

            return Math.Min(1.0, similarity);
        }

        /// <summary>
        /// Calcula similaridade entre dois padrões de string
        /// </summary>
        private double CalculatePatternSimilarity(string pattern1, string pattern2)
        {
            if (string.IsNullOrEmpty(pattern1) || string.IsNullOrEmpty(pattern2))
                return 0.0;

            if (pattern1 == pattern2)
                return 1.0;

            // Usar algoritmo de Levenshtein simplificado
            var distance = LevenshteinDistance(pattern1, pattern2);
            var maxLength = Math.Max(pattern1.Length, pattern2.Length);

            if (maxLength == 0)
                return 1.0;

            return 1.0 - ((double)distance / maxLength);
        }

        /// <summary>
        /// Calcula distância de Levenshtein entre duas strings
        /// </summary>
        private int LevenshteinDistance(string s, string t)
        {
            if (string.IsNullOrEmpty(s))
                return string.IsNullOrEmpty(t) ? 0 : t.Length;

            if (string.IsNullOrEmpty(t))
                return s.Length;

            int n = s.Length;
            int m = t.Length;
            int[,] d = new int[n + 1, m + 1];

            for (int i = 0; i <= n; d[i, 0] = i++) { }
            for (int j = 0; j <= m; d[0, j] = j++) { }

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    int cost = (t[j - 1] == s[i - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),d[i - 1, j - 1] + cost);
                }
            }

            return d[n, m];
        }

        /// <summary>
        /// Calcula similaridade entre metadados
        /// </summary>
        private double CalculateMetadataSimilarity(
            Dictionary<string, object> metadata1,
            Dictionary<string, object> metadata2)
        {
            if (metadata1 == null || metadata2 == null || !metadata1.Any() || !metadata2.Any())
                return 0.0;

            var commonKeys = metadata1.Keys.Intersect(metadata2.Keys).ToList();
            if (!commonKeys.Any())
                return 0.0;

            var matches = 0;
            foreach (var key in commonKeys)
            {
                var value1 = metadata1[key]?.ToString();
                var value2 = metadata2[key]?.ToString();

                if (value1 == value2)
                    matches++;
            }

            return (double)matches / Math.Max(metadata1.Count, metadata2.Count);
        }

        /// <summary>
        /// Encontra padrões mais similares a um padrão gerado
        /// </summary>
        public List<SimilarityResult> FindMostSimilarPatterns( LearnedPattern generatedPattern, List<LearnedPattern> learnedPatterns, double threshold = 0.7)
        {
            var similarities = learnedPatterns
                .Select(lp => new SimilarityResult
                {
                    Pattern = lp,
                    Similarity = CalculateSimilarity(generatedPattern, lp)
                }).Where(sr => sr.Similarity >= threshold).OrderByDescending(sr => sr.Similarity).ToList();

            return similarities;
        }

        /// <summary>
        /// Sugere melhorias para um padrão gerado baseado em padrões aprendidos
        /// </summary>
        public List<string> SuggestImprovements(
            LearnedPattern generatedPattern,
            List<LearnedPattern> learnedPatterns)
        {
            var suggestions = new List<string>();

            var similarPatterns = FindMostSimilarPatterns(generatedPattern, learnedPatterns, 0.6);

            if (!similarPatterns.Any())
            {
                suggestions.Add("Nenhum padrão similar encontrado. Padrão pode ser único ou novo.");
                return suggestions;
            }

            var bestMatch = similarPatterns.First();

            // Sugerir melhorias baseadas no melhor match
            if (bestMatch.Similarity < 0.8)
                suggestions.Add($"Padrão similar encontrado com {bestMatch.Similarity:P0} de similaridade. Considere revisar.");

            // Comparar confiança
            if (generatedPattern.Confidence < bestMatch.Pattern.Confidence)
                suggestions.Add($"Padrão aprendido tem maior confiança ({bestMatch.Pattern.Confidence:P0}). Considere usar como referência.");
            
            // Comparar frequência
            if (generatedPattern.Frequency < bestMatch.Pattern.Frequency)
                suggestions.Add($"Padrão aprendido aparece mais frequentemente ({bestMatch.Pattern.Frequency} vezes). Pode ser mais comum.");

            return suggestions;
        }
    }
}