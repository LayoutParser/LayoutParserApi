using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Logging;
using LayoutParserApi.Models.Structure;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Parsing.Interfaces;

using Newtonsoft.Json;

namespace LayoutParserApi.Services.Parsing.Implementations
{
    public class LayoutValidator : ILayoutValidator
    {
        private readonly ITechLogger _techLogger;

        public LayoutValidator(ITechLogger techLogger)
        {
            _techLogger = techLogger;
        }

        public void ValidateCompleteLayout(Layout layout)
        {
            try
            {
                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ValidateCompleteLayout",
                    Level = "Info",
                    Message = "=== VALIDAÇÃO COMPLETA DO LAYOUT ==="
                });

                if (layout?.Elements == null)
                {
                    _techLogger.LogTechnical(new TechLogEntry
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        Endpoint = "ValidateCompleteLayout",
                        Level = "Error",
                        Message = "Layout ou Elements é nulo"
                    });
                    return;
                }

                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ValidateCompleteLayout",
                    Level = "Info",
                    Message = $"Total de linhas no layout: {layout.Elements.Count}"
                });

                var validationResults = new List<LineValidationResult>();

                foreach (var line in layout.Elements)
                {
                    ValidateLineLayoutWithResult(line, validationResults);
                }

                ShowValidationSummary(validationResults);

                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ValidateCompleteLayout",
                    Level = "Info",
                    Message = "=== FIM DA VALIDAÇÃO ==="
                });

            }
            catch (Exception ex)
            {
                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ValidateCompleteLayout",
                    Level = "Error",
                    Message = $"Erro na validação completa: {ex.Message}"
                });
            }
        }

        private void ValidateLineLayoutWithResult(LineElement lineConfig, List<LineValidationResult> results)
        {
            try
            {
                var (fieldElements, childLineElements) = SeparateElementsRobust(lineConfig);

                int initialValueLength = !string.IsNullOrEmpty(lineConfig.InitialValue) ? lineConfig.InitialValue.Length : 0;

                // Calcular todos os FieldElements EXCETO "Sequencia" (que pertence à próxima linha)
                var fieldsToCalculate = fieldElements
                    .Where(f => !f.Name.Equals("Sequencia", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f.Sequence)
                    .ToList();

                int fieldsLength = fieldsToCalculate.Sum(f => f.LengthField);

                // Adicionar 6 chars (Sequencia da linha anterior) para todas as linhas EXCETO HEADER
                int sequenceFromPreviousLine = lineConfig.Name?.Equals("HEADER", StringComparison.OrdinalIgnoreCase) == true ? 0 : 6;

                int totalLength = initialValueLength + fieldsLength + sequenceFromPreviousLine;

                bool hasChildren = childLineElements.Any();
                bool isValid = hasChildren ? totalLength <= 600 : totalLength == 600;

                var result = new LineValidationResult
                {
                    LineName = lineConfig.Name,
                    InitialValue = lineConfig.InitialValue,
                    TotalLength = totalLength,
                    IsValid = isValid,
                    HasChildren = hasChildren,
                    FieldCount = fieldsToCalculate.Count,
                    ChildCount = childLineElements.Count
                };

                results.Add(result);

                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ValidateLineLayout",
                    Level = "Info",
                    Message = $"=== VALIDAÇÃO DO LAYOUT: {lineConfig.Name} ==="
                });

                if (childLineElements.Any())
                {
                    foreach (var childLine in childLineElements)
                    {
                        ValidateLineLayoutWithResult(childLine, results);
                    }
                }

            }
            catch (Exception ex)
            {
                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ValidateLineLayout",
                    Level = "Error",
                    Message = $"Erro na validação do layout {lineConfig.Name}: {ex.Message}"
                });
            }
        }

        private void ShowValidationSummary(List<LineValidationResult> results)
        {
            _techLogger.LogTechnical(new TechLogEntry
            {
                RequestId = Guid.NewGuid().ToString(),
                Endpoint = "ValidateCompleteLayout",
                Level = "Info",
                Message = "=== RESUMO DA VALIDAÇÃO ==="
            });

            var validLines = results.Where(r => r.IsValid && !r.HasChildren && r.TotalLength == 600).ToList();
            if (validLines.Any())
            {
                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ValidateCompleteLayout",
                    Level = "Info",
                    Message = $"LINHAS VÁLIDAS (600 caracteres): {validLines.Count}"
                });

                foreach (var line in validLines.OrderBy(r => r.LineName))
                {
                    _techLogger.LogTechnical(new TechLogEntry
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        Endpoint = "ValidateCompleteLayout",
                        Level = "Info",
                        Message = $"{line.LineName}: {line.TotalLength} chars (InitialValue: '{line.InitialValue}', Campos: {line.FieldCount})"
                    });
                }
            }

            var linesWithChildren = results.Where(r => r.HasChildren).ToList();
            if (linesWithChildren.Any())
            {
                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ValidateCompleteLayout",
                    Level = "Info",
                    Message = $"LINHAS COM ELEMENTOS FILHOS: {linesWithChildren.Count}"
                });

                foreach (var line in linesWithChildren.OrderBy(r => r.LineName))
                {
                    _techLogger.LogTechnical(new TechLogEntry
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        Endpoint = "ValidateCompleteLayout",
                        Level = "Info",
                        Message = $"{line.LineName}: {line.TotalLength} chars (Filhos: {line.ChildCount}, Campos próprios: {line.FieldCount})"
                    });
                }
            }

            var invalidLines = results.Where(r => !r.IsValid && !r.HasChildren).ToList();
            if (invalidLines.Any())
            {
                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ValidateCompleteLayout",
                    Level = "Error",
                    Message = $"LINHAS INVÁLIDAS: {invalidLines.Count}"
                });

                foreach (var line in invalidLines.OrderBy(r => r.LineName))
                {
                    int difference = 600 - line.TotalLength;
                    _techLogger.LogTechnical(new TechLogEntry
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        Endpoint = "ValidateCompleteLayout",
                        Level = "Error",
                        Message = $"{line.LineName}: {line.TotalLength} chars ({(difference > 0 ? "FALTAM" : "SOBRAM")} {Math.Abs(difference)}) - InitialValue: '{line.InitialValue}'"
                    });
                }
            }

            var validNonStandardLines = results.Where(r => r.IsValid && r.HasChildren && r.TotalLength != 600).ToList();
            if (validNonStandardLines.Any())
            {
                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ValidateCompleteLayout",
                    Level = "Warn",
                    Message = $"LINHAS COM FILHOS (tamanho variável): {validNonStandardLines.Count}"
                });

                foreach (var line in validNonStandardLines.OrderBy(r => r.LineName))
                {
                    _techLogger.LogTechnical(new TechLogEntry
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        Endpoint = "ValidateCompleteLayout",
                        Level = "Warn",
                        Message = $"{line.LineName}: {line.TotalLength} chars (Filhos: {line.ChildCount}, Campos próprios: {line.FieldCount})"
                    });
                }
            }

            _techLogger.LogTechnical(new TechLogEntry
            {
                RequestId = Guid.NewGuid().ToString(),
                Endpoint = "ValidateCompleteLayout",
                Level = "Info",
                Message = $"ESTATÍSTICAS: Total de {results.Count} linhas validadas - {validLines.Count} válidas, {invalidLines.Count} inválidas, {linesWithChildren.Count} com filhos"
            });
        }

        private (List<FieldElement> fieldElements, List<LineElement> childLineElements) SeparateElementsRobust(LineElement lineElement)
        {
            var fieldElements = new List<FieldElement>();
            var childLineElements = new List<LineElement>();

            if (lineElement?.Elements == null)
                return (fieldElements, childLineElements);

            foreach (var elementJson in lineElement.Elements)
            {
                // Verificar o tipo do elemento antes de desserializar
                bool isLineElement = elementJson.Contains("\"Type\":\"LineElementVO\"");
                bool isFieldElement = elementJson.Contains("\"Type\":\"FieldElementVO\"");

                if (isFieldElement)
                {
                    try
                    {
                        var field = JsonConvert.DeserializeObject<FieldElement>(elementJson);
                        if (field != null && !string.IsNullOrEmpty(field.Name))
                        {
                            fieldElements.Add(field);
                        }
                    }
                    catch (Exception ex)
                    {
                        _techLogger.LogTechnical(new TechLogEntry
                        {
                            RequestId = Guid.NewGuid().ToString(),
                            Endpoint = "SeparateElementsRobust",
                            Level = "Error",
                            Message = $"Erro ao desserializar FieldElement: {ex.Message}"
                        });
                    }
                }
                else if (isLineElement)
                {
                    try
                    {
                        var childLine = JsonConvert.DeserializeObject<LineElement>(elementJson);
                        if (childLine != null && !string.IsNullOrEmpty(childLine.Name) && childLine.Name != lineElement.Name)
                        {
                            childLineElements.Add(childLine);
                            _techLogger.LogTechnical(new TechLogEntry
                            {
                                RequestId = Guid.NewGuid().ToString(),
                                Endpoint = "SeparateElementsRobust",
                                Level = "Info",
                                Message = $"✅ LineElement FILHO encontrado: {childLine.Name} dentro de {lineElement.Name}"
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _techLogger.LogTechnical(new TechLogEntry
                        {
                            RequestId = Guid.NewGuid().ToString(),
                            Endpoint = "SeparateElementsRobust",
                            Level = "Error",
                            Message = $"Erro ao desserializar LineElement: {ex.Message}"
                        });
                    }
                }
            }

            if (childLineElements.Any())
            {
                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "SeparateElementsRobust",
                    Level = "Info",
                    Message = $"📊 SEPARAÇÃO EM {lineElement.Name}: {fieldElements.Count} FieldElements, {childLineElements.Count} LineElements filhos ({string.Join(", ", childLineElements.Select(c => c.Name))})"
                });
            }

            return (fieldElements, childLineElements);
        }
    }
}


