using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Enums;
using LayoutParserApi.Services.Generation.Interfaces;
using System.Text;

namespace LayoutParserApi.Services.Generation.Implementations
{
    /// <summary>
    /// Valida linhas geradas incrementalmente
    /// </summary>
    public class LineValidator : ILineValidator
    {
        private readonly ILogger<LineValidator> _logger;

        public LineValidator(ILogger<LineValidator> logger)
        {
            _logger = logger;
        }

        public LineValidationResult ValidateLine(string generatedLine, LineElement lineConfig, int expectedLength = 600)
        {
            var result = new LineValidationResult
            {
                LineName = lineConfig.Name,
                ExpectedLength = expectedLength,
                ActualLength = generatedLine?.Length ?? 0
            };

            if (string.IsNullOrEmpty(generatedLine))
            {
                result.Errors.Add("Linha gerada está vazia");
                result.IsValid = false;
                return result;
            }

            // Validar tamanho
            if (result.ActualLength != expectedLength)
            {
                result.Errors.Add($"Tamanho incorreto: {result.ActualLength} caracteres (esperado: {expectedLength})");
                result.IsValid = false;
            }

            // Validar InitialValue se existir
            if (!string.IsNullOrEmpty(lineConfig.InitialValue))
            {
                var expectedInitial = lineConfig.InitialValue;
                var actualInitial = generatedLine.Length >= expectedInitial.Length 
                    ? generatedLine.Substring(0, expectedInitial.Length) 
                    : generatedLine;

                if (!actualInitial.StartsWith(expectedInitial))
                {
                    result.Errors.Add($"Valor inicial incorreto: esperado '{expectedInitial}', encontrado '{actualInitial}'");
                    result.IsValid = false;
                }
            }

            // Validar campos
            var fieldValidation = ValidateFields(generatedLine, lineConfig);
            if (fieldValidation.HasErrors)
            {
                result.Errors.AddRange(fieldValidation.Details
                    .Where(d => d.Status == "error")
                    .Select(d => $"{d.FieldName}: {d.ErrorMessage}"));
                result.IsValid = false;
            }

            result.Warnings.AddRange(fieldValidation.Details
                .Where(d => d.Status == "warning")
                .Select(d => $"{d.FieldName}: {d.ErrorMessage}"));

            if (result.Errors.Count == 0)
            {
                result.IsValid = true;
            }

            return result;
        }

        public FieldValidationResult ValidateFields(string generatedLine, LineElement lineConfig)
        {
            var result = new FieldValidationResult();
            var parsedFields = new List<FieldValidationDetail>();

            if (lineConfig?.Elements == null || string.IsNullOrEmpty(generatedLine))
            {
                return result;
            }

            // Extrair campos do layout
            var fields = new List<FieldElement>();
            ExtractFieldsFromLineElement(lineConfig, fields);

            // Ordenar por sequência
            fields = fields.OrderBy(f => f.Sequence).ToList();

            // Calcular posição inicial (considerando InitialValue e Sequencia)
            int currentPosition = 0;
            
            // HEADER não tem Sequencia no início
            bool isHeader = lineConfig.Name?.Equals("HEADER", StringComparison.OrdinalIgnoreCase) == true;
            if (!isHeader)
            {
                currentPosition = 6; // Sequencia da linha anterior (6 caracteres)
            }

            // Adicionar InitialValue se existir
            if (!string.IsNullOrEmpty(lineConfig.InitialValue))
            {
                currentPosition += lineConfig.InitialValue.Length;
            }

            foreach (var field in fields)
            {
                // Ignorar campo Sequencia (ele pertence à próxima linha)
                if (field.Name?.Equals("Sequencia", StringComparison.OrdinalIgnoreCase) == true)
                    continue;

                var fieldDetail = new FieldValidationDetail
                {
                    FieldName = field.Name,
                    Start = currentPosition + 1, // 1-based
                    Length = field.LengthField
                };

                // Extrair valor do campo
                if (currentPosition + field.LengthField <= generatedLine.Length)
                {
                    fieldDetail.Value = generatedLine.Substring(currentPosition, field.LengthField);
                }
                else
                {
                    fieldDetail.Value = "";
                    fieldDetail.Status = "error";
                    fieldDetail.ErrorMessage = $"Campo fora dos limites da linha (posição {currentPosition + 1}-{currentPosition + field.LengthField})";
                    result.ErrorCount++;
                }

                // Validar campo
                if (string.IsNullOrEmpty(fieldDetail.Status))
                {
                    if (field.IsRequired && string.IsNullOrWhiteSpace(fieldDetail.Value))
                    {
                        fieldDetail.Status = "error";
                        fieldDetail.ErrorMessage = "Campo obrigatório está vazio";
                        result.ErrorCount++;
                    }
                    else if (fieldDetail.Value.Length < field.LengthField)
                    {
                        fieldDetail.Status = "warning";
                        fieldDetail.ErrorMessage = $"Campo incompleto: {fieldDetail.Value.Length}/{field.LengthField} caracteres";
                        result.WarningCount++;
                    }
                    else
                    {
                        fieldDetail.Status = "valid";
                        result.ValidFields++;
                    }
                }

                parsedFields.Add(fieldDetail);
                currentPosition += field.LengthField;
                result.TotalFields++;
            }

            result.Details = parsedFields;
            return result;
        }

        private void ExtractFieldsFromLineElement(LineElement lineElement, List<FieldElement> fields)
        {
            if (lineElement?.Elements == null)
                return;

            foreach (var elementJson in lineElement.Elements)
            {
                try
                {
                    var field = Newtonsoft.Json.JsonConvert.DeserializeObject<FieldElement>(elementJson);
                    if (field != null)
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
    }
}

