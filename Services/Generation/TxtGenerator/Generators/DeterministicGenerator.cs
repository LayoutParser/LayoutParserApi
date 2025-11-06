using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LayoutParserApi.Services.Generation.TxtGenerator.Models;

namespace LayoutParserApi.Services.Generation.TxtGenerator.Generators
{
    /// <summary>
    /// Gerador determinístico: valores fixos e previsíveis
    /// </summary>
    public class DeterministicGenerator : IFieldValueGenerator
    {
        private readonly ILogger<DeterministicGenerator> _logger;

        public DeterministicGenerator(ILogger<DeterministicGenerator> logger)
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

            // Se tem domínio, usar primeiro valor do domínio
            if (!string.IsNullOrEmpty(field.Domain))
            {
                var domainValues = field.Domain.Split(',', StringSplitOptions.RemoveEmptyEntries);
                if (domainValues.Any())
                {
                    var value = domainValues[0].Trim();
                    _logger.LogDebug("Campo {FieldName} gerado do domínio: {Value}", field.Name, value);
                    return FormatValue(value, field.Length, field.Alignment);
                }
            }

            // Gerar valor determinístico baseado no tipo
            var generatedValue = GenerateDeterministicValue(field, recordIndex);
            _logger.LogDebug("Campo {FieldName} gerado deterministicamente: {Value}", field.Name, generatedValue);
            return FormatValue(generatedValue, field.Length, field.Alignment);
        }

        private string GenerateDeterministicValue(FieldDefinition field, int recordIndex)
        {
            return field.DataType.ToLower() switch
            {
                "cnpj" => "12345678000190", // CNPJ fixo válido
                "cpf" => "12345678909", // CPF fixo válido
                "date" => DateTime.Now.ToString("yyyyMMdd"),
                "time" => DateTime.Now.ToString("HHmmss"),
                "datetime" => DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                "decimal" => "000000000000100", // R$ 1,00 em centavos
                "int" => (recordIndex + 1).ToString().PadLeft(field.Length, '0'),
                "email" => "teste@exemplo.com",
                "filler" => new string(' ', field.Length),
                "string" => field.Example ?? $"CAMPO_{field.Name}",
                _ => new string(' ', field.Length)
            };
        }

        private string FormatValue(string value, int length, string alignment)
        {
            if (string.IsNullOrEmpty(value))
                value = "";

            // Truncar se necessário
            if (value.Length > length)
                value = value.Substring(0, length);

            // Preencher conforme alinhamento
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
    }
}

