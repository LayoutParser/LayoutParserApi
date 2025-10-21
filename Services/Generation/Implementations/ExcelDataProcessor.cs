using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Generation;
using LayoutParserApi.Services.Generation.Interfaces;
using System.Text.RegularExpressions;

namespace LayoutParserApi.Services.Generation.Implementations
{
    public class ExcelDataProcessor : IExcelDataProcessor
    {
        private readonly ILogger<ExcelDataProcessor> _logger;

        public ExcelDataProcessor(ILogger<ExcelDataProcessor> logger)
        {
            _logger = logger;
        }

        public async Task<ExcelProcessingResult> ProcessExcelFileAsync(Stream excelStream, string fileName = null)
        {
            var result = new ExcelProcessingResult();
            
            try
            {
                // Para simplificar, vamos usar uma implementação básica
                // Em produção, usar bibliotecas como EPPlus ou ClosedXML
                var content = await ReadExcelAsCsv(excelStream);
                var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                
                if (lines.Length < 2)
                {
                    result.ErrorMessage = "Arquivo Excel deve ter pelo menos 2 linhas (cabeçalho + dados)";
                    return result;
                }

                var headers = ParseCsvLine(lines[0]);
                var dataContext = new ExcelDataContext
                {
                    Headers = headers,
                    FileName = fileName ?? "unknown.xlsx",
                    RowCount = lines.Length - 1
                };

                // Processar dados
                for (int i = 1; i < lines.Length; i++)
                {
                    var values = ParseCsvLine(lines[i]);
                    for (int j = 0; j < Math.Min(headers.Count, values.Count); j++)
                    {
                        if (!dataContext.ColumnData.ContainsKey(headers[j]))
                            dataContext.ColumnData[headers[j]] = new List<string>();
                        
                        dataContext.ColumnData[headers[j]].Add(values[j]);
                    }
                }

                // Detectar tipos de colunas
                dataContext.ColumnTypes = await DetectColumnTypesAsync(dataContext);
                
                result.DataContext = dataContext;
                result.Success = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao processar arquivo Excel");
                result.ErrorMessage = $"Erro ao processar Excel: {ex.Message}";
            }

            return result;
        }

        public async Task<List<FieldMapping>> MapExcelColumnsToLayoutFieldsAsync(ExcelDataContext excelData, Layout layout)
        {
            var mappings = new List<FieldMapping>();
            
            if (excelData?.Headers == null || layout?.Elements == null)
                return mappings;

            // Extrair todos os campos do layout
            var layoutFields = new List<FieldElement>();
            foreach (var lineElement in layout.Elements)
            {
                ExtractFieldsFromLineElement(lineElement, layoutFields);
            }

            // Mapear colunas do Excel para campos do layout
            foreach (var header in excelData.Headers)
            {
                var bestMatch = FindBestFieldMatch(header, layoutFields, excelData);
                if (bestMatch != null)
                {
                    mappings.Add(bestMatch);
                }
            }

            return mappings;
        }

        public async Task<Dictionary<string, string>> DetectColumnTypesAsync(ExcelDataContext excelData)
        {
            var columnTypes = new Dictionary<string, string>();
            
            foreach (var header in excelData.Headers)
            {
                if (!excelData.ColumnData.ContainsKey(header))
                    continue;

                var values = excelData.ColumnData[header].Take(100).ToList(); // Amostra para análise
                var detectedType = DetectColumnType(values);
                columnTypes[header] = detectedType;
            }

            return columnTypes;
        }

        public async Task<List<string>> ExtractSampleValuesAsync(ExcelDataContext excelData, string columnName, int maxSamples = 10)
        {
            if (!excelData.ColumnData.ContainsKey(columnName))
                return new List<string>();

            return excelData.ColumnData[columnName]
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct()
                .Take(maxSamples)
                .ToList();
        }

        private async Task<string> ReadExcelAsCsv(Stream excelStream)
        {
            // Implementação simplificada - em produção usar EPPlus ou ClosedXML
            using var reader = new StreamReader(excelStream);
            return await reader.ReadToEndAsync();
        }

        private List<string> ParseCsvLine(string line)
        {
            // Parser CSV simples - em produção usar biblioteca robusta
            var values = new List<string>();
            var current = "";
            var inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                var c = line[i];
                
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (c == ',' && !inQuotes)
                {
                    values.Add(current.Trim());
                    current = "";
                }
                else
                {
                    current += c;
                }
            }
            
            values.Add(current.Trim());
            return values;
        }

        private string DetectColumnType(List<string> values)
        {
            if (!values.Any()) return "string";

            var nonEmptyValues = values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            if (!nonEmptyValues.Any()) return "string";

            // Verificar se é numérico
            if (nonEmptyValues.All(v => decimal.TryParse(v.Replace(",", "."), out _)))
                return "decimal";

            // Verificar se é data
            if (nonEmptyValues.All(v => DateTime.TryParse(v, out _)))
                return "datetime";

            // Verificar se é CNPJ/CPF
            if (nonEmptyValues.All(v => IsCnpjCpf(v)))
                return "cnpj_cpf";

            // Verificar se é email
            if (nonEmptyValues.All(v => IsEmail(v)))
                return "email";

            return "string";
        }

        private bool IsCnpjCpf(string value)
        {
            var clean = Regex.Replace(value, @"[^\d]", "");
            return clean.Length == 11 || clean.Length == 14;
        }

        private bool IsEmail(string value)
        {
            return Regex.IsMatch(value, @"^[^@\s]+@[^@\s]+\.[^@\s]+$");
        }

        private void ExtractFieldsFromLineElement(LineElement lineElement, List<FieldElement> fields)
        {
            if (lineElement?.Elements == null) return;

            foreach (var elementJson in lineElement.Elements)
            {
                try
                {
                    var field = Newtonsoft.Json.JsonConvert.DeserializeObject<FieldElement>(elementJson);
                    if (field != null && !string.IsNullOrEmpty(field.Name))
                    {
                        fields.Add(field);
                    }
                }
                catch
                {
                    // Ignorar elementos que não são FieldElement
                }
            }
        }

        private FieldMapping FindBestFieldMatch(string excelColumn, List<FieldElement> layoutFields, ExcelDataContext excelData)
        {
            var bestMatch = layoutFields
                .Select(field => new
                {
                    Field = field,
                    Score = CalculateMatchScore(excelColumn, field, excelData)
                })
                .Where(x => x.Score > 0.3) // Threshold mínimo
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();

            if (bestMatch == null) return null;

            return new FieldMapping
            {
                LayoutFieldName = bestMatch.Field.Name,
                ExcelColumnName = excelColumn,
                Confidence = bestMatch.Score,
                IsAutoDetected = true,
                MappingReason = $"Score: {bestMatch.Score:F2}",
                SampleMappings = excelData.ColumnData.ContainsKey(excelColumn) 
                    ? excelData.ColumnData[excelColumn].Take(3).ToList() 
                    : new List<string>()
            };
        }

        private double CalculateMatchScore(string excelColumn, FieldElement field, ExcelDataContext excelData)
        {
            var score = 0.0;
            var columnLower = excelColumn.ToLower();
            var fieldLower = field.Name.ToLower();

            // Match exato
            if (columnLower == fieldLower) score += 1.0;

            // Match parcial
            if (columnLower.Contains(fieldLower) || fieldLower.Contains(columnLower)) score += 0.7;

            // Match por palavras-chave
            var keywords = new[] { "cnpj", "cpf", "nome", "data", "valor", "quantidade", "codigo", "id" };
            foreach (var keyword in keywords)
            {
                if (columnLower.Contains(keyword) && fieldLower.Contains(keyword))
                    score += 0.5;
            }

            // Verificar compatibilidade de tipo
            if (excelData.ColumnTypes.ContainsKey(excelColumn))
            {
                var excelType = excelData.ColumnTypes[excelColumn];
                var fieldType = InferFieldType(field);
                
                if (excelType == fieldType) score += 0.3;
            }

            return Math.Min(score, 1.0);
        }

        private string InferFieldType(FieldElement field)
        {
            var name = field.Name.ToLower();
            
            if (name.Contains("cnpj") || name.Contains("cpf")) return "cnpj_cpf";
            if (name.Contains("data") || name.Contains("date")) return "datetime";
            if (name.Contains("valor") || name.Contains("preco") || name.Contains("amount")) return "decimal";
            if (name.Contains("email")) return "email";
            
            return "string";
        }
    }
}
