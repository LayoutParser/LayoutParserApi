using LayoutParserApi.Services.Generation.Interfaces;

namespace LayoutParserApi.Services.Generation.Implementations
{
    /// <summary>
    /// Implementação padrão de <see cref="ITypedValueGenerator"/>. Concentra os geradores de valor
    /// por tipo (CPF/CNPJ/data/decimal/…) que antes viviam duplicados como métodos privados de
    /// <see cref="SyntheticDataGeneratorService"/> — agora consumidos também pelo caminho Xml
    /// (issue #356). 100% local, sem IA, sem dado real de cliente.
    /// </summary>
    public class TypedValueGenerator : ITypedValueGenerator
    {
        private readonly ILogger<TypedValueGenerator> _logger;
        private readonly Random _random = new();

        public TypedValueGenerator(ILogger<TypedValueGenerator> logger)
        {
            _logger = logger;
        }

        public string InferType(string? fieldName, string? explicitType = null)
        {
            if (!string.IsNullOrWhiteSpace(explicitType))
                return explicitType.ToLowerInvariant();

            var name = (fieldName ?? string.Empty).ToLowerInvariant();

            if (name.Contains("cnpj")) return "cnpj";
            if (name.Contains("cpf")) return "cpf";
            if (name.StartsWith("dt") || name.Contains("data") || name.Contains("date") || name.Contains("emissao")) return "date";
            if (name.Contains("hora") || name.Contains("time")) return "datetime";
            if (name.Contains("valor") || name.Contains("preco") || name.Contains("amount") || name.Contains("total")) return "decimal";
            if (name.Contains("quantidade") || name.Contains("qtd")) return "integer";
            if (name.Contains("email")) return "email";
            if (name.Contains("telefone") || name.Contains("phone")) return "phone";
            if (name.Contains("versao")) return "version";

            return "text";
        }

        public string Generate(string fieldType, int length = 0, string? context = null)
        {
            try
            {
                return (fieldType ?? "text").ToLowerInvariant() switch
                {
                    "cnpj" => GenerateCnpj(),
                    "cpf" => GenerateCpf(),
                    "date" => GenerateDate(),
                    "datetime" => GenerateDateTime(),
                    "time" => GenerateDateTime(),
                    "decimal" => GenerateDecimal(length),
                    "integer" or "int" => GenerateInteger(length),
                    "email" => GenerateEmail(),
                    "phone" => GeneratePhone(),
                    "version" => "1.00",
                    _ => GenerateText(length, context)
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao gerar valor sintético para tipo {FieldType}", fieldType);
                return length > 0 ? new string(' ', length) : string.Empty;
            }
        }

        private string GenerateCnpj()
        {
            // Gera CNPJ sinteticamente válido: 12 dígitos base (8 aleatórios + "0001" de
            // filial matriz) seguidos dos 2 dígitos verificadores calculados por módulo 11.
            var baseDigits = _random.Next(10000000, 99999999).ToString() + "0001";
            var dv1 = CalculateCnpjCheckDigit(baseDigits);
            var dv2 = CalculateCnpjCheckDigit(baseDigits + dv1);
            return baseDigits + dv1 + dv2;
        }

        private string GenerateCpf()
        {
            // Gera CPF sinteticamente válido: 9 dígitos base aleatórios seguidos dos 2
            // dígitos verificadores calculados por módulo 11.
            var baseDigits = _random.Next(100000000, 999999999).ToString().PadLeft(9, '0');
            var dv1 = CalculateCpfCheckDigit(baseDigits);
            var dv2 = CalculateCpfCheckDigit(baseDigits + dv1);
            return baseDigits + dv1 + dv2;
        }

        /// <summary>
        /// Calcula um dígito verificador de CPF pelo algoritmo padrão de módulo 11.
        /// Os pesos começam em (tamanho do trecho + 1) e decrescem até 2.
        /// </summary>
        private static int CalculateCpfCheckDigit(string digits)
        {
            var sum = 0;
            var weight = digits.Length + 1;

            foreach (var c in digits)
            {
                sum += (c - '0') * weight;
                weight--;
            }

            var remainder = sum % 11;
            return remainder < 2 ? 0 : 11 - remainder;
        }

        /// <summary>
        /// Calcula um dígito verificador de CNPJ pelo algoritmo padrão de módulo 11.
        /// Os pesos seguem a sequência fixa 2..9 repetida da direita para a esquerda.
        /// </summary>
        private static int CalculateCnpjCheckDigit(string digits)
        {
            var sum = 0;
            var weight = 2;

            for (var i = digits.Length - 1; i >= 0; i--)
            {
                sum += (digits[i] - '0') * weight;
                weight = weight == 9 ? 2 : weight + 1;
            }

            var remainder = sum % 11;
            return remainder < 2 ? 0 : 11 - remainder;
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
            return length > 0 ? formatted.PadLeft(length, '0') : formatted;
        }

        private string GenerateInteger(int length)
        {
            var value = _random.Next(1, 999999);
            return length > 0 ? value.ToString().PadLeft(length, '0') : value.ToString();
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

        private string GenerateText(int length, string? context)
        {
            var words = new[] { "exemplo", "teste", "dados", "sinteticos", "gerado", "automaticamente" };
            var word = words[_random.Next(words.Length)];
            return length > 0 ? word.PadRight(length, ' ') : word;
        }
    }
}
