using LayoutParserApi.Models.Generation;
using LayoutParserApi.Services.Generation.TxtGenerator.Models;

namespace LayoutParserApi.Services.Generation.TxtGenerator.Parsers
{
    /// <summary>
    /// Parser para extrair regras de negócio do Excel
    /// </summary>
    public class ExcelRulesParser
    {
        private readonly ILogger<ExcelRulesParser> _logger;

        public ExcelRulesParser(ILogger<ExcelRulesParser> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Aplica regras do Excel aos campos do layout
        /// </summary>
        public void ApplyExcelRules(FileLayout fileLayout, ExcelDataContext excelContext)
        {
            if (excelContext == null || fileLayout == null)
                return;

            foreach (var record in fileLayout.Records)
                foreach (var field in record.Fields)
                    ApplyFieldRules(field, excelContext);

            _logger.LogInformation("Regras do Excel aplicadas ao layout");
        }

        private void ApplyFieldRules(FieldDefinition field, ExcelDataContext excelContext)
        {
            // Buscar coluna correspondente no Excel pelo nome do campo
            var matchingColumn = FindMatchingColumn(field.Name, excelContext);

            if (matchingColumn == null)
                return;

            // Extrair descrição
            if (excelContext.ColumnData.ContainsKey(matchingColumn))
            {
                var samples = excelContext.ColumnData[matchingColumn].Where(v => !string.IsNullOrWhiteSpace(v)).Take(5).ToList();

                if (samples.Any())
                {
                    field.Example = samples.First();
                    field.Description = $"Exemplos: {string.Join(", ", samples)}";
                }
            }

            // Detectar se é fixo ou random
            field.IsFixed = DetectIfFixed(matchingColumn, excelContext);

            // Extrair domínio (valores possíveis)
            if (excelContext.ColumnData.ContainsKey(matchingColumn))
            {
                var distinctValues = excelContext.ColumnData[matchingColumn].Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().Take(20).ToList();

                if (distinctValues.Count <= 10) // Se poucos valores distintos, pode ser um domínio
                    field.Domain = string.Join(",", distinctValues);
            }

            // Detectar tipo de dado do Excel
            if (excelContext.ColumnTypes.ContainsKey(matchingColumn))
            {
                var excelType = excelContext.ColumnTypes[matchingColumn];
                if (!string.IsNullOrEmpty(excelType))
                {
                    // Refinar tipo baseado no Excel
                    if (excelType == "decimal" && field.DataType == "string")
                        field.DataType = "decimal";
                    else if (excelType == "datetime" && field.DataType == "string")
                        field.DataType = "date";
                }
            }
        }

        private string FindMatchingColumn(string fieldName, ExcelDataContext excelContext)
        {
            if (excelContext.Headers == null)
                return null;

            var fieldNameLower = fieldName.ToLowerInvariant();

            // Match exato
            var exactMatch = excelContext.Headers.FirstOrDefault(h => h.Equals(fieldName, StringComparison.OrdinalIgnoreCase));
            if (exactMatch != null)
                return exactMatch;

            // Match parcial
            var partialMatch = excelContext.Headers.FirstOrDefault(h => h.ToLowerInvariant().Contains(fieldNameLower) || fieldNameLower.Contains(h.ToLowerInvariant()));
            if (partialMatch != null)
                return partialMatch;

            // Match por palavras-chave
            var keywords = new[] { "cnpj", "cpf", "nome", "data", "valor", "quantidade", "codigo", "email" };
            foreach (var keyword in keywords)
            {
                if (fieldNameLower.Contains(keyword))
                {
                    var keywordMatch = excelContext.Headers.FirstOrDefault(h => h.ToLowerInvariant().Contains(keyword));
                    if (keywordMatch != null)
                        return keywordMatch;
                }
            }

            return null;
        }

        private bool DetectIfFixed(string columnName, ExcelDataContext excelContext)
        {
            if (!excelContext.ColumnData.ContainsKey(columnName))
                return false;

            var values = excelContext.ColumnData[columnName].Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().ToList();

            // Se todos os valores são iguais ou há muito poucos valores distintos, pode ser fixo
            if (values.Count == 1)
                return true;

            if (values.Count <= 3 && excelContext.ColumnData[columnName].Count > 10)
                return true;

            return false;
        }
    }
}