using LayoutParserApi.Models;

namespace LayoutParserApi.Services.Generation.Implementations
{
    /// <summary>
    /// Valida consistência matemática e lógica dos dados gerados
    /// </summary>
    public class ConsistencyValidator
    {
        private readonly ILogger<ConsistencyValidator> _logger;

        public ConsistencyValidator(ILogger<ConsistencyValidator> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Valida se o total da nota bate com a soma dos itens
        /// </summary>
        public ValidationResult ValidateTotalMatchesItems(decimal totalValue, List<decimal> itemValues, decimal tolerance = 0.01m)
        {
            var sum = itemValues?.Sum() ?? 0;
            var difference = Math.Abs(totalValue - sum);

            var result = new ValidationResult
            {
                IsValid = difference <= tolerance,
                ActualValue = totalValue,
                ExpectedValue = sum,
                Difference = difference,
                Message = difference <= tolerance ? $"Total válido: {totalValue} = soma dos itens {sum}" : $"Inconsistência: Total {totalValue} != Soma dos itens {sum} (diferença: {difference})"
            };

            if (!result.IsValid)
                _logger.LogWarning("Inconsistência detectada: Total {Total} != Soma {Sum} (diferença: {Diff})", totalValue, sum, difference);

            return result;
        }

        /// <summary>
        /// Valida se valores monetários estão em formato correto (sem separadores, apenas números)
        /// </summary>
        public ValidationResult ValidateMonetaryFormat(string value, int expectedLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return new ValidationResult
                {
                    IsValid = false,
                    Message = "Valor monetário vazio"
                };
            }

            // Deve conter apenas dígitos
            if (!value.All(char.IsDigit))
            {
                return new ValidationResult
                {
                    IsValid = false,
                    Message = $"Valor monetário contém caracteres não numéricos: {value}"
                };
            }

            // Deve ter o tamanho esperado
            if (value.Length != expectedLength)
            {
                return new ValidationResult
                {
                    IsValid = false,
                    Message = $"Valor monetário tem tamanho incorreto: {value.Length} (esperado: {expectedLength})"
                };
            }

            return new ValidationResult
            {
                IsValid = true,
                Message = "Formato monetário válido"
            };
        }

        /// <summary>
        /// Valida se campos FILLER estão vazios (apenas espaços)
        /// </summary>
        public ValidationResult ValidateFillerField(string value, string fieldName)
        {
            if (string.IsNullOrEmpty(value))
            {
                return new ValidationResult
                {
                    IsValid = true,
                    Message = "Campo FILLER vazio (correto)"
                };
            }

            // FILLER deve conter apenas espaços
            if (value.All(char.IsWhiteSpace))
            {
                return new ValidationResult
                {
                    IsValid = true,
                    Message = "Campo FILLER contém apenas espaços (correto)"
                };
            }

            // Se contém texto, está incorreto
            var nonSpaceChars = value.Where(c => !char.IsWhiteSpace(c)).ToList();
            if (nonSpaceChars.Any())
            {
                _logger.LogWarning("Campo FILLER {FieldName} contém texto quando deveria estar vazio: '{Value}'", fieldName, value.Trim());

                return new ValidationResult
                {
                    IsValid = false,
                    Message = $"Campo FILLER {fieldName} contém texto: '{value.Trim()}' (deveria estar vazio)"
                };
            }

            return new ValidationResult { IsValid = true };
        }

        /// <summary>
        /// Valida consistência de sequências (ex: número da nota deve ser único)
        /// </summary>
        public ValidationResult ValidateSequenceConsistency(List<string> sequences, string fieldName)
        {
            if (sequences == null || !sequences.Any())
            {
                return new ValidationResult
                {
                    IsValid = false,
                    Message = $"Lista de sequências vazia para campo {fieldName}"
                };
            }

            var duplicates = sequences.GroupBy(s => s).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

            if (duplicates.Any())
            {
                _logger.LogWarning("Sequências duplicadas encontradas no campo {FieldName}: {Duplicates}", fieldName, string.Join(", ", duplicates));

                return new ValidationResult
                {
                    IsValid = false,
                    Message = $"Sequências duplicadas em {fieldName}: {string.Join(", ", duplicates)}"
                };
            }

            return new ValidationResult
            {
                IsValid = true,
                Message = $"Todas as {sequences.Count} sequências são únicas"
            };
        }
    }
}