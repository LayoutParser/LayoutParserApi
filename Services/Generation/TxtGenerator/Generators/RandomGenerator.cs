using LayoutParserApi.Services.Generation.TxtGenerator.Generators.Interfaces;
using LayoutParserApi.Services.Generation.TxtGenerator.Models;

using System.Text;

namespace LayoutParserApi.Services.Generation.TxtGenerator.Generators
{
    /// <summary>
    /// Gerador random controlado: valores aleatórios respeitando tipo e limites
    /// </summary>
    public class RandomGenerator : IFieldValueGenerator
    {
        private readonly ILogger<RandomGenerator> _logger;
        private readonly Random _random = new();

        public RandomGenerator(ILogger<RandomGenerator> logger)
        {
            _logger = logger;
        }

        public string GenerateValue(FieldDefinition field, int recordIndex, Dictionary<string, object> context = null)
        {
            // Se é fixo, retornar valor fixo
            if (field.IsFixed && !string.IsNullOrEmpty(field.FixedValue))
            {
                _logger.LogDebug("Campo {FieldName} gerado com valor fixo: {Value}", field.Name, field.FixedValue);
                return FormatValue(field.FixedValue, field.Length, field.Alignment);
            }

            // Se tem domínio, escolher aleatoriamente do domínio
            if (!string.IsNullOrEmpty(field.Domain))
            {
                var domainValues = field.Domain.Split(',', StringSplitOptions.RemoveEmptyEntries);
                if (domainValues.Any())
                {
                    var value = domainValues[_random.Next(domainValues.Length)].Trim();
                    _logger.LogDebug("Campo {FieldName} gerado aleatoriamente do domínio: {Value}", field.Name, value);
                    return FormatValue(value, field.Length, field.Alignment);
                }
            }

            // Gerar valor aleatório baseado no tipo
            var generatedValue = GenerateRandomValue(field, recordIndex, context);
            _logger.LogDebug("Campo {FieldName} gerado aleatoriamente: {Value}", field.Name, generatedValue);
            return FormatValue(generatedValue, field.Length, field.Alignment);
        }

        private string GenerateRandomValue(FieldDefinition field, int recordIndex, Dictionary<string, object> context)
        {
            return field.DataType.ToLower() switch
            {
                "cnpj" => GenerateRandomCNPJ(),
                "cpf" => GenerateRandomCPF(),
                "date" => GenerateRandomDate(),
                "time" => GenerateRandomTime(),
                "datetime" => GenerateRandomDateTime(),
                "decimal" => GenerateRandomDecimal(field.Length),
                "int" => GenerateRandomInt(field.Length, recordIndex),
                "email" => GenerateRandomEmail(),
                "filler" => new string(' ', field.Length),
                "string" => GenerateRandomString(field, context),
                _ => new string(' ', field.Length)
            };
        }

        private string GenerateRandomCNPJ()
        {
            // Gerar CNPJ válido aleatório
            var n1 = _random.Next(0, 10);
            var n2 = _random.Next(0, 10);
            var n3 = _random.Next(0, 10);
            var n4 = _random.Next(0, 10);
            var n5 = _random.Next(0, 10);
            var n6 = _random.Next(0, 10);
            var n7 = _random.Next(0, 10);
            var n8 = _random.Next(0, 10);
            var n9 = _random.Next(0, 10);
            var n10 = _random.Next(0, 10);
            var n11 = _random.Next(0, 10);
            var n12 = _random.Next(0, 10);

            var d1 = CalculateCNPJDigit1(n1, n2, n3, n4, n5, n6, n7, n8, n9, n10, n11, n12);
            var d2 = CalculateCNPJDigit2(n1, n2, n3, n4, n5, n6, n7, n8, n9, n10, n11, n12, d1);

            return $"{n1}{n2}{n3}{n4}{n5}{n6}{n7}{n8}{n9}{n10}{n11}{n12}{d1}{d2}";
        }

        private string GenerateRandomCPF()
        {
            var n1 = _random.Next(0, 10);
            var n2 = _random.Next(0, 10);
            var n3 = _random.Next(0, 10);
            var n4 = _random.Next(0, 10);
            var n5 = _random.Next(0, 10);
            var n6 = _random.Next(0, 10);
            var n7 = _random.Next(0, 10);
            var n8 = _random.Next(0, 10);
            var n9 = _random.Next(0, 10);

            var d1 = CalculateCPFDigit1(n1, n2, n3, n4, n5, n6, n7, n8, n9);
            var d2 = CalculateCPFDigit2(n1, n2, n3, n4, n5, n6, n7, n8, n9, d1);

            return $"{n1}{n2}{n3}{n4}{n5}{n6}{n7}{n8}{n9}{d1}{d2}";
        }

        private string GenerateRandomDate()
        {
            var daysOffset = _random.Next(-30, 30);
            var date = DateTime.Now.AddDays(daysOffset);
            return date.ToString("yyyyMMdd");
        }

        private string GenerateRandomTime()
        {
            var hours = _random.Next(0, 24);
            var minutes = _random.Next(0, 60);
            var seconds = _random.Next(0, 60);
            return $"{hours:D2}{minutes:D2}{seconds:D2}";
        }

        private string GenerateRandomDateTime()
        {
            var daysOffset = _random.Next(-30, 30);
            var date = DateTime.Now.AddDays(daysOffset);
            return date.ToString("yyyy-MM-ddTHH:mm:ss");
        }

        private string GenerateRandomDecimal(int length)
        {
            // Gerar valor monetário em centavos
            var minValue = 100; // R$ 1,00
            var maxValue = (int)Math.Pow(10, length) - 1;
            var value = _random.Next(minValue, maxValue);
            return value.ToString().PadLeft(length, '0');
        }

        private string GenerateRandomInt(int length, int recordIndex)
        {
            var baseValue = (recordIndex + 1) * 1000;
            var randomPart = _random.Next(1, 999);
            var value = baseValue + randomPart;
            return value.ToString().PadLeft(length, '0');
        }

        private string GenerateRandomEmail()
        {
            var names = new[] { "teste", "usuario", "contato", "vendas", "suporte" };
            var domains = new[] { "@exemplo.com", "@teste.com.br", "@empresa.com" };
            var name = names[_random.Next(names.Length)];
            var number = _random.Next(100, 999);
            var domain = domains[_random.Next(domains.Length)];
            return $"{name}{number}{domain}";
        }

        private string GenerateRandomString(FieldDefinition field, Dictionary<string, object> context)
        {
            // Se tem exemplo, usar como base com variação
            if (!string.IsNullOrEmpty(field.Example))
                return field.Example.Substring(0, Math.Min(field.Example.Length, field.Length));
            

            // Gerar string aleatória
            var chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 ";
            var result = new StringBuilder();
            for (int i = 0; i < field.Length; i++)
                result.Append(chars[_random.Next(chars.Length)]);
            
            return result.ToString();
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

        // Métodos auxiliares para cálculo de dígitos verificadores
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