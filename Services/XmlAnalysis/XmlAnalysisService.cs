using LayoutParserApi.Models.Entities;
using LayoutParserApi.Services.XmlAnalysis.Models;

using Newtonsoft.Json;

using System.Xml.Linq;

namespace LayoutParserApi.Services.XmlAnalysis
{
    /// <summary>
    /// Serviço para análise e validação de arquivos XML
    /// </summary>
    public class XmlAnalysisService
    {
        private readonly ILogger<XmlAnalysisService> _logger;

        public XmlAnalysisService(ILogger<XmlAnalysisService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Analisa e valida um arquivo XML
        /// </summary>
        public async Task<XmlAnalysisResult> AnalyzeXmlAsync(string xmlContent, Layout layout = null)
        {
            var result = new XmlAnalysisResult
            {
                Success = true,
                Errors = new List<string>(),
                Warnings = new List<string>(),
                ValidationDetails = new Dictionary<string, object>()
            };

            try
            {
                // 1. Parse do XML usando XDocument (que já valida estrutura corretamente, incluindo namespaces)
                // XDocument.Parse é a melhor forma de validar XML, pois lida corretamente com:
                // - Declarações XML (<?xml ... ?>)
                // - Namespaces
                // - Comentários, CDATA, etc.
                XDocument doc;
                try
                {
                    doc = XDocument.Parse(xmlContent, LoadOptions.None);
                }
                catch (System.Xml.XmlException xmlEx)
                {
                    result.Success = false;
                    result.Errors.Add($"Erro de estrutura XML: {xmlEx.Message}");
                    if (xmlEx.LineNumber > 0)
                        result.Errors.Add($"Linha {xmlEx.LineNumber}, Posição {xmlEx.LinePosition}");

                    _logger.LogWarning("XML inválido estruturalmente: {Errors}", string.Join(", ", result.Errors));
                    return result;
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Errors.Add($"Erro ao fazer parse do XML: {ex.Message}");
                    _logger.LogWarning("Erro ao parsear XML: {Error}", ex.Message);
                    return result;
                }

                // 2. Análise de estrutura
                var structureAnalysis = AnalyzeXmlStructure(doc);
                result.ValidationDetails["structure"] = structureAnalysis;
                result.TotalElements = structureAnalysis.TotalElements;
                result.TotalAttributes = structureAnalysis.TotalAttributes;
                result.Depth = structureAnalysis.MaxDepth;

                // 3. Validação contra layout (se fornecido)
                if (layout != null)
                {
                    var layoutValidation = ValidateAgainstLayout(doc, layout);
                    result.ValidationDetails["layout"] = layoutValidation;
                    result.Errors.AddRange(layoutValidation.Errors);
                    result.Warnings.AddRange(layoutValidation.Warnings);

                    if (layoutValidation.Errors.Any())
                        result.Success = false;
                }

                // 4. Validação de regras de negócio
                var businessRulesValidation = ValidateBusinessRules(doc);
                result.ValidationDetails["businessRules"] = businessRulesValidation;
                result.Warnings.AddRange(businessRulesValidation.Warnings);

                _logger.LogInformation("Análise XML concluída: {Elements} elementos, {Attributes} atributos, {Depth} níveis",
                    result.TotalElements, result.TotalAttributes, result.Depth);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro durante análise XML");
                result.Success = false;
                result.Errors.Add($"Erro interno: {ex.Message}");
                return result;
            }
        }


        /// <summary>
        /// Analisa a estrutura do XML
        /// </summary>
        private XmlStructureAnalysis AnalyzeXmlStructure(XDocument doc)
        {
            var analysis = new XmlStructureAnalysis
            {
                RootElement = doc.Root?.Name.LocalName ?? "Unknown",
                TotalElements = doc.Descendants().Count(),
                TotalAttributes = doc.Descendants().SelectMany(e => e.Attributes()).Count(),
                Namespaces = doc.Descendants().SelectMany(e => e.Attributes().Where(a => a.IsNamespaceDeclaration)).Select(a => a.Name.LocalName).Distinct().ToList()
            };

            // Calcular profundidade máxima
            if (doc.Root != null)
                analysis.MaxDepth = CalculateMaxDepth(doc.Root);

            // Coletar tipos de elementos
            analysis.ElementTypes = doc.Descendants().Select(e => e.Name.LocalName).Distinct().ToList();

            return analysis;
        }

        private int CalculateMaxDepth(XElement element, int currentDepth = 0)
        {
            if (!element.Elements().Any())
                return currentDepth;

            return element.Elements().Max(e => CalculateMaxDepth(e, currentDepth + 1));
        }

        /// <summary>
        /// Valida XML contra layout definido
        /// </summary>
        private LayoutValidationResult ValidateAgainstLayout(XDocument doc, Layout layout)
        {
            var result = new LayoutValidationResult
            {
                Errors = new List<string>(),
                Warnings = new List<string>()
            };

            if (layout?.Elements == null || !layout.Elements.Any())
            {
                result.Warnings.Add("Layout não possui elementos para validação");
                return result;
            }

            if (doc?.Root == null)
            {
                result.Errors.Add("XML não possui elemento raiz");
                return result;
            }

            try
            {
                var rootElement = doc.Root;
                var rootName = rootElement.Name.LocalName;

                // Validar elemento raiz (geralmente "ROOT")
                if (rootName != "ROOT")
                {
                    result.Warnings.Add($"Elemento raiz esperado: 'ROOT', encontrado: '{rootName}'");
                }

                // Processar cada LineElement do layout
                foreach (var lineElement in layout.Elements)
                {
                    ValidateLineElement(doc, rootElement, lineElement, result);
                }

                // Validar elementos no XML que não estão no layout (aviso)
                var xmlLineNames = rootElement.Elements()
                    .Select(e => e.Name.LocalName)
                    .Distinct()
                    .ToList();

                var layoutLineNames = layout.Elements
                    .Where(e => !string.IsNullOrEmpty(e.Name))
                    .Select(e => e.Name)
                    .Distinct()
                    .ToList();

                var unexpectedElements = xmlLineNames
                    .Where(xmlName => !layoutLineNames.Contains(xmlName, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                if (unexpectedElements.Any())
                {
                    result.Warnings.Add(
                        $"Elementos no XML não encontrados no layout: {string.Join(", ", unexpectedElements)}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao validar XML contra layout");
                result.Errors.Add($"Erro durante validação: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Valida um LineElement específico contra o XML
        /// </summary>
        private void ValidateLineElement(XDocument doc, XElement rootElement, LineElement lineElement, LayoutValidationResult result)
        {
            if (lineElement == null || string.IsNullOrEmpty(lineElement.Name))
            {
                return;
            }

            var lineName = lineElement.Name;
            var xmlLineElements = rootElement.Elements()
                .Where(e => e.Name.LocalName.Equals(lineName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var occurrenceCount = xmlLineElements.Count;

            // Validar ocorrências mínimas
            if (lineElement.MinimalOccurrence > 0 && occurrenceCount < lineElement.MinimalOccurrence)
            {
                result.Errors.Add(
                    $"Linha '{lineName}': Ocorrência mínima esperada: {lineElement.MinimalOccurrence}, encontrada: {occurrenceCount}");
            }

            // Validar ocorrências máximas
            if (lineElement.MaximumOccurrence > 0 && occurrenceCount > lineElement.MaximumOccurrence)
            {
                result.Warnings.Add(
                    $"Linha '{lineName}': Ocorrência máxima esperada: {lineElement.MaximumOccurrence}, encontrada: {occurrenceCount}");
            }

            // Validar se elemento obrigatório está presente
            if (lineElement.IsRequired && occurrenceCount == 0)
            {
                result.Errors.Add($"Linha obrigatória '{lineName}' não encontrada no XML");
                return;
            }

            // Se não há ocorrências e não é obrigatório, pular validação de campos
            if (occurrenceCount == 0)
            {
                return;
            }

            // Separar FieldElements e LineElements filhos
            var (fieldElements, childLineElements) = SeparateElements(lineElement);

            // Validar campos de cada ocorrência da linha
            foreach (var xmlLineElement in xmlLineElements)
            {
                ValidateLineFields(xmlLineElement, lineName, fieldElements, result);
            }

            // Validar linhas filhas recursivamente
            foreach (var childLine in childLineElements)
            {
                // Para linhas filhas, procurar dentro das ocorrências da linha pai
                foreach (var xmlLineElement in xmlLineElements)
                {
                    ValidateLineElement(doc, xmlLineElement, childLine, result);
                }
            }
        }

        /// <summary>
        /// Valida campos de uma linha específica
        /// </summary>
        private void ValidateLineFields(XElement xmlLineElement, string lineName, List<FieldElement> fieldElements, LayoutValidationResult result)
        {
            if (fieldElements == null || !fieldElements.Any())
            {
                return;
            }

            foreach (var fieldElement in fieldElements)
            {
                if (fieldElement == null || string.IsNullOrEmpty(fieldElement.Name))
                {
                    continue;
                }

                var fieldName = fieldElement.Name;
                var xmlFieldElements = xmlLineElement.Elements()
                    .Where(e => e.Name.LocalName.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var fieldOccurrenceCount = xmlFieldElements.Count;

                // Validar campo obrigatório
                if (fieldElement.IsRequired && fieldOccurrenceCount == 0)
                {
                    result.Errors.Add(
                        $"Linha '{lineName}': Campo obrigatório '{fieldName}' não encontrado");
                    continue;
                }

                // Validar se campo tem valor quando presente
                if (fieldOccurrenceCount > 0)
                {
                    var emptyFields = xmlFieldElements
                        .Where(e => string.IsNullOrWhiteSpace(e.Value))
                        .ToList();

                    if (emptyFields.Any() && fieldElement.IsRequired)
                    {
                        result.Warnings.Add(
                            $"Linha '{lineName}': Campo obrigatório '{fieldName}' encontrado mas está vazio");
                    }

                    // Validar comprimento do campo (se especificado)
                    if (fieldElement.LengthField > 0)
                    {
                        foreach (var xmlField in xmlFieldElements)
                        {
                            var fieldValue = xmlField.Value ?? string.Empty;
                            if (fieldValue.Length > fieldElement.LengthField)
                            {
                                result.Warnings.Add(
                                    $"Linha '{lineName}': Campo '{fieldName}' excede comprimento esperado. " +
                                    $"Esperado: {fieldElement.LengthField}, encontrado: {fieldValue.Length}");
                            }
                        }
                    }
                }
            }

            // Verificar campos no XML que não estão no layout (aviso)
            var xmlFieldNames = xmlLineElement.Elements()
                .Select(e => e.Name.LocalName)
                .Distinct()
                .ToList();

            var layoutFieldNames = fieldElements
                .Where(f => !string.IsNullOrEmpty(f.Name))
                .Select(f => f.Name)
                .Distinct()
                .ToList();

            var unexpectedFields = xmlFieldNames
                .Where(xmlName => !layoutFieldNames.Contains(xmlName, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (unexpectedFields.Any())
            {
                result.Warnings.Add(
                    $"Linha '{lineName}': Campos no XML não encontrados no layout: {string.Join(", ", unexpectedFields)}");
            }
        }

        /// <summary>
        /// Separa FieldElements e LineElements filhos de um LineElement
        /// </summary>
        private (List<FieldElement> fieldElements, List<LineElement> childLineElements) SeparateElements(LineElement lineElement)
        {
            var fieldElements = new List<FieldElement>();
            var childLineElements = new List<LineElement>();

            if (lineElement?.Elements == null || !lineElement.Elements.Any())
            {
                return (fieldElements, childLineElements);
            }

            foreach (var elementJson in lineElement.Elements)
            {
                if (string.IsNullOrWhiteSpace(elementJson))
                {
                    continue;
                }

                try
                {
                    // Verificar o tipo do elemento antes de desserializar
                    bool isLineElement = elementJson.Contains("\"Type\":\"LineElementVO\"", StringComparison.OrdinalIgnoreCase) ||
                                        elementJson.Contains("\"type\":\"LineElementVO\"", StringComparison.OrdinalIgnoreCase);
                    bool isFieldElement = elementJson.Contains("\"Type\":\"FieldElementVO\"", StringComparison.OrdinalIgnoreCase) ||
                                         elementJson.Contains("\"type\":\"FieldElementVO\"", StringComparison.OrdinalIgnoreCase);

                    if (isFieldElement)
                    {
                        var field = JsonConvert.DeserializeObject<FieldElement>(elementJson);
                        if (field != null && !string.IsNullOrEmpty(field.Name))
                        {
                            fieldElements.Add(field);
                        }
                    }
                    else if (isLineElement)
                    {
                        var childLine = JsonConvert.DeserializeObject<LineElement>(elementJson);
                        if (childLine != null && !string.IsNullOrEmpty(childLine.Name) &&
                            childLine.Name != lineElement.Name)
                        {
                            childLineElements.Add(childLine);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Erro ao desserializar elemento do layout: {LineName}", lineElement.Name);
                    // Continuar processamento mesmo se houver erro em um elemento
                }
            }

            return (fieldElements, childLineElements);
        }

        /// <summary>
        /// Valida regras de negócio comuns
        /// </summary>
        private BusinessRulesValidation ValidateBusinessRules(XDocument doc)
        {
            var result = new BusinessRulesValidation
            {
                Warnings = new List<string>()
            };

            // Verificar elementos vazios
            var emptyElements = doc.Descendants().Where(e => !e.Elements().Any() && string.IsNullOrWhiteSpace(e.Value)).ToList();

            if (emptyElements.Any())
                result.Warnings.Add($"{emptyElements.Count} elementos vazios encontrados");

            // Verificar atributos sem valor
            var emptyAttributes = doc.Descendants().SelectMany(e => e.Attributes()).Where(a => string.IsNullOrWhiteSpace(a.Value)).ToList();

            if (emptyAttributes.Any())
                result.Warnings.Add($"{emptyAttributes.Count} atributos vazios encontrados");

            return result;
        }
    }
}