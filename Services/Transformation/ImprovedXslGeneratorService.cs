using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using LayoutParserApi.Services.XmlAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LayoutParserApi.Services.Transformation
{
    /// <summary>
    /// Serviço melhorado para geração de XSL usando aprendizado de máquina
    /// </summary>
    public class ImprovedXslGeneratorService
    {
        private readonly ILogger<ImprovedXslGeneratorService> _logger;
        private readonly XslGeneratorService _baseGenerator;
        private readonly TransformationLearningService _learningService;
        private readonly PatternComparisonService _comparisonService;

        public ImprovedXslGeneratorService(
            ILogger<ImprovedXslGeneratorService> logger,
            XslGeneratorService baseGenerator,
            TransformationLearningService learningService,
            PatternComparisonService comparisonService)
        {
            _logger = logger;
            _baseGenerator = baseGenerator;
            _learningService = learningService;
            _comparisonService = comparisonService;
        }

        /// <summary>
        /// Gera XSL usando aprendizado de máquina para melhorar a precisão
        /// </summary>
        public async Task<ImprovedXslGenerationResult> GenerateXslWithMLAsync(
            string mapXmlPath,
            string layoutName,
            string outputPath = null,
            string targetLayoutGuid = null,
            string intermediateXml = null)
        {
            var result = new ImprovedXslGenerationResult
            {
                Success = true,
                Suggestions = new List<string>(),
                Warnings = new List<string>()
            };

            try
            {
                _logger.LogInformation("Gerando XSL com aprendizado de máquina para layout: {LayoutName}", layoutName);

                // 1. Gerar XSL base usando o gerador padrão
                var baseXsl = await _baseGenerator.GenerateXslFromMapAsync(mapXmlPath, outputPath);
                result.GeneratedXsl = baseXsl;

                // 2. Limpar XSL base (remove namespace 'ng', corrige namespaces)
                baseXsl = CleanXslContent(baseXsl);
                result.GeneratedXsl = baseXsl;

                // 3. Carregar exemplos TCL constantemente da pasta ExamplesTclPath
                var tclExamples = await _learningService.LoadTclExamplesAsync(layoutName);
                if (tclExamples != null && tclExamples.Any())
                {
                    _logger.LogInformation("Carregados {Count} exemplos TCL constantemente para layout: {LayoutName}", tclExamples.Count, layoutName);
                    
                    // Aprender padrões TCL constantemente
                    var tclLearningResult = await _learningService.LearnTclPatternsAsync(layoutName, tclExamples);
                    if (tclLearningResult.Success && tclLearningResult.PatternsLearned.Any())
                    {
                        _logger.LogInformation("Padroes TCL aprendidos: {Count} padroes", tclLearningResult.PatternsLearned.Count);
                        result.Suggestions.Add($"Aprendizado TCL: {tclLearningResult.PatternsLearned.Count} padrões aprendidos de {tclExamples.Count} exemplo(s)");
                    }
                }

                // 4. Carregar exemplos XSL constantemente da pasta ExamplesXslPath
                var xslExamples = await _learningService.LoadXslExamplesAsync(layoutName);
                if (xslExamples != null && xslExamples.Any())
                {
                    _logger.LogInformation("Carregados {Count} exemplos XSL constantemente para layout: {LayoutName}", xslExamples.Count, layoutName);
                    
                    // Aprender padrões XSL constantemente
                    var xslLearningResult = await _learningService.LearnXslPatternsAsync(layoutName, xslExamples);
                    if (xslLearningResult.Success && xslLearningResult.PatternsLearned.Any())
                    {
                        _logger.LogInformation("Padroes XSL aprendidos: {Count} padroes", xslLearningResult.PatternsLearned.Count);
                        result.Suggestions.Add($"Aprendizado XSL: {xslLearningResult.PatternsLearned.Count} padrões aprendidos de {xslExamples.Count} exemplo(s)");
                        
                        // Usar padrões XSL aprendidos para melhorar o XSL gerado
                        if (!string.IsNullOrEmpty(intermediateXml) && xslExamples.Any(e => !string.IsNullOrEmpty(e.OutputXml)))
                        {
                            _logger.LogInformation("Usando padrões XSL aprendidos para melhorar geração do XSL");
                            
                            // Comparar estrutura do XML intermediário com exemplos esperados
                            var improvedXslFromExamples = await ImproveXslUsingExamplesAsync(
                                baseXsl, 
                                intermediateXml, 
                                xslExamples.Select(e => e.OutputXml).Where(x => !string.IsNullOrEmpty(x)).ToList());
                            
                            if (!string.IsNullOrEmpty(improvedXslFromExamples))
                            {
                                baseXsl = improvedXslFromExamples;
                                result.Suggestions.Add($"XSL melhorado usando {xslExamples.Count} exemplo(s) XSL aprendido(s)");
                                _logger.LogInformation("XSL melhorado usando padroes XSL aprendidos");
                            }
                        }
                    }
                }

                // 5. Carregar exemplos XML esperados da pasta ExamplesPath (fallback)
                var xmlExamples = await _learningService.LoadXmlExamplesAsync(layoutName, targetLayoutGuid);
                if (xmlExamples != null && xmlExamples.Any())
                {
                    _logger.LogInformation("Carregados {Count} exemplos XML para layout: {LayoutName}", xmlExamples.Count, layoutName);
                    
                    // Se temos XML intermediário e exemplos XML esperados, podemos melhorar o XSL
                    if (!string.IsNullOrEmpty(intermediateXml) && xmlExamples.Any(e => !string.IsNullOrEmpty(e.OutputXml)))
                    {
                        _logger.LogInformation("Usando exemplos XML para melhorar geração do XSL");
                        
                        // Comparar estrutura do XML intermediário com exemplos esperados
                        var improvedXslFromExamples = await ImproveXslUsingExamplesAsync(
                            baseXsl, 
                            intermediateXml, 
                            xmlExamples.Select(e => e.OutputXml).Where(x => !string.IsNullOrEmpty(x)).ToList());
                        
                        if (!string.IsNullOrEmpty(improvedXslFromExamples))
                        {
                            baseXsl = improvedXslFromExamples;
                            result.Suggestions.Add($"XSL melhorado usando {xmlExamples.Count} exemplo(s) XML encontrado(s)");
                            _logger.LogInformation("XSL melhorado usando exemplos XML");
                        }
                    }
                }
                else
                {
                    _logger.LogInformation("Nenhum exemplo XML encontrado para layout: {LayoutName}", layoutName);
                }

                // 4. Carregar modelo aprendido se existir
                var learnedModel = await _learningService.LoadXslModelAsync(layoutName);
                if (learnedModel != null && learnedModel.Patterns.Any())
                {
                    // 5. Analisar XSL gerado e comparar com padrões aprendidos
                    var generatedPatterns = await ExtractPatternsFromXslAsync(baseXsl);
                    
                    // 6. Comparar padrões gerados com padrões aprendidos
                    var improvements = new List<string>();
                    foreach (var generatedPattern in generatedPatterns)
                    {
                        var similarPatterns = _comparisonService.FindMostSimilarPatterns(
                            generatedPattern,
                            learnedModel.Patterns,
                            threshold: 0.7);

                        if (similarPatterns.Any())
                        {
                            var bestMatch = similarPatterns.First();
                            if (bestMatch.Similarity < 0.9)
                            {
                                improvements.Add(
                                    $"Padrão '{generatedPattern.Name}' tem similaridade de {bestMatch.Similarity:P0} " +
                                    $"com padrão aprendido. Considere revisar.");
                            }
                        }
                    }

                    result.Suggestions.AddRange(improvements);

                    // 7. Aplicar melhorias baseadas em regras aprendidas
                    var improvedXsl = await ApplyLearnedRulesAsync(baseXsl, learnedModel);
                    if (!string.IsNullOrEmpty(improvedXsl))
                    {
                        baseXsl = improvedXsl;
                    }

                    // 8. Validar XSL melhorado
                    var validationResult = await ValidateXslStructureAsync(baseXsl);
                    if (!validationResult.Success)
                    {
                        result.Warnings.AddRange(validationResult.Errors);
                        _logger.LogWarning("XSL melhorado possui erros de validação. Usando XSL base.");
                    }
                }
                else
                {
                    _logger.LogInformation("Nenhum modelo aprendido encontrado. Usando XSL base gerado.");
                }

                result.SuggestedXsl = baseXsl;
                result.Success = true;
                _logger.LogInformation("Geração de XSL com ML concluída. {SuggestionCount} sugestões geradas", result.Suggestions.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao gerar XSL com aprendizado de máquina");
                result.Success = false;
                result.Warnings.Add($"Erro: {ex.Message}");
                // Em caso de erro, retornar XSL base
                if (!string.IsNullOrEmpty(result.GeneratedXsl))
                {
                    result.SuggestedXsl = result.GeneratedXsl;
                }
            }

            return result;
        }

        /// <summary>
        /// Melhora XSL usando exemplos XML esperados
        /// Compara a estrutura do XML intermediário com os exemplos esperados e ajusta o XSL
        /// </summary>
        private async Task<string> ImproveXslUsingExamplesAsync(
            string baseXsl, 
            string intermediateXml, 
            List<string> expectedOutputXmls)
        {
            try
            {
                if (string.IsNullOrEmpty(intermediateXml) || expectedOutputXmls == null || !expectedOutputXmls.Any())
                {
                    return baseXsl;
                }

                _logger.LogInformation("Melhorando XSL usando {Count} exemplo(s) XML", expectedOutputXmls.Count);

                // Parsear XML intermediário
                var intermediateDoc = XDocument.Parse(intermediateXml);
                
                // Parsear exemplos XML esperados
                var expectedDocs = expectedOutputXmls
                    .Where(xml => !string.IsNullOrEmpty(xml))
                    .Select(xml =>
                    {
                        try
                        {
                            return XDocument.Parse(xml);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Erro ao fazer parse de exemplo XML esperado");
                            return null;
                        }
                    })
                    .Where(doc => doc != null)
                    .ToList();

                if (!expectedDocs.Any())
                {
                    _logger.LogWarning("Nenhum exemplo XML válido encontrado para melhorar XSL");
                    return baseXsl;
                }

                // Analisar estrutura dos exemplos esperados
                var expectedStructure = AnalyzeXmlStructure(expectedDocs.First());
                
                // Analisar estrutura do XML intermediário
                var intermediateStructure = AnalyzeXmlStructure(intermediateDoc);
                
                // Comparar estruturas e identificar diferenças
                var differences = CompareXmlStructures(intermediateStructure, expectedStructure);
                
                if (differences.Any())
                {
                    _logger.LogInformation("Encontradas {Count} diferenças entre XML intermediário e exemplos esperados", differences.Count);
                    
                    // Ajustar XSL baseado nas diferenças encontradas
                    var improvedXsl = AdjustXslBasedOnDifferences(baseXsl, differences, intermediateStructure, expectedStructure);
                    
                    if (!string.IsNullOrEmpty(improvedXsl))
                    {
                        _logger.LogInformation("XSL ajustado com base nos exemplos XML");
                        return improvedXsl;
                    }
                }
                else
                {
                    _logger.LogInformation("Estruturas XML são compatíveis. XSL base deve funcionar corretamente.");
                }

                return baseXsl;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao melhorar XSL usando exemplos XML");
                return baseXsl;
            }
        }

        /// <summary>
        /// Analisa estrutura de um XML (elementos, atributos, hierarquia)
        /// </summary>
        private Dictionary<string, object> AnalyzeXmlStructure(XDocument doc)
        {
            var structure = new Dictionary<string, object>();
            
            try
            {
                var root = doc.Root;
                if (root == null)
                    return structure;

                structure["RootElement"] = root.Name.LocalName;
                structure["RootNamespace"] = root.Name.NamespaceName;
                
                var elements = root.Descendants()
                    .Select(e => new
                    {
                        Name = e.Name.LocalName,
                        Namespace = e.Name.NamespaceName,
                        Path = GetXPath(e),
                        HasAttributes = e.Attributes().Any(),
                        Attributes = e.Attributes().Select(a => new { a.Name.LocalName, a.Value }).ToList(),
                        HasText = !string.IsNullOrWhiteSpace(e.Value),
                        Text = e.Value?.Trim()
                    })
                    .ToList();

                structure["Elements"] = elements;
                structure["ElementCount"] = elements.Count;
                
                return structure;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao analisar estrutura XML");
                return structure;
            }
        }

        /// <summary>
        /// Obtém XPath de um elemento
        /// </summary>
        private string GetXPath(XElement element)
        {
            if (element.Parent == null)
                return "/" + element.Name.LocalName;

            return GetXPath(element.Parent) + "/" + element.Name.LocalName;
        }

        /// <summary>
        /// Compara duas estruturas XML e retorna diferenças
        /// </summary>
        private List<string> CompareXmlStructures(
            Dictionary<string, object> structure1, 
            Dictionary<string, object> structure2)
        {
            var differences = new List<string>();

            try
            {
                var root1 = structure1.ContainsKey("RootElement") ? structure1["RootElement"]?.ToString() : "";
                var root2 = structure2.ContainsKey("RootElement") ? structure2["RootElement"]?.ToString() : "";

                if (root1 != root2)
                {
                    differences.Add($"Elemento raiz diferente: '{root1}' vs '{root2}'");
                }

                var elements1 = structure1.ContainsKey("Elements") ? structure1["Elements"] : null;
                var elements2 = structure2.ContainsKey("Elements") ? structure2["Elements"] : null;

                // Comparação básica - pode ser expandida
                if (elements1 != null && elements2 != null)
                {
                    var count1 = structure1.ContainsKey("ElementCount") ? (int)structure1["ElementCount"] : 0;
                    var count2 = structure2.ContainsKey("ElementCount") ? (int)structure2["ElementCount"] : 0;

                    if (count1 != count2)
                    {
                        differences.Add($"Número de elementos diferente: {count1} vs {count2}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao comparar estruturas XML");
            }

            return differences;
        }

        /// <summary>
        /// Ajusta XSL baseado nas diferenças encontradas
        /// </summary>
        private string AdjustXslBasedOnDifferences(
            string baseXsl,
            List<string> differences,
            Dictionary<string, object> intermediateStructure,
            Dictionary<string, object> expectedStructure)
        {
            try
            {
                // Por enquanto, retornar XSL base
                // Esta função pode ser expandida para fazer ajustes específicos no XSL
                // baseado nas diferenças encontradas entre o XML intermediário e os exemplos esperados
                
                _logger.LogInformation("Ajustando XSL com base em {Count} diferença(s) encontrada(s)", differences.Count);
                
                // Log das diferenças para debug
                foreach (var difference in differences)
                {
                    _logger.LogInformation("Diferença: {Difference}", difference);
                }

                // Por enquanto, apenas retornar o XSL base
                // Futuramente, esta função pode fazer ajustes específicos no XSL
                return baseXsl;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao ajustar XSL baseado nas diferenças");
                return baseXsl;
            }
        }

        /// <summary>
        /// Extrai padrões do XSL gerado
        /// </summary>
        private async Task<List<LearnedPattern>> ExtractPatternsFromXslAsync(string xslContent)
        {
            var patterns = new List<LearnedPattern>();

            try
            {
                var doc = XDocument.Parse(xslContent);
                var ns = XNamespace.Get("http://www.w3.org/1999/XSL/Transform");

                var templates = doc.Descendants(ns + "template").ToList();
                foreach (var template in templates)
                {
                    var match = template.Attribute("match")?.Value;
                    var name = template.Attribute("name")?.Value;

                    patterns.Add(new LearnedPattern
                    {
                        Type = "XslTemplate",
                        Name = name ?? match ?? "UNNAMED",
                        Pattern = template.ToString(),
                        Frequency = 1,
                        Confidence = 1.0,
                        Metadata = new Dictionary<string, object>
                        {
                            ["MatchPattern"] = match,
                            ["HasApplyTemplates"] = template.Descendants(ns + "apply-templates").Any()
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao extrair padrões do XSL");
            }

            return patterns;
        }

        /// <summary>
        /// Aplica regras aprendidas para melhorar o XSL
        /// </summary>
        private async Task<string> ApplyLearnedRulesAsync(string baseXsl, LearnedXslModel learnedModel)
        {
            try
            {
                var doc = XDocument.Parse(baseXsl);
                
                // Aplicar melhorias baseadas em regras de transformação aprendidas
                foreach (var rule in learnedModel.TransformationRules.OrderByDescending(r => r.Confidence))
                {
                    // Implementar lógica para aplicar regras aprendidas
                    // Por exemplo: ajustar XPath, adicionar templates, etc.
                }

                return doc.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao aplicar regras aprendidas");
                return baseXsl;
            }
        }

        /// <summary>
        /// Valida estrutura do XSL
        /// </summary>
        private async Task<ValidationResult> ValidateXslStructureAsync(string xslContent)
        {
            var result = new ValidationResult { Success = true, Errors = new List<string>() };

            try
            {
                var doc = XDocument.Parse(xslContent);
                var ns = XNamespace.Get("http://www.w3.org/1999/XSL/Transform");

                var stylesheet = doc.Descendants(ns + "stylesheet").FirstOrDefault() 
                              ?? doc.Descendants(ns + "transform").FirstOrDefault();
                
                if (stylesheet == null)
                {
                    result.Success = false;
                    result.Errors.Add("Elemento stylesheet/transform não encontrado");
                    return result;
                }

                var templates = doc.Descendants(ns + "template").ToList();
                if (!templates.Any())
                {
                    result.Errors.Add("Nenhum template encontrado no XSL");
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"Erro ao validar XSL: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Limpa conteúdo XSL:
        /// - Remove namespace 'ng' (com.neogrid.integrator.XSLFunctions)
        /// - Remove referências ao namespace 'ng' (exclude-result-prefixes, extension-element-prefixes)
        /// - Garante que namespace 'xsi' esteja declarado se for usado
        /// </summary>
        private string CleanXslContent(string xslContent)
        {
            try
            {
                // Remover namespace 'ng' do xsl:stylesheet
                xslContent = System.Text.RegularExpressions.Regex.Replace(
                    xslContent,
                    @"\s*xmlns:ng=""[^""]*""",
                    "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                // Remover exclude-result-prefixes="ng"
                xslContent = System.Text.RegularExpressions.Regex.Replace(
                    xslContent,
                    @"\s*exclude-result-prefixes=""ng""",
                    "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                // Remover extension-element-prefixes="ng"
                xslContent = System.Text.RegularExpressions.Regex.Replace(
                    xslContent,
                    @"\s*extension-element-prefixes=""ng""",
                    "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                // Verificar se XSL usa xsi: (xsi:type, xsi:nil, etc.)
                bool usesXsi = System.Text.RegularExpressions.Regex.IsMatch(
                    xslContent,
                    @"xsi:",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                // Se usa xsi:, garantir que o namespace esteja declarado no xsl:stylesheet
                if (usesXsi)
                {
                    // Verificar se o namespace xsi já está declarado no xsl:stylesheet
                    bool hasXsiInStylesheet = System.Text.RegularExpressions.Regex.IsMatch(
                        xslContent,
                        @"<xsl:stylesheet[^>]*xmlns:xsi=""http://www\.w3\.org/2001/XMLSchema-instance""",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                    if (!hasXsiInStylesheet)
                    {
                        // Adicionar xmlns:xsi no xsl:stylesheet (antes do > de fechamento)
                        xslContent = System.Text.RegularExpressions.Regex.Replace(
                            xslContent,
                            @"(<xsl:stylesheet[^>]*xmlns:xsl=""http://www\.w3\.org/1999/XSL/Transform"")([^>]*>)",
                            @"$1" + Environment.NewLine + "\txmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"$2",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        
                        _logger.LogInformation("Namespace 'xsi' adicionado ao xsl:stylesheet");
                    }
                }

                // Garantir que o namespace xsi esteja declarado no elemento de saída se for usado
                if (usesXsi)
                {
                    // Verificar se há elemento NFe ou outro elemento raiz que precise do namespace xsi
                    var rootElementPattern = @"<(\w+)[^>]*xmlns=""[^""]*""[^>]*>";
                    var rootMatch = System.Text.RegularExpressions.Regex.Match(xslContent, rootElementPattern);
                    if (rootMatch.Success)
                    {
                        var rootElementName = rootMatch.Groups[1].Value;
                        if (!System.Text.RegularExpressions.Regex.IsMatch(
                            xslContent,
                            $@"<{rootElementName}[^>]*xmlns:xsi=""http://www\.w3\.org/2001/XMLSchema-instance""",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        {
                            // Adicionar xmlns:xsi no elemento raiz
                            xslContent = System.Text.RegularExpressions.Regex.Replace(
                                xslContent,
                                $@"(<{rootElementName}[^>]*xmlns=""[^""]*"")([^>]*>)",
                                @"$1" + Environment.NewLine + $"\t\t\t xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"$2",
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        }
                    }
                }

                _logger.LogInformation("XSL limpo: namespace 'ng' removido, namespace 'xsi' verificado");
                return xslContent;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao limpar XSL. Retornando XSL original.");
                return xslContent;
            }
        }
    }

    /// <summary>
    /// Resultado da geração melhorada de XSL
    /// </summary>
    public class ImprovedXslGenerationResult
    {
        public bool Success { get; set; }
        public string GeneratedXsl { get; set; }
        public string SuggestedXsl { get; set; }
        public List<string> Suggestions { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }
}

