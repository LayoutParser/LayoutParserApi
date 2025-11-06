using System.Collections.Generic;

namespace LayoutParserApi.Services.Generation.Interfaces
{
    /// <summary>
    /// Interface para geradores de valores com regras de consistência
    /// </summary>
    public interface IValueGenerator
    {
        /// <summary>
        /// Gera um valor monetário com regras de consistência
        /// </summary>
        string GenerateMonetaryValue(int length, decimal? targetTotal = null, List<decimal> itemValues = null);

        /// <summary>
        /// Gera um valor numérico com formatação específica
        /// </summary>
        string GenerateNumericValue(int length, int? minValue = null, int? maxValue = null, bool padWithZeros = true);

        /// <summary>
        /// Gera um valor de texto realista a partir de exemplos do Excel
        /// </summary>
        string GenerateRealisticText(string fieldName, int length, List<string> excelSamples = null);

        /// <summary>
        /// Valida consistência de valores (ex: total = soma dos itens)
        /// </summary>
        bool ValidateConsistency(Dictionary<string, decimal> values, Dictionary<string, string> rules);
    }
}

