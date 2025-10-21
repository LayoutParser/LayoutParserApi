using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Generation;
using LayoutParserApi.Services.Generation.Interfaces;
using System.Text;

namespace LayoutParserApi.Services.Generation.Implementations
{
    public class SyntheticDataGeneratorService : ISyntheticDataGeneratorService
    {
        private readonly IAIService _aiService;
        private readonly ILogger<SyntheticDataGeneratorService> _logger;
        private readonly Random _random = new();

        public SyntheticDataGeneratorService(IAIService aiService, ILogger<SyntheticDataGeneratorService> logger)
        {
            _aiService = aiService;
            _logger = logger;
        }

        public async Task<GeneratedDataResult> GenerateSyntheticDataAsync(SyntheticDataRequest request)
        {
            var result = new GeneratedDataResult
            {
                UsedLayout = request.Layout,
                TotalRecords = request.NumberOfRecords
            };

            var startTime = DateTime.Now;

            try
            {
                _logger.LogInformation("Iniciando geração de {Count} registros sintéticos", request.NumberOfRecords);

                if (request.UseAI && _aiService != null)
                {
                    result = await GenerateWithAIAsync(request);
                }
                else
                {
                    result = await GenerateWithRulesAsync(request);
                }

                result.GenerationTime = DateTime.Now - startTime;
                result.Success = true;

                _logger.LogInformation("Geração concluída em {Duration}ms", result.GenerationTime.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro na geração de dados sintéticos");
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.GenerationTime = DateTime.Now - startTime;
            }

            return result;
        }

        public async Task<string> GenerateFieldValueAsync(FieldElement field, string context, string dataType, ExcelDataContext excelContext = null)
        {
            try
            {
                var fieldType = InferFieldType(field, dataType);
                
                switch (fieldType)
                {
                    case "cnpj":
                        return GenerateCnpj();
                    case "cpf":
                        return GenerateCpf();
                    case "date":
                        return GenerateDate();
                    case "datetime":
                        return GenerateDateTime();
                    case "decimal":
                        return GenerateDecimal(field.LengthField);
                    case "integer":
                        return GenerateInteger(field.LengthField);
                    case "email":
                        return GenerateEmail();
                    case "phone":
                        return GeneratePhone();
                    case "text":
                    default:
                        return GenerateText(field.LengthField, context, excelContext);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao gerar valor para campo {FieldName}", field.Name);
                return new string(' ', field.LengthField);
            }
        }

        public async Task<List<string>> GenerateMultipleFieldValuesAsync(FieldElement field, int count, ExcelDataContext excelContext = null)
        {
            var values = new List<string>();
            
            for (int i = 0; i < count; i++)
            {
                var value = await GenerateFieldValueAsync(field, $"Record {i + 1}", null, excelContext);
                values.Add(value);
            }
            
            return values;
        }

        public async Task<Dictionary<string, object>> AnalyzeFieldGenerationRequirements(FieldElement field, ExcelDataContext excelContext = null)
        {
            var requirements = new Dictionary<string, object>
            {
                ["fieldName"] = field.Name,
                ["fieldType"] = InferFieldType(field),
                ["length"] = field.LengthField,
                ["isSequential"] = field.IsSequential,
                ["isRequired"] = field.IsRequired,
                ["alignment"] = field.AlignmentType.ToString()
            };

            if (excelContext != null)
            {
                // Analisar dados do Excel para melhorar geração
                var sampleValues = await ExtractSampleValuesFromExcel(field, excelContext);
                if (sampleValues.Any())
                {
                    requirements["sampleValues"] = sampleValues;
                    requirements["hasExcelData"] = true;
                }
            }

            return requirements;
        }

        private async Task<GeneratedDataResult> GenerateWithAIAsync(SyntheticDataRequest request)
        {
            var prompt = BuildAIPrompt(request);
            var aiResponse = await _aiService.GenerateSyntheticDataAsync(prompt);
            
            var lines = ParseAIResponse(aiResponse, request.NumberOfRecords);
            
            return new GeneratedDataResult
            {
                Success = true,
                GeneratedLines = lines,
                TotalRecords = lines.Count,
                UsedLayout = request.Layout,
                GenerationMetadata = new Dictionary<string, object>
                {
                    ["generationMethod"] = "AI",
                    ["aiPrompt"] = prompt,
                    ["aiResponseLength"] = aiResponse.Length
                }
            };
        }

        private async Task<GeneratedDataResult> GenerateWithRulesAsync(SyntheticDataRequest request)
        {
            var generatedLines = new List<string>();
            var fieldStats = new List<FieldGenerationStats>();

            // Extrair todos os campos do layout
            var allFields = ExtractAllFields(request.Layout);

            for (int recordIndex = 0; recordIndex < request.NumberOfRecords; recordIndex++)
            {
                var lineBuilder = new StringBuilder();
                
                foreach (var lineElement in request.Layout.Elements)
                {
                    var lineContent = await GenerateLineContent(lineElement, recordIndex, request);
                    lineBuilder.Append(lineContent);
                }

                generatedLines.Add(lineBuilder.ToString());
            }

            return new GeneratedDataResult
            {
                Success = true,
                GeneratedLines = generatedLines,
                TotalRecords = generatedLines.Count,
                UsedLayout = request.Layout,
                FieldStats = fieldStats,
                GenerationMetadata = new Dictionary<string, object>
                {
                    ["generationMethod"] = "Rules",
                    ["totalFields"] = allFields.Count
                }
            };
        }

        private string BuildAIPrompt(SyntheticDataRequest request)
        {
            var prompt = new StringBuilder();
            
            prompt.AppendLine($"Gere {request.NumberOfRecords} registros sintéticos baseados no layout abaixo.");
            prompt.AppendLine($"Layout: {request.Layout.Name}");
            prompt.AppendLine($"Tipo: {request.Layout.LayoutType}");
            prompt.AppendLine();

            // Adicionar estrutura do layout
            prompt.AppendLine("Estrutura do Layout:");
            foreach (var lineElement in request.Layout.Elements)
            {
                prompt.AppendLine($"- {lineElement.Name}: {lineElement.InitialValue}");
                // Adicionar campos da linha se necessário
            }

            // Adicionar dados de exemplo se disponíveis
            if (request.SampleRealData.Any())
            {
                prompt.AppendLine();
                prompt.AppendLine("Dados reais de exemplo:");
                foreach (var sample in request.SampleRealData.Take(3))
                {
                    prompt.AppendLine($"- {sample.FieldName}: {sample.Value}");
                }
            }

            // Adicionar regras específicas
            prompt.AppendLine();
            prompt.AppendLine("Regras:");
            prompt.AppendLine("- Manter formato posicional exato");
            prompt.AppendLine("- CNPJ/CPF: gerar números válidos sinteticamente");
            prompt.AppendLine("- Datas: usar formato apropriado");
            prompt.AppendLine("- Valores monetários: usar formato sem separadores");
            prompt.AppendLine("- Campos sequenciais: incrementar automaticamente");
            prompt.AppendLine();
            prompt.AppendLine("Resposta: Apenas os dados gerados, um registro por linha, sem marcações.");

            return prompt.ToString();
        }

        private List<string> ParseAIResponse(string aiResponse, int expectedCount)
        {
            var lines = aiResponse
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Take(expectedCount)
                .ToList();

            return lines;
        }

        private async Task<string> GenerateLineContent(LineElement lineElement, int recordIndex, SyntheticDataRequest request)
        {
            var lineBuilder = new StringBuilder();
            
            // Adicionar InitialValue se existir
            if (!string.IsNullOrEmpty(lineElement.InitialValue))
            {
                lineBuilder.Append(lineElement.InitialValue);
            }

            // Gerar campos da linha
            if (lineElement.Elements != null)
            {
                foreach (var elementJson in lineElement.Elements)
                {
                    try
                    {
                        var field = Newtonsoft.Json.JsonConvert.DeserializeObject<FieldElement>(elementJson);
                        if (field != null)
                        {
                            var fieldValue = await GenerateFieldValueAsync(field, $"Record {recordIndex}", null, request.ExcelContext);
                            lineBuilder.Append(fieldValue);
                        }
                    }
                    catch
                    {
                        // Ignorar elementos inválidos
                    }
                }
            }

            return lineBuilder.ToString();
        }

        private List<FieldElement> ExtractAllFields(Layout layout)
        {
            var fields = new List<FieldElement>();
            
            foreach (var lineElement in layout.Elements)
            {
                ExtractFieldsFromLineElement(lineElement, fields);
            }
            
            return fields;
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

        private async Task<List<string>> ExtractSampleValuesFromExcel(FieldElement field, ExcelDataContext excelContext)
        {
            // Implementar lógica para extrair valores de exemplo do Excel
            // baseado no mapeamento de campos
            return new List<string>();
        }

        private string InferFieldType(FieldElement field, string dataType = null)
        {
            if (!string.IsNullOrEmpty(dataType))
                return dataType.ToLower();

            var name = field.Name.ToLower();
            
            if (name.Contains("cnpj")) return "cnpj";
            if (name.Contains("cpf")) return "cpf";
            if (name.Contains("data") || name.Contains("date")) return "date";
            if (name.Contains("hora") || name.Contains("time")) return "datetime";
            if (name.Contains("valor") || name.Contains("preco") || name.Contains("amount")) return "decimal";
            if (name.Contains("quantidade") || name.Contains("qtd")) return "integer";
            if (name.Contains("email")) return "email";
            if (name.Contains("telefone") || name.Contains("phone")) return "phone";
            
            return "text";
        }

        private string GenerateCnpj()
        {
            // Gerar CNPJ válido sinteticamente
            var cnpj = _random.Next(10000000, 99999999).ToString() + "0001" + _random.Next(10, 99).ToString();
            return cnpj.PadLeft(14, '0');
        }

        private string GenerateCpf()
        {
            // Gerar CPF válido sinteticamente
            var cpf = _random.Next(100000000, 999999999).ToString() + _random.Next(10, 99).ToString();
            return cpf.PadLeft(11, '0');
        }

        private string GenerateDate()
        {
            var startDate = DateTime.Now.AddYears(-5);
            var endDate = DateTime.Now;
            var randomDate = startDate.AddDays(_random.Next(0, (int)(endDate - startDate).TotalDays));
            return randomDate.ToString("yyyyMMdd");
        }

        private string GenerateDateTime()
        {
            var startDate = DateTime.Now.AddYears(-1);
            var endDate = DateTime.Now;
            var randomDate = startDate.AddDays(_random.Next(0, (int)(endDate - startDate).TotalDays));
            return randomDate.ToString("yyyy-MM-ddTHH:mm:ss");
        }

        private string GenerateDecimal(int length)
        {
            var value = (decimal)_random.NextDouble() * 10000;
            var formatted = value.ToString("F2").Replace(".", "").Replace(",", "");
            return formatted.PadLeft(length, '0');
        }

        private string GenerateInteger(int length)
        {
            var value = _random.Next(1, 999999);
            return value.ToString().PadLeft(length, '0');
        }

        private string GenerateEmail()
        {
            var domains = new[] { "gmail.com", "hotmail.com", "outlook.com", "empresa.com.br" };
            var names = new[] { "joao", "maria", "pedro", "ana", "carlos", "lucia" };
            var domain = domains[_random.Next(domains.Length)];
            var name = names[_random.Next(names.Length)];
            return $"{name}{_random.Next(100, 999)}@{domain}";
        }

        private string GeneratePhone()
        {
            var ddd = _random.Next(11, 99);
            var number = _random.Next(10000000, 99999999);
            return $"{ddd}{number}";
        }

        private string GenerateText(int length, string context, ExcelDataContext excelContext)
        {
            var words = new[] { "exemplo", "teste", "dados", "sinteticos", "gerado", "automaticamente" };
            var word = words[_random.Next(words.Length)];
            return word.PadRight(length, ' ');
        }
    }
}
