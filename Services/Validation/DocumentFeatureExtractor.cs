using LayoutParserApi.Models.Configuration;

using System.Text.Json;

namespace LayoutParserApi.Services.Validation
{
    /// <summary>
    /// Extrator ÚNICO de features numéricas de documentos posicionais.
    /// Compartilhado entre <c>DocumentMLValidationService</c> (que grava os padrões em
    /// MLData/DocumentPatterns) e <c>DocumentAnomalyDetectorService</c> (que pontua por z-score),
    /// garantindo que histórico e documento novo usem EXATAMENTE as mesmas features.
    /// </summary>
    public static class DocumentFeatureExtractor
    {
        /// <summary>
        /// Extrai as features de um documento. Mantém as MESMAS chaves e semântica
        /// historicamente persistidas em <c>DocumentPattern.Features</c>.
        /// </summary>
        /// <param name="documentContent">Conteúdo bruto do documento posicional.</param>
        /// <param name="expectedLineLength">
        /// Tamanho de linha esperado — resolva via <see cref="LineLengthResolver"/>; o default
        /// legado só existe para call-sites historicamente incondicionais.
        /// </param>
        public static Dictionary<string, object> Extract(string documentContent, int expectedLineLength = LineLengthResolver.LegacyDefaultLineLength)
        {
            var features = new Dictionary<string, object>();

            // ✅ Guarda mínima contra divisão por zero (sem mudar o resultado para valores válidos)
            if (expectedLineLength <= 0)
                expectedLineLength = LineLengthResolver.LegacyDefaultLineLength;

            features["totalLength"] = documentContent.Length;
            features["lineCount"] = documentContent.Length / expectedLineLength;
            features["hasHeader"] = documentContent.StartsWith("HEADER");
            features["averageLineLength"] = documentContent.Length / (documentContent.Length / (double)expectedLineLength);

            // Contar tipos de caracteres
            features["numericCharCount"] = documentContent.Count(char.IsDigit);
            features["alphaCharCount"] = documentContent.Count(char.IsLetter);
            features["spaceCharCount"] = documentContent.Count(char.IsWhiteSpace);

            return features;
        }

        /// <summary>
        /// Converte um valor de feature em double de forma tolerante.
        /// Necessário porque padrões carregados do disco chegam como <see cref="JsonElement"/>
        /// (Features é <c>Dictionary&lt;string, object&gt;</c>), enquanto features recém-extraídas
        /// chegam como int/double/bool nativos. Booleanos viram 0/1 para entrar na distribuição.
        /// </summary>
        public static bool TryToDouble(object? value, out double result)
        {
            result = 0;

            switch (value)
            {
                case null:
                    return false;

                case bool b:
                    result = b ? 1 : 0;
                    return true;

                case JsonElement element:
                    switch (element.ValueKind)
                    {
                        case JsonValueKind.Number when element.TryGetDouble(out var d):
                            result = d;
                            break;
                        case JsonValueKind.True:
                            result = 1;
                            break;
                        case JsonValueKind.False:
                            result = 0;
                            break;
                        default:
                            return false;
                    }
                    break;

                case IConvertible convertible:
                    try
                    {
                        result = convertible.ToDouble(System.Globalization.CultureInfo.InvariantCulture);
                    }
                    catch
                    {
                        return false;
                    }
                    break;

                default:
                    return false;
            }

            // NaN/Infinity não entram na distribuição (podem surgir de documentos vazios legados)
            return !double.IsNaN(result) && !double.IsInfinity(result);
        }
    }
}
