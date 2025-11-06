using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LayoutParserApi.Services.Generation.TxtGenerator.Models;
using LayoutParserApi.Services.Generation.Interfaces;
using LayoutParserApi.Services.Generation.Implementations;

namespace LayoutParserApi.Services.Generation.TxtGenerator.Generators
{
    /// <summary>
    /// Gerador semântico usando IA: valores realistas respeitando tipo e tamanho
    /// </summary>
    public class SemanticAIGenerator : IAsyncFieldValueGenerator
    {
        private readonly ILogger<SemanticAIGenerator> _logger;
        private readonly GeminiAIService _aiService;
        private readonly IValueGenerator _valueGenerator;

        public SemanticAIGenerator(
            ILogger<SemanticAIGenerator> logger,
            GeminiAIService aiService,
            IValueGenerator valueGenerator)
        {
            _logger = logger;
            _aiService = aiService;
            _valueGenerator = valueGenerator;
        }

        public async Task<string> GenerateValueAsync(FieldDefinition field, int recordIndex, Dictionary<string, object> context = null)
        {
            // Campos com regras matemáticas: usar gerador programático
            if (field.DataType == "decimal" || field.DataType == "int" || 
                field.DataType == "cnpj" || field.DataType == "cpf" ||
                field.DataType == "date" || field.DataType == "time" ||
                field.DataType == "filler")
            {
                return GenerateProgrammaticValue(field, recordIndex, context);
            }

            // Se é fixo, retornar valor fixo
            if (field.IsFixed && !string.IsNullOrEmpty(field.FixedValue))
            {
                _logger.LogDebug("Campo {FieldName} gerado com valor fixo: {Value}", field.Name, field.FixedValue);
                return FormatValue(field.FixedValue, field.Length, field.Alignment);
            }

            // Se tem domínio pequeno, escolher do domínio
            if (!string.IsNullOrEmpty(field.Domain))
            {
                var domainValues = field.Domain.Split(',', StringSplitOptions.RemoveEmptyEntries);
                if (domainValues.Length <= 5)
                {
                    var random = new Random();
                    var value = domainValues[random.Next(domainValues.Length)].Trim();
                    _logger.LogDebug("Campo {FieldName} gerado do domínio: {Value}", field.Name, value);
                    return FormatValue(value, field.Length, field.Alignment);
                }
            }

            // Para textos descritivos, usar IA
            try
            {
                var prompt = BuildPromptForField(field, recordIndex, context);
                var aiResponse = await _aiService.CallGeminiAPI(prompt);
                
                // Extrair apenas o valor gerado (primeira linha, sem explicações)
                var lines = aiResponse.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .ToList();

                var generatedValue = lines.FirstOrDefault() ?? "";
                
                // Limpar o valor (remover marcações, explicações, etc.)
                generatedValue = CleanAIResponse(generatedValue);
                
                _logger.LogDebug("Campo {FieldName} gerado pela IA: {Value}", field.Name, generatedValue);
                return FormatValue(generatedValue, field.Length, field.Alignment);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao gerar valor com IA para campo {FieldName}, usando fallback", field.Name);
                return GenerateProgrammaticValue(field, recordIndex, context);
            }
        }

        public string GenerateValue(FieldDefinition field, int recordIndex, Dictionary<string, object> context = null)
        {
            // Para compatibilidade síncrona, usar versão assíncrona
            return GenerateValueAsync(field, recordIndex, context).GetAwaiter().GetResult();
        }

        private string GenerateProgrammaticValue(FieldDefinition field, int recordIndex, Dictionary<string, object> context)
        {
            // Usar ValueGenerator para valores programáticos
            var excelSamples = context?.ContainsKey("ExcelSamples") == true 
                ? context["ExcelSamples"] as List<string> 
                : null;

            return field.DataType.ToLower() switch
            {
                "decimal" => _valueGenerator.GenerateMonetaryValue(field.Length),
                "int" => _valueGenerator.GenerateNumericValue(field.Length),
                "cnpj" => GenerateCNPJ(),
                "cpf" => GenerateCPF(),
                "date" => DateTime.Now.ToString("yyyyMMdd"),
                "time" => DateTime.Now.ToString("HHmmss"),
                "datetime" => DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                "filler" => new string(' ', field.Length),
                "string" => _valueGenerator.GenerateRealisticText(field.Name, field.Length, excelSamples),
                _ => new string(' ', field.Length)
            };
        }

        private string BuildPromptForField(FieldDefinition field, int recordIndex, Dictionary<string, object> context)
        {
            var sb = new StringBuilder();
            
            sb.AppendLine("=== GERAÇÃO DE CAMPO ===");
            sb.AppendLine($"Campo: {field.Name}");
            sb.AppendLine($"Descrição: {field.Description ?? "N/A"}");
            sb.AppendLine($"Tipo: {field.DataType}");
            sb.AppendLine($"Tamanho exato: {field.Length} caracteres");
            sb.AppendLine($"Alinhamento: {field.Alignment}");
            sb.AppendLine($"Obrigatório: {(field.IsRequired ? "SIM" : "NÃO")}");
            sb.AppendLine();

            if (!string.IsNullOrEmpty(field.Example))
            {
                sb.AppendLine($"Exemplo: {field.Example}");
                sb.AppendLine();
            }

            if (!string.IsNullOrEmpty(field.Domain))
            {
                sb.AppendLine($"Valores possíveis: {field.Domain}");
                sb.AppendLine();
            }

            // Adicionar exemplos do Excel se disponíveis
            if (context?.ContainsKey("ExcelSamples") == true)
            {
                var samples = context["ExcelSamples"] as List<string>;
                if (samples != null && samples.Any())
                {
                    sb.AppendLine("Exemplos reais do Excel:");
                    foreach (var sample in samples.Take(3))
                    {
                        sb.AppendLine($"  - {sample}");
                    }
                    sb.AppendLine();
                }
            }

            sb.AppendLine("=== INSTRUÇÕES ===");
            sb.AppendLine($"Gere um valor realista para este campo com EXATAMENTE {field.Length} caracteres.");
            sb.AppendLine("Use dados realistas (nomes reais de empresas, produtos, pessoas).");
            sb.AppendLine("Evite palavras genéricas como 'teste', 'exemplo', 'sintetico'.");
            sb.AppendLine("Retorne APENAS o valor gerado, sem explicações.");

            return sb.ToString();
        }

        private string CleanAIResponse(string response)
        {
            if (string.IsNullOrEmpty(response))
                return "";

            // Remover marcações comuns da IA
            response = response.Trim();
            
            // Remover prefixos comuns
            var prefixes = new[] { "Valor:", "Campo:", "Gerado:", "Resultado:" };
            foreach (var prefix in prefixes)
            {
                if (response.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    response = response.Substring(prefix.Length).Trim();
                }
            }

            // Remover aspas se houver
            if (response.StartsWith("\"") && response.EndsWith("\""))
            {
                response = response.Substring(1, response.Length - 2);
            }

            return response.Trim();
        }

        private string FormatValue(string value, int length, string alignment)
        {
            if (string.IsNullOrEmpty(value))
                value = "";

            if (value.Length > length)
                value = value.Substring(0, length);

            if (value.Length < length)
            {
                if (alignment == "Right")
                    value = value.PadLeft(length, ' ');
                else if (alignment == "Center")
                {
                    int pad = length - value.Length;
                    value = value.PadLeft(value.Length + pad / 2, ' ').PadRight(length, ' ');
                }
                else
                    value = value.PadRight(length, ' ');
            }

            return value;
        }

        private string GenerateCNPJ()
        {
            var random = new Random();
            var n1 = random.Next(0, 10);
            var n2 = random.Next(0, 10);
            var n3 = random.Next(0, 10);
            var n4 = random.Next(0, 10);
            var n5 = random.Next(0, 10);
            var n6 = random.Next(0, 10);
            var n7 = random.Next(0, 10);
            var n8 = random.Next(0, 10);
            var n9 = random.Next(0, 10);
            var n10 = random.Next(0, 10);
            var n11 = random.Next(0, 10);
            var n12 = random.Next(0, 10);

            var d1 = CalculateCNPJDigit1(n1, n2, n3, n4, n5, n6, n7, n8, n9, n10, n11, n12);
            var d2 = CalculateCNPJDigit2(n1, n2, n3, n4, n5, n6, n7, n8, n9, n10, n11, n12, d1);

            return $"{n1}{n2}{n3}{n4}{n5}{n6}{n7}{n8}{n9}{n10}{n11}{n12}{d1}{d2}";
        }

        private string GenerateCPF()
        {
            var random = new Random();
            var n1 = random.Next(0, 10);
            var n2 = random.Next(0, 10);
            var n3 = random.Next(0, 10);
            var n4 = random.Next(0, 10);
            var n5 = random.Next(0, 10);
            var n6 = random.Next(0, 10);
            var n7 = random.Next(0, 10);
            var n8 = random.Next(0, 10);
            var n9 = random.Next(0, 10);

            var d1 = CalculateCPFDigit1(n1, n2, n3, n4, n5, n6, n7, n8, n9);
            var d2 = CalculateCPFDigit2(n1, n2, n3, n4, n5, n6, n7, n8, n9, d1);

            return $"{n1}{n2}{n3}{n4}{n5}{n6}{n7}{n8}{n9}{d1}{d2}";
        }

        private int CalculateCNPJDigit1(int n1, int n2, int n3, int n4, int n5, int n6, int n7, int n8, int n9, int n10, int n11, int n12)
        {
            var sum = n1 * 5 + n2 * 4 + n3 * 3 + n4 * 2 + n5 * 9 + n6 * 8 + n7 * 7 + n8 * 6 + n9 * 5 + n10 * 4 + n11 * 3 + n12 * 2;
            var remainder = sum % 11;
            return remainder < 2 ? 0 : 11 - remainder;
        }

        private int CalculateCNPJDigit2(int n1, int n2, int n3, int n4, int n5, int n6, int n7, int n8, int n9, int n10, int n11, int n12, int d1)
        {
            var sum = n1 * 6 + n2 * 5 + n3 * 4 + n4 * 3 + n5 * 2 + n6 * 9 + n7 * 8 + n8 * 7 + n9 * 6 + n10 * 5 + n11 * 4 + n12 * 3 + d1 * 2;
            var remainder = sum % 11;
            return remainder < 2 ? 0 : 11 - remainder;
        }

        private int CalculateCPFDigit1(int n1, int n2, int n3, int n4, int n5, int n6, int n7, int n8, int n9)
        {
            var sum = n1 * 10 + n2 * 9 + n3 * 8 + n4 * 7 + n5 * 6 + n6 * 5 + n7 * 4 + n8 * 3 + n9 * 2;
            var remainder = sum % 11;
            return remainder < 2 ? 0 : 11 - remainder;
        }

        private int CalculateCPFDigit2(int n1, int n2, int n3, int n4, int n5, int n6, int n7, int n8, int n9, int d1)
        {
            var sum = n1 * 11 + n2 * 10 + n3 * 9 + n4 * 8 + n5 * 7 + n6 * 6 + n7 * 5 + n8 * 4 + n9 * 3 + d1 * 2;
            var remainder = sum % 11;
            return remainder < 2 ? 0 : 11 - remainder;
        }
    }
}

