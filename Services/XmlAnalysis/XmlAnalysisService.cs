using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Parsing;

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
                    {
                        result.Errors.Add($"Linha {xmlEx.LineNumber}, Posição {xmlEx.LinePosition}");
                    }
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
                Namespaces = doc.Descendants()
                    .SelectMany(e => e.Attributes().Where(a => a.IsNamespaceDeclaration))
                    .Select(a => a.Name.LocalName)
                    .Distinct()
                    .ToList()
            };

            // Calcular profundidade máxima
            if (doc.Root != null)
            {
                analysis.MaxDepth = CalculateMaxDepth(doc.Root);
            }

            // Coletar tipos de elementos
            analysis.ElementTypes = doc.Descendants()
                .Select(e => e.Name.LocalName)
                .Distinct()
                .ToList();

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

            // Extrair elementos do layout que são relevantes para XML
            if (layout?.Elements != null)
            {
                var layoutElements = layout.Elements
                    .Where(e => e.GetType().Name.Contains("Element"))
                    .ToList();

                // Validar presença de elementos esperados
                foreach (var layoutElement in layoutElements)
                {
                    // Implementar validação específica baseada no layout
                    // Por enquanto, apenas verificar estrutura básica
                }
            }

            return result;
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
            var emptyElements = doc.Descendants()
                .Where(e => !e.Elements().Any() && string.IsNullOrWhiteSpace(e.Value))
                .ToList();

            if (emptyElements.Any())
            {
                result.Warnings.Add($"{emptyElements.Count} elementos vazios encontrados");
            }

            // Verificar atributos sem valor
            var emptyAttributes = doc.Descendants()
                .SelectMany(e => e.Attributes())
                .Where(a => string.IsNullOrWhiteSpace(a.Value))
                .ToList();

            if (emptyAttributes.Any())
            {
                result.Warnings.Add($"{emptyAttributes.Count} atributos vazios encontrados");
            }

            return result;
        }
    }

    public class XmlAnalysisResult
    {
        public bool Success { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public int TotalElements { get; set; }
        public int TotalAttributes { get; set; }
        public int Depth { get; set; }
        public Dictionary<string, object> ValidationDetails { get; set; } = new();
    }

    public class XmlValidationResult
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }

    public class XmlStructureAnalysis
    {
        public string RootElement { get; set; }
        public int TotalElements { get; set; }
        public int TotalAttributes { get; set; }
        public int MaxDepth { get; set; }
        public List<string> Namespaces { get; set; } = new();
        public List<string> ElementTypes { get; set; } = new();
    }

    public class LayoutValidationResult
    {
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }

    public class BusinessRulesValidation
    {
        public List<string> Warnings { get; set; } = new();
    }
}

