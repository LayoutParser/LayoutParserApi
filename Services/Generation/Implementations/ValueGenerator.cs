using LayoutParserApi.Services.Generation.Interfaces;

namespace LayoutParserApi.Services.Generation.Implementations
{
    /// <summary>
    /// Gerador de valores com regras de consistência e uso de dados reais do Excel
    /// </summary>
    public class ValueGenerator : IValueGenerator
    {
        private readonly ILogger<ValueGenerator> _logger;
        private readonly Random _random = new();

        public ValueGenerator(ILogger<ValueGenerator> logger)
        {
            _logger = logger;
        }

        public string GenerateMonetaryValue(int length, decimal? targetTotal = null, List<decimal> itemValues = null)
        {
            decimal value;

            // Se temos itens, calcular total baseado na soma
            if (itemValues != null && itemValues.Any())
                value = itemValues.Sum();
            // Se temos um total alvo, usar ele
            else if (targetTotal.HasValue)
                value = targetTotal.Value;
            // Caso contrário, gerar valor aleatório realista
            else
            {
                // Valores monetários realistas (entre R$ 100 e R$ 999.999,99)
                var minValue = 100.00m;
                var maxValue = 999999.99m;
                value = (decimal)(_random.NextDouble() * (double)(maxValue - minValue) + (double)minValue);
            }

            // Formatar como string sem separadores (formato brasileiro: 0000000005085659 = R$ 5.085,65)
            // Assumindo que o valor está em centavos
            var valueInCents = (long)(value * 100);
            var valueStr = valueInCents.ToString().PadLeft(length, '0');

            // Se exceder o tamanho, truncar
            if (valueStr.Length > length)
            {
                _logger.LogWarning("Valor monetário truncado de {Original} para {Length} caracteres", valueStr.Length, length);
                valueStr = valueStr.Substring(valueStr.Length - length);
            }

            return valueStr;
        }

        public string GenerateNumericValue(int length, int? minValue = null, int? maxValue = null, bool padWithZeros = true)
        {
            var min = minValue ?? 1;
            var max = maxValue ?? (int)Math.Pow(10, length) - 1;

            var value = _random.Next(min, max + 1);
            var valueStr = value.ToString();

            if (padWithZeros && valueStr.Length < length)
                valueStr = valueStr.PadLeft(length, '0');
            else if (valueStr.Length > length)
                valueStr = valueStr.Substring(0, length);

            return valueStr;
        }

        public string GenerateRealisticText(string fieldName, int length, List<string> excelSamples = null)
        {
            // Se temos exemplos do Excel, usar um deles (com variação)
            if (excelSamples != null && excelSamples.Any())
            {
                var sample = excelSamples[_random.Next(excelSamples.Count)];

                // Aplicar variação leve no texto
                var variation = ApplyTextVariation(sample, length);
                return FormatField(variation, length);
            }

            // Fallback: gerar texto baseado no tipo de campo
            var normalizedName = fieldName.ToUpperInvariant();
            if (normalizedName.Contains("NOME") || normalizedName.Contains("RAZAO"))
                return GenerateCompanyName(length);
            else if (normalizedName.Contains("PRODUTO") || normalizedName.Contains("DESCRICAO"))
                return GenerateProductDescription(length);
            else if (normalizedName.Contains("ENDERECO") || normalizedName.Contains("LOGRADOURO"))
                return GenerateAddress(length);
            else if (normalizedName.Contains("BAIRRO"))
                return GenerateNeighborhood(length);
            else if (normalizedName.Contains("CIDADE") || normalizedName.Contains("MUNICIPIO"))
                return GenerateCity(length);
            else if (normalizedName.Contains("EMAIL"))
                return GenerateEmail(length);

            // Fallback genérico
            return new string(' ', length);
        }

        public bool ValidateConsistency(Dictionary<string, decimal> values, Dictionary<string, string> rules)
        {
            foreach (var rule in rules)
            {
                var ruleParts = rule.Value.Split('=');
                if (ruleParts.Length != 2)
                    continue;

                var leftSide = ruleParts[0].Trim();
                var rightSide = ruleParts[1].Trim();

                // Parse simples: "total = item1 + item2 + item3"
                if (rightSide.Contains("+"))
                {
                    var sumParts = rightSide.Split('+');
                    decimal sum = 0;

                    foreach (var part in sumParts)
                    {
                        var key = part.Trim();
                        if (values.ContainsKey(key))
                            sum += values[key];
                       
                    }

                    if (values.ContainsKey(leftSide))
                    {
                        var total = values[leftSide];
                        var tolerance = 0.01m; // Tolerância para arredondamentos

                        if (Math.Abs(total - sum) > tolerance)
                        {
                            _logger.LogWarning("Inconsistência detectada: {LeftSide} ({Total}) != soma de {RightSide} ({Sum})",leftSide, total, rightSide, sum);
                            return false;
                        }
                    }
                }
            }

            return true;
        }

        private string ApplyTextVariation(string original, int maxLength)
        {
            if (string.IsNullOrEmpty(original))
                return original;

            // Variações simples: adicionar/remover espaços, mudar maiúsculas/minúsculas
            var variations = new[]
            {
                original,
                original.ToUpper(),
                original.ToLower(),
                original.Trim(),
                original.Replace("  ", " ")
            };

            var selected = variations[_random.Next(variations.Length)];

            if (selected.Length > maxLength)
                selected = selected.Substring(0, maxLength);

            return selected;
        }

        private string GenerateCompanyName(int length)
        {
            var companies = new[]
            {
                "Stellantis Automoveis Brasil Ltda.",
                "FCA FIAT CHRYSLER AUTOMOVEIS",
                "SADA TRANSPORTES E ARMAZENAGENS SA",
                "Fiasa - Matriz",
                "Empresa Brasileira de Tecnologia",
                "Industria Nacional de Componentes"
            };

            var company = companies[_random.Next(companies.Length)];
            return FormatField(company, length);
        }

        private string GenerateProductDescription(int length)
        {
            var products = new[]
            {
                "ARGO DRI1.0 FFLY PGMORVRPL8 005 PASSAGEIROS",
                "VEICULO AUTOMOTOR",
                "PRODUTO INDUSTRIALIZADO",
                "MERCADORIA PARA REVENDA"
            };

            var product = products[_random.Next(products.Length)];
            return FormatField(product, length);
        }

        private string GenerateAddress(int length)
        {
            var addresses = new[]
            {
                "Avenida Contorno",
                "RODOVIA BR 101 - NORTE",
                "Rua das Flores",
                "Avenida Paulista",
                "Rodovia BR 101 - NO S/N, KM 13 AO 1"
            };

            var address = addresses[_random.Next(addresses.Length)];
            return FormatField(address, length);
        }

        private string GenerateNeighborhood(int length)
        {
            var neighborhoods = new[]
            {
                "Bairro Paulo Camilo",
                "NOVA GOIANA",
                "Centro",
                "Jardim das Flores"
            };

            var neighborhood = neighborhoods[_random.Next(neighborhoods.Length)];
            return FormatField(neighborhood, length);
        }

        private string GenerateCity(int length)
        {
            var cities = new[]
            {
                "Betim",
                "GOIANA",
                "IGARAPE",
                "Sao Paulo",
                "Rio de Janeiro"
            };

            var city = cities[_random.Next(cities.Length)];
            return FormatField(city, length);
        }

        private string GenerateEmail(int length)
        {
            var domains = new[] { "@stellantis.com", "@outlook.com", "@hotmail.com", "@empresa.com.br" };
            var names = new[] { "antuany.veridiano", "contato", "vendas", "suporte" };

            var email = $"{names[_random.Next(names.Length)]}{_random.Next(100, 999)}{domains[_random.Next(domains.Length)]}";
            return FormatField(email, length);
        }

        private string FormatField(string value, int length)
        {
            if (string.IsNullOrEmpty(value))
                return new string(' ', length);

            if (value.Length > length)
                return value.Substring(0, length);

            return value.PadRight(length, ' ');
        }
    }
}