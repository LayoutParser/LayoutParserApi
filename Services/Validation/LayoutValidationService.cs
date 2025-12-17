using LayoutParserApi.Models.Database;
using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Validation;
using LayoutParserApi.Models.Logging;
using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Parsing.Interfaces;
using Newtonsoft.Json;
using System.Xml.Linq;

namespace LayoutParserApi.Services.Validation
{
    /// <summary>
    /// Serviço para validação de layouts - garante que cada linha tenha exatamente 600 posições
    /// </summary>
    public class LayoutValidationService
    {
        private readonly ICachedLayoutService _layoutService;
        private readonly ILayoutValidator _layoutValidator;
        private readonly ITechLogger _techLogger;
        private readonly ILogger<LayoutValidationService> _logger;
        private static Dictionary<string, LayoutValidationResult> _validationCache = new();
        private static DateTime _lastFullValidation = DateTime.MinValue;

        public LayoutValidationService(
            ICachedLayoutService layoutService,
            ILayoutValidator layoutValidator,
            ITechLogger techLogger,
            ILogger<LayoutValidationService> logger)
        {
            _layoutService = layoutService;
            _layoutValidator = layoutValidator;
            _techLogger = techLogger;
            _logger = logger;
        }

        /// <summary>
        /// Valida um layout específico por GUID
        /// </summary>
        public async Task<LayoutValidationResult> ValidateLayoutByGuidAsync(string layoutGuid)
        {
            try
            {
                _logger.LogInformation("Validando layout por GUID: {LayoutGuid}", layoutGuid);

                // Buscar layout no banco
                var layoutRecord = await _layoutService.GetLayoutByGuidAsync(layoutGuid);
                if (layoutRecord == null)
                {
                    return new LayoutValidationResult
                    {
                        LayoutGuid = layoutGuid,
                        IsValid = false,
                        Errors = new List<LineValidationError>
                        {
                            new LineValidationError
                            {
                                LineName = "N/A",
                                ErrorMessage = $"Layout não encontrado: {layoutGuid}"
                            }
                        }
                    };
                }

                // Parse do XML do layout
                var layout = ParseLayoutFromXml(layoutRecord.DecryptedContent);
                if (layout == null)
                {
                    return new LayoutValidationResult
                    {
                        LayoutGuid = layoutGuid,
                        LayoutName = layoutRecord.Name,
                        IsValid = false,
                        Errors = new List<LineValidationError>
                        {
                            new LineValidationError
                            {
                                LineName = "N/A",
                                ErrorMessage = "Erro ao parsear XML do layout"
                            }
                        }
                    };
                }

                // Validar cada linha do layout
                var validationResult = ValidateLayoutLines(layout);
                validationResult.LayoutGuid = layoutGuid;
                validationResult.LayoutName = layoutRecord.Name;
                validationResult.ValidatedAt = DateTime.UtcNow;

                // Cachear resultado
                _validationCache[layoutGuid] = validationResult;

                return validationResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao validar layout {LayoutGuid}", layoutGuid);
                return new LayoutValidationResult
                {
                    LayoutGuid = layoutGuid,
                    IsValid = false,
                    Errors = new List<LineValidationError>
                    {
                        new LineValidationError
                        {
                            LineName = "N/A",
                            ErrorMessage = $"Erro na validação: {ex.Message}"
                        }
                    }
                };
            }
        }

        /// <summary>
        /// Valida múltiplos layouts por GUID
        /// </summary>
        public async Task<List<LayoutValidationResult>> ValidateLayoutsByGuidsAsync(List<string> layoutGuids)
        {
            var results = new List<LayoutValidationResult>();

            foreach (var guid in layoutGuids)
            {
                var result = await ValidateLayoutByGuidAsync(guid);
                results.Add(result);
            }

            return results;
        }

        /// <summary>
        /// Valida todos os layouts do banco (para execução na inicialização e diariamente)
        /// </summary>
        public async Task<List<LayoutValidationResult>> ValidateAllLayoutsAsync(bool forceRevalidation = false)
        {
            try
            {
                _logger.LogInformation("Iniciando validação de todos os layouts. ForceRevalidation: {Force}", forceRevalidation);

                // Buscar todos os layouts
                var request = new LayoutSearchRequest
                {
                    SearchTerm = "",
                    MaxResults = 1000
                };

                var searchResponse = await _layoutService.SearchLayoutsAsync(request);
                if (!searchResponse.Success || !searchResponse.Layouts.Any())
                {
                    _logger.LogWarning("Nenhum layout encontrado para validação");
                    return new List<LayoutValidationResult>();
                }

                var results = new List<LayoutValidationResult>();

                foreach (var layoutRecord in searchResponse.Layouts)
                {
                    var layoutGuid = layoutRecord.LayoutGuid.ToString();

                    // Verificar cache se não for forçado
                    if (!forceRevalidation && _validationCache.ContainsKey(layoutGuid))
                    {
                        var cached = _validationCache[layoutGuid];
                        // Se validado há menos de 24 horas, usar cache
                        if (cached.ValidatedAt > DateTime.UtcNow.AddHours(-24))
                        {
                            results.Add(cached);
                            continue;
                        }
                    }

                    var result = await ValidateLayoutByGuidAsync(layoutGuid);
                    results.Add(result);
                }

                _lastFullValidation = DateTime.UtcNow;
                _logger.LogInformation("Validação completa concluída: {Total} layouts, {Invalid} inválidos",
                    results.Count, results.Count(r => !r.IsValid));

                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao validar todos os layouts");
                throw;
            }
        }

        /// <summary>
        /// Valida as linhas de um layout parseado
        /// </summary>
        private LayoutValidationResult ValidateLayoutLines(Layout layout)
        {
            var result = new LayoutValidationResult
            {
                TotalLines = layout.Elements.Count,
                Errors = new List<LineValidationError>()
            };

            foreach (var lineElement in layout.Elements)
            {
                var lineError = ValidateLineElement(lineElement);
                if (lineError != null)
                {
                    result.Errors.Add(lineError);
                    result.InvalidLines++;
                }
                else
                {
                    result.ValidLines++;
                }
            }

            result.IsValid = result.Errors.Count == 0;
            return result;
        }

        /// <summary>
        /// Valida um LineElement individual
        /// </summary>
        private LineValidationError? ValidateLineElement(LineElement lineConfig)
        {
            try
            {
                // Separar campos de linhas filhas
                var (fieldElements, childLineElements) = SeparateElements(lineConfig);

                // Calcular tamanho total da linha
                int initialValueLength = !string.IsNullOrEmpty(lineConfig.InitialValue) 
                    ? lineConfig.InitialValue.Length 
                    : 0;

                var fieldsToCalculate = fieldElements
                    .Where(f => !f.Name.Equals("Sequencia", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f.Sequence)
                    .ToList();

                int fieldsLength = fieldsToCalculate.Sum(f => f.LengthField);

                // HEADER não tem sequência da linha anterior, outras linhas têm 6 caracteres
                int sequenceFromPreviousLine = lineConfig.Name?.Equals("HEADER", StringComparison.OrdinalIgnoreCase) == true 
                    ? 0 
                    : 6;

                int totalLength = initialValueLength + fieldsLength + sequenceFromPreviousLine;

                // Linhas com filhos podem ter tamanho variável, mas devem ser <= 600
                // Linhas sem filhos devem ter exatamente 600
                bool hasChildren = childLineElements.Any();
                bool isValid = hasChildren ? totalLength <= 600 : totalLength == 600;

                if (!isValid)
                {
                    int difference = 600 - totalLength;
                    return new LineValidationError
                    {
                        LineName = lineConfig.Name ?? "UNKNOWN",
                        ExpectedLength = 600,
                        ActualLength = totalLength,
                        Difference = difference,
                        InitialValue = lineConfig.InitialValue ?? "",
                        FieldCount = fieldsToCalculate.Count,
                        HasChildren = hasChildren,
                        ErrorMessage = hasChildren
                            ? $"Linha com filhos tem {totalLength} caracteres (máximo permitido: 600)"
                            : $"Linha tem {totalLength} caracteres (esperado: 600). {(difference > 0 ? "Faltam" : "Sobram")} {Math.Abs(difference)} caracteres."
                    };
                }

                return null; // Linha válida
            }
            catch (Exception ex)
            {
                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ValidateLineElement",
                    Level = "Error",
                    Message = $"Erro ao validar linha {lineConfig.Name}: {ex.Message}"
                });

                return new LineValidationError
                {
                    LineName = lineConfig.Name ?? "UNKNOWN",
                    ErrorMessage = $"Erro na validação: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Separa FieldElements de LineElements filhos
        /// </summary>
        private (List<FieldElement> fieldElements, List<LineElement> childLineElements) SeparateElements(LineElement lineElement)
        {
            var fieldElements = new List<FieldElement>();
            var childLineElements = new List<LineElement>();

            if (lineElement?.Elements == null)
                return (fieldElements, childLineElements);

            foreach (var elementJson in lineElement.Elements)
            {
                // Tentar detectar tipo pelo JSON
                bool isLineElement = elementJson.Contains("\"Type\":\"LineElementVO\"") || 
                                     elementJson.Contains("\"Name\":\"LINHA");
                bool isFieldElement = elementJson.Contains("\"Type\":\"FieldElementVO\"") ||
                                      elementJson.Contains("\"LengthField\"");

                if (isFieldElement)
                {
                    try
                    {
                        var field = JsonConvert.DeserializeObject<FieldElement>(elementJson);
                        if (field != null && !string.IsNullOrEmpty(field.Name))
                            fieldElements.Add(field);
                    }
                    catch
                    {
                        // Ignorar erro de deserialização
                    }
                }
                else if (isLineElement)
                {
                    try
                    {
                        var childLine = JsonConvert.DeserializeObject<LineElement>(elementJson);
                        if (childLine != null && !string.IsNullOrEmpty(childLine.Name) && childLine.Name != lineElement.Name)
                            childLineElements.Add(childLine);
                    }
                    catch
                    {
                        // Ignorar erro de deserialização
                    }
                }
            }

            return (fieldElements, childLineElements);
        }

        /// <summary>
        /// Parse do XML do layout (similar ao LayoutParserService)
        /// </summary>
        private Layout? ParseLayoutFromXml(string xmlContent)
        {
            try
            {
                var xdoc = XDocument.Parse(xmlContent);
                var root = xdoc.Root;
                if (root == null) return null;

                XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";

                var layout = new Layout
                {
                    LayoutGuid = root.Element("LayoutGuid")?.Value ?? "",
                    LayoutType = root.Element("LayoutType")?.Value ?? "",
                    Name = root.Element("Name")?.Value ?? "",
                    Description = root.Element("Description")?.Value ?? "",
                    LimitOfCaracters = int.TryParse(root.Element("LimitOfCaracters")?.Value, out var limit) ? limit : 600,
                    Elements = new List<LineElement>()
                };

                var elements = root.Element("Elements");
                if (elements != null)
                {
                    foreach (var lineElem in elements.Elements("Element"))
                    {
                        var typeAttr = lineElem.Attribute(xsi + "type") ?? lineElem.Attribute("type");
                        if (typeAttr?.Value == "LineElementVO")
                        {
                            var line = ParseLineElementRecursive(lineElem, xsi);
                            if (line != null)
                                layout.Elements.Add(line);
                        }
                    }
                }

                return layout;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao parsear XML do layout");
                return null;
            }
        }

        private LineElement? ParseLineElementRecursive(XElement lineElem, XNamespace xsi)
        {
            try
            {
                var line = new LineElement
                {
                    ElementGuid = lineElem.Element("ElementGuid")?.Value ?? "",
                    Name = lineElem.Element("Name")?.Value ?? "",
                    Description = lineElem.Element("Description")?.Value ?? "",
                    Sequence = int.TryParse(lineElem.Element("Sequence")?.Value, out var seq) ? seq : 0,
                    IsRequired = bool.TryParse(lineElem.Element("IsRequired")?.Value, out var req) && req,
                    InitialValue = lineElem.Element("InitialValue")?.Value ?? "",
                    Elements = new List<string>()
                };

                var lineElements = lineElem.Element("Elements");
                if (lineElements != null)
                {
                    foreach (var childElem in lineElements.Elements("Element"))
                    {
                        var childTypeAttr = childElem.Attribute(xsi + "type") ?? childElem.Attribute("type");
                        if (childTypeAttr?.Value == "FieldElementVO")
                        {
                            var field = new FieldElement
                            {
                                ElementGuid = childElem.Element("ElementGuid")?.Value ?? "",
                                Name = childElem.Element("Name")?.Value ?? "",
                                Sequence = int.TryParse(childElem.Element("Sequence")?.Value, out var fseq) ? fseq : 0,
                                StartValue = int.TryParse(childElem.Element("StartValue")?.Value, out var start) ? start : 0,
                                LengthField = int.TryParse(childElem.Element("LengthField")?.Value, out var len) ? len : 0
                            };
                            line.Elements.Add(JsonConvert.SerializeObject(field));
                        }
                        else if (childTypeAttr?.Value == "LineElementVO")
                        {
                            // Linha filha - serializar como JSON para manter estrutura
                            var childLine = ParseLineElementRecursive(childElem, xsi);
                            if (childLine != null)
                                line.Elements.Add(JsonConvert.SerializeObject(childLine));
                        }
                    }
                }

                return line;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao parsear LineElement recursivo");
                return null;
            }
        }

        /// <summary>
        /// Obtém resultado de validação do cache
        /// </summary>
        public LayoutValidationResult? GetCachedValidation(string layoutGuid)
        {
            return _validationCache.TryGetValue(layoutGuid, out var result) ? result : null;
        }

        /// <summary>
        /// Verifica se precisa revalidar (última validação foi há mais de 24h)
        /// </summary>
        public bool NeedsRevalidation()
        {
            return _lastFullValidation < DateTime.UtcNow.AddHours(-24);
        }
    }
}

