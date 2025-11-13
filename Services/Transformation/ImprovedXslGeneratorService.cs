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
            string outputPath = null)
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

                // 3. Carregar modelo aprendido se existir
                var learnedModel = await _learningService.LoadXslModelAsync(layoutName);
                if (learnedModel == null || !learnedModel.Patterns.Any())
                {
                    result.Warnings.Add("Nenhum modelo aprendido encontrado. Usando geração base.");
                    result.SuggestedXsl = baseXsl;
                    return result;
                }

                // 4. Analisar XSL gerado e comparar com padrões aprendidos
                var generatedPatterns = await ExtractPatternsFromXslAsync(baseXsl);
                
                // 5. Comparar padrões gerados com padrões aprendidos
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

                result.Suggestions = improvements;

                // 5. Aplicar melhorias baseadas em regras aprendidas
                var improvedXsl = await ApplyLearnedRulesAsync(baseXsl, learnedModel);
                result.SuggestedXsl = improvedXsl;

                // 6. Validar XSL melhorado
                var validationResult = await ValidateXslStructureAsync(improvedXsl);
                if (!validationResult.Success)
                {
                    result.Warnings.AddRange(validationResult.Errors);
                    result.SuggestedXsl = baseXsl; // Reverter se inválido
                }

                result.Success = true;
                _logger.LogInformation("Geração de XSL com ML concluída. {SuggestionCount} sugestões geradas", improvements.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao gerar XSL com aprendizado de máquina");
                result.Success = false;
                result.Warnings.Add($"Erro: {ex.Message}");
            }

            return result;
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

