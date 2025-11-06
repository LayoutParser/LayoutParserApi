using System;
using System.Collections.Generic;
using System.Linq;
using LayoutParserApi.Services.Generation.TxtGenerator.Models;

namespace LayoutParserApi.Services.Generation.TxtGenerator.Validators
{
    /// <summary>
    /// Validador para garantir que o resultado final obedece ao layout
    /// </summary>
    public class LayoutValidator
    {
        private readonly ILogger<LayoutValidator> _logger;

        public LayoutValidator(ILogger<LayoutValidator> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Valida uma linha gerada contra o layout
        /// </summary>
        public ValidationResult ValidateLine(string generatedLine, RecordLayout recordLayout)
        {
            var result = new ValidationResult
            {
                IsValid = true,
                Errors = new List<string>(),
                Warnings = new List<string>()
            };

            // Validar tamanho total
            if (generatedLine.Length != recordLayout.TotalLength)
            {
                result.IsValid = false;
                result.Errors.Add($"Tamanho incorreto: {generatedLine.Length} caracteres (esperado: {recordLayout.TotalLength})");
            }

            // Validar InitialValue
            if (!string.IsNullOrEmpty(recordLayout.InitialValue))
            {
                if (!generatedLine.StartsWith(recordLayout.InitialValue))
                {
                    result.IsValid = false;
                    result.Errors.Add($"Valor inicial incorreto: esperado '{recordLayout.InitialValue}' no início");
                }
            }

            // Validar cada campo
            foreach (var field in recordLayout.Fields)
            {
                var fieldValidation = ValidateField(generatedLine, field);
                if (!fieldValidation.IsValid)
                {
                    result.IsValid = false;
                    result.Errors.AddRange(fieldValidation.Errors);
                }
                result.Warnings.AddRange(fieldValidation.Warnings);
            }

            return result;
        }

        /// <summary>
        /// Valida um campo específico
        /// </summary>
        public FieldValidationResult ValidateField(string line, FieldDefinition field)
        {
            var result = new FieldValidationResult
            {
                FieldName = field.Name,
                IsValid = true,
                Errors = new List<string>(),
                Warnings = new List<string>()
            };

            // Verificar se a posição está dentro dos limites
            if (field.EndPosition >= line.Length)
            {
                result.IsValid = false;
                result.Errors.Add($"Campo {field.Name}: posição {field.EndPosition} fora dos limites da linha ({line.Length})");
                return result;
            }

            // Extrair valor do campo
            var startIndex = field.StartPosition;
            var length = field.Length;
            var value = line.Substring(startIndex, Math.Min(length, line.Length - startIndex));

            // Validar tamanho
            if (value.Length != field.Length)
            {
                result.IsValid = false;
                result.Errors.Add($"Campo {field.Name}: tamanho {value.Length} (esperado: {field.Length})");
            }

            // Validar obrigatoriedade
            if (field.IsRequired && string.IsNullOrWhiteSpace(value))
            {
                result.IsValid = false;
                result.Errors.Add($"Campo {field.Name}: obrigatório mas está vazio");
            }

            // Validar tipo de dado
            var typeValidation = ValidateDataType(value, field);
            if (!typeValidation.IsValid)
            {
                result.IsValid = false;
                result.Errors.AddRange(typeValidation.Errors);
            }

            // Validar domínio (se especificado)
            if (!string.IsNullOrEmpty(field.Domain))
            {
                var domainValues = field.Domain.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(v => v.Trim())
                    .ToList();

                if (!domainValues.Contains(value.Trim()))
                {
                    result.Warnings.Add($"Campo {field.Name}: valor '{value.Trim()}' não está no domínio permitido");
                }
            }

            // Validar FILLER (deve estar vazio)
            if (field.DataType == "filler" && !string.IsNullOrWhiteSpace(value.Trim()))
            {
                result.IsValid = false;
                result.Errors.Add($"Campo FILLER {field.Name}: deve estar vazio, mas contém '{value.Trim()}'");
            }

            return result;
        }

        /// <summary>
        /// Valida o tipo de dado do valor
        /// </summary>
        private ValidationResult ValidateDataType(string value, FieldDefinition field)
        {
            var result = new ValidationResult { IsValid = true, Errors = new List<string>() };

            switch (field.DataType.ToLower())
            {
                case "int":
                    if (!long.TryParse(value.Trim(), out _))
                    {
                        result.IsValid = false;
                        result.Errors.Add($"Campo {field.Name}: valor '{value}' não é um número inteiro válido");
                    }
                    break;

                case "decimal":
                    // Remover espaços e tentar parsear
                    var cleanValue = value.Trim().Replace(" ", "");
                    if (!decimal.TryParse(cleanValue, out _))
                    {
                        result.IsValid = false;
                        result.Errors.Add($"Campo {field.Name}: valor '{value}' não é um número decimal válido");
                    }
                    break;

                case "cnpj":
                    if (value.Trim().Length != 14 || !value.Trim().All(char.IsDigit))
                    {
                        result.IsValid = false;
                        result.Errors.Add($"Campo {field.Name}: CNPJ inválido (deve ter 14 dígitos)");
                    }
                    break;

                case "cpf":
                    if (value.Trim().Length != 11 || !value.Trim().All(char.IsDigit))
                    {
                        result.IsValid = false;
                        result.Errors.Add($"Campo {field.Name}: CPF inválido (deve ter 11 dígitos)");
                    }
                    break;

                case "date":
                    if (value.Trim().Length == 8 && !DateTime.TryParseExact(value.Trim(), "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out _))
                    {
                        result.IsValid = false;
                        result.Errors.Add($"Campo {field.Name}: data inválida (formato esperado: yyyyMMdd)");
                    }
                    break;
            }

            return result;
        }

        /// <summary>
        /// Valida um arquivo completo
        /// </summary>
        public FileValidationResult ValidateFile(List<string> lines, FileLayout fileLayout)
        {
            var result = new FileValidationResult
            {
                IsValid = true,
                TotalLines = lines.Count,
                ValidLines = 0,
                InvalidLines = 0,
                Errors = new List<string>(),
                Warnings = new List<string>()
            };

            var recordIndex = 0;
            foreach (var recordLayout in fileLayout.Records)
            {
                var occurrences = 0;
                var maxOccurrences = recordLayout.MaximumOccurrence;

                for (int i = 0; i < maxOccurrences && recordIndex < lines.Count; i++)
                {
                    var line = lines[recordIndex];
                    var lineValidation = ValidateLine(line, recordLayout);

                    if (lineValidation.IsValid)
                    {
                        result.ValidLines++;
                        occurrences++;
                    }
                    else
                    {
                        result.InvalidLines++;
                        result.Errors.Add($"Linha {recordIndex + 1} ({recordLayout.Name}): {string.Join("; ", lineValidation.Errors)}");
                    }

                    result.Warnings.AddRange(lineValidation.Warnings.Select(w => $"Linha {recordIndex + 1}: {w}"));
                    recordIndex++;
                }

                // Validar ocorrências mínimas
                if (occurrences < recordLayout.MinimalOccurrence)
                {
                    result.IsValid = false;
                    result.Errors.Add($"Registro {recordLayout.Name}: apenas {occurrences} ocorrências (mínimo: {recordLayout.MinimalOccurrence})");
                }
            }

            result.IsValid = result.InvalidLines == 0 && result.Errors.Count == 0;
            return result;
        }
    }

    public class ValidationResult
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }

    public class FieldValidationResult : ValidationResult
    {
        public string FieldName { get; set; }
    }

    public class FileValidationResult : ValidationResult
    {
        public int TotalLines { get; set; }
        public int ValidLines { get; set; }
        public int InvalidLines { get; set; }
    }
}

