using LayoutParserApi.Models.Analysis;
using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Enums;
using LayoutParserApi.Services.Generation.Interfaces;

using System.Text.RegularExpressions;

namespace LayoutParserApi.Services.Generation.Implementations
{
    public class LayoutAnalysisService : ILayoutAnalysisService
    {
        private readonly ILogger<LayoutAnalysisService> _logger;


        public LayoutAnalysisService(ILogger<LayoutAnalysisService> logger)
        {
            _logger = logger;
        }

        public async Task<LayoutAnalysisResult> AnalyzeLayoutForAIAsync(Layout layout)
        {
            var result = new LayoutAnalysisResult
            {
                LayoutType = layout.LayoutType,
                LayoutName = layout.Name,
                TotalLines = layout.Elements?.Count ?? 0
            };

            try
            {
                _logger.LogInformation("Analisando layout {LayoutName} para IA", layout.Name);

                // Extrair metadados do layout
                result.Metadata = await ExtractLayoutMetadataAsync(layout);

                // Analisar tipos de linha
                result.LineTypes = await AnalyzeLineTypesAsync(layout);

                // Extrair e analisar campos
                result.Fields = await ExtractFieldMetadataAsync(layout);
                result.TotalFields = result.Fields.Count;

                _logger.LogInformation("Análise concluída: {FieldCount} campos, {LineCount} linhas",
                    result.TotalFields, result.TotalLines);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao analisar layout {LayoutName}", layout.Name);
                throw;
            }

            return result;
        }

        public async Task<FieldPatternAnalysis> AnalyzeFieldPatternsAsync(string fieldName, List<string> sampleValues)
        {
            var analysis = new FieldPatternAnalysis
            {
                FieldName = fieldName
            };

            if (!sampleValues.Any())
                return analysis;

            try
            {
                // Analisar frequência de valores
                analysis.ValueFrequency = sampleValues.GroupBy(v => v).ToDictionary(g => g.Key, g => g.Count());

                // Identificar valores mais comuns
                analysis.CommonValues = analysis.ValueFrequency.OrderByDescending(kv => kv.Value).Take(10).Select(kv => kv.Key).ToList();

                // Detectar padrões
                analysis.DetectedPattern = DetectPattern(sampleValues);
                analysis.SuggestedGenerationStrategy = SuggestGenerationStrategy(analysis.DetectedPattern, fieldName);

                // Metadados adicionais
                analysis.PatternMetadata = new Dictionary<string, object>
                {
                    ["totalSamples"] = sampleValues.Count,
                    ["uniqueValues"] = analysis.ValueFrequency.Count,
                    ["mostCommonValue"] = analysis.CommonValues.FirstOrDefault(),
                    ["mostCommonFrequency"] = analysis.ValueFrequency.Values.Max()
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao analisar padrões do campo {FieldName}", fieldName);
            }

            return analysis;
        }

        public async Task<List<FieldAnalysis>> ExtractFieldMetadataAsync(Layout layout)
        {
            var fields = new List<FieldAnalysis>();

            if (layout?.Elements == null)
                return fields;

            foreach (var lineElement in layout.Elements)
            {
                var lineFields = await ExtractFieldsFromLineElementAsync(lineElement);
                fields.AddRange(lineFields);
            }

            return fields;
        }

        public async Task<Dictionary<string, object>> ExtractLayoutMetadataAsync(Layout layout)
        {
            var metadata = new Dictionary<string, object>
            {
                ["layoutGuid"] = layout.LayoutGuid,
                ["layoutType"] = layout.LayoutType,
                ["name"] = layout.Name,
                ["description"] = layout.Description,
                ["limitOfCharacters"] = layout.LimitOfCaracters,
                ["totalElements"] = layout.Elements?.Count ?? 0,
                ["hasDelimiter"] = layout.Delimiter > 0,
                ["hasEscape"] = !string.IsNullOrEmpty(layout.Escape),
                ["hasInitializerLine"] = !string.IsNullOrEmpty(layout.InitializerLine),
                ["hasFinisherLine"] = !string.IsNullOrEmpty(layout.FinisherLine),
                ["withBreakLines"] = layout.WithBreakLines
            };

            return metadata;
        }

        private async Task<List<LineTypeAnalysis>> AnalyzeLineTypesAsync(Layout layout)
        {
            var lineTypes = new List<LineTypeAnalysis>();

            foreach (var lineElement in layout.Elements)
            {
                var lineAnalysis = new LineTypeAnalysis
                {
                    Name = lineElement.Name,
                    InitialValue = lineElement.InitialValue,
                    MinimalOccurrence = lineElement.MinimalOccurrence,
                    MaximumOccurrence = lineElement.MaximumOccurrence,
                    IsRequired = lineElement.IsRequired,
                    Fields = await ExtractFieldsFromLineElementAsync(lineElement)
                };

                // Calcular comprimento total da linha
                lineAnalysis.TotalLength = CalculateLineLength(lineElement);

                lineTypes.Add(lineAnalysis);
            }

            return lineTypes;
        }

        private async Task<List<FieldAnalysis>> ExtractFieldsFromLineElementAsync(LineElement lineElement)
        {
            var fields = new List<FieldAnalysis>();

            if (lineElement?.Elements == null)
                return fields;

            foreach (var elementJson in lineElement.Elements)
            {
                try
                {
                    var field = Newtonsoft.Json.JsonConvert.DeserializeObject<FieldElement>(elementJson);
                    if (field != null && !string.IsNullOrEmpty(field.Name))
                    {
                        var fieldAnalysis = new FieldAnalysis
                        {
                            Name = field.Name,
                            DataType = InferDataType(field),
                            Length = field.LengthField,
                            Pattern = InferPattern(field),
                            IsSequential = field.IsSequential,
                            IsRequired = field.IsRequired,
                            Alignment = field.AlignmentType.ToString(),
                            StartPosition = field.StartValue,
                            Description = field.Description,
                            LineName = lineElement.Name,
                            ValidationRules = BuildValidationRules(field)
                        };

                        fields.Add(fieldAnalysis);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Erro ao processar elemento JSON na linha {LineName}", lineElement.Name);
                }
            }

            return fields;
        }

        private int CalculateLineLength(LineElement lineElement)
        {
            var totalLength = 0;

            // Adicionar comprimento do InitialValue
            if (!string.IsNullOrEmpty(lineElement.InitialValue))
                totalLength += lineElement.InitialValue.Length;

            // Adicionar comprimento dos campos
            if (lineElement.Elements != null)
            {
                foreach (var elementJson in lineElement.Elements)
                {
                    try
                    {
                        var field = Newtonsoft.Json.JsonConvert.DeserializeObject<FieldElement>(elementJson);
                        if (field != null)
                            totalLength += field.LengthField;
                    }
                    catch { }
                }
            }

            return totalLength;
        }

        private string InferDataType(FieldElement field)
        {
            var name = field.Name.ToLower();

            if (name.Contains("cnpj")) return "cnpj";
            if (name.Contains("cpf")) return "cpf";
            if (name.Contains("data") || name.Contains("date")) return "date";
            if (name.Contains("hora") || name.Contains("time")) return "datetime";
            if (name.Contains("valor") || name.Contains("preco") || name.Contains("amount")) return "decimal";
            if (name.Contains("quantidade") || name.Contains("qtd")) return "integer";
            if (name.Contains("email")) return "email";
            if (name.Contains("telefone") || name.Contains("phone")) return "phone";
            if (name.Contains("sequencia") || name.Contains("sequence")) return "sequence";

            return "string";
        }

        private string InferPattern(FieldElement field)
        {
            var dataType = InferDataType(field);

            return dataType switch
            {
                "cnpj" => "##############",
                "cpf" => "###########",
                "date" => "########",
                "datetime" => "########T##:##:##",
                "decimal" => new string('#', field.LengthField),
                "integer" => new string('#', field.LengthField),
                "email" => "###@###.###",
                "phone" => "###########",
                "sequence" => "######",
                _ => new string('#', field.LengthField)
            };
        }

        private string BuildValidationRules(FieldElement field)
        {
            var rules = new List<string>();

            if (field.IsRequired)
                rules.Add("required");

            if (field.IsSequential)
                rules.Add("sequential");

            if (field.LengthField > 0)
                rules.Add($"length:{field.LengthField}");

            if (field.IsCaseSensitiveValue)
                rules.Add("case_sensitive");

            if (field.RemoveWhiteSpaceType != RemoveWhiteSpaceType.None)
                rules.Add($"remove_whitespace:{field.RemoveWhiteSpaceType}");

            return string.Join(",", rules);
        }

        private string DetectPattern(List<string> sampleValues)
        {
            if (!sampleValues.Any())
                return "unknown";

            var firstValue = sampleValues.First();

            // Detectar padrões comuns
            if (IsNumericPattern(firstValue))
                return "numeric";

            if (IsDatePattern(firstValue))
                return "date";

            if (IsEmailPattern(firstValue))
                return "email";

            if (IsCnpjCpfPattern(firstValue))
                return "cnpj_cpf";

            if (IsSequentialPattern(sampleValues))
                return "sequential";

            if (IsFixedLengthPattern(sampleValues))
                return "fixed_length";

            return "text";
        }

        private bool IsNumericPattern(string value)
        {
            return decimal.TryParse(value.Replace(",", "."), out _);
        }

        private bool IsDatePattern(string value)
        {
            return DateTime.TryParse(value, out _) || Regex.IsMatch(value, @"^\d{8}$") || Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}$");
        }

        private bool IsEmailPattern(string value)
        {
            return Regex.IsMatch(value, @"^[^@\s]+@[^@\s]+\.[^@\s]+$");
        }

        private bool IsCnpjCpfPattern(string value)
        {
            var clean = Regex.Replace(value, @"[^\d]", "");
            return clean.Length == 11 || clean.Length == 14;
        }

        private bool IsSequentialPattern(List<string> values)
        {
            if (values.Count < 2) return false;

            var numericValues = values.Where(v => int.TryParse(v, out _)).Select(int.Parse).OrderBy(x => x).ToList();

            if (numericValues.Count < 2) return false;

            var differences = numericValues.Skip(1).Zip(numericValues, (a, b) => a - b).ToList();

            return differences.All(d => d == differences.First());
        }

        private bool IsFixedLengthPattern(List<string> values)
        {
            if (!values.Any()) return false;

            var firstLength = values.First().Length;
            return values.All(v => v.Length == firstLength);
        }

        private string SuggestGenerationStrategy(string pattern, string fieldName)
        {
            return pattern switch
            {
                "numeric" => "generate_numeric_range",
                "date" => "generate_date_range",
                "email" => "generate_email_pattern",
                "cnpj_cpf" => "generate_valid_cnpj_cpf",
                "sequential" => "generate_sequential",
                "fixed_length" => "generate_fixed_length_text",
                _ => "generate_random_text"
            };
        }
    }
}