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
    /// Serviço melhorado para geração de TCL usando aprendizado de máquina
    /// </summary>
    public class ImprovedTclGeneratorService
    {
        private readonly ILogger<ImprovedTclGeneratorService> _logger;
        private readonly TclGeneratorService _baseGenerator;
        private readonly TransformationLearningService _learningService;
        private readonly PatternComparisonService _comparisonService;

        public ImprovedTclGeneratorService(
            ILogger<ImprovedTclGeneratorService> logger,
            TclGeneratorService baseGenerator,
            TransformationLearningService learningService,
            PatternComparisonService comparisonService)
        {
            _logger = logger;
            _baseGenerator = baseGenerator;
            _learningService = learningService;
            _comparisonService = comparisonService;
        }

        /// <summary>
        /// Gera TCL usando aprendizado de máquina para melhorar a precisão
        /// </summary>
        public async Task<ImprovedTclGenerationResult> GenerateTclWithMLAsync(
            string layoutXmlPath,
            string layoutName,
            string outputPath = null)
        {
            var result = new ImprovedTclGenerationResult
            {
                Success = true,
                Suggestions = new List<string>(),
                Warnings = new List<string>()
            };

            try
            {
                _logger.LogInformation("Gerando TCL com aprendizado de máquina para layout: {LayoutName}", layoutName);

                // 1. Gerar TCL base usando o gerador padrão
                var baseTcl = await _baseGenerator.GenerateTclFromLayoutAsync(layoutXmlPath, outputPath);
                result.GeneratedTcl = baseTcl;

                // 2. Carregar modelo aprendido se existir
                var learnedModel = await _learningService.LoadTclModelAsync(layoutName);
                if (learnedModel == null || !learnedModel.Patterns.Any())
                {
                    result.Warnings.Add("Nenhum modelo aprendido encontrado. Usando geração base.");
                    result.SuggestedTcl = baseTcl;
                    return result;
                }

                // 3. Analisar TCL gerado e comparar com padrões aprendidos
                var generatedPatterns = await ExtractPatternsFromTclAsync(baseTcl);
                
                // 4. Comparar padrões gerados com padrões aprendidos
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
                    else
                    {
                        improvements.Add(
                            $"Padrão '{generatedPattern.Name}' não encontrado nos padrões aprendidos. " +
                            "Pode ser um padrão novo ou incorreto.");
                    }
                }

                result.Suggestions = improvements;

                // 5. Aplicar melhorias baseadas em regras aprendidas
                var improvedTcl = await ApplyLearnedRulesAsync(baseTcl, learnedModel);
                result.SuggestedTcl = improvedTcl;

                // 6. Validar TCL melhorado
                var validationResult = await ValidateTclStructureAsync(improvedTcl);
                if (!validationResult.IsValid)
                {
                    result.Warnings.AddRange(validationResult.Errors);
                    result.SuggestedTcl = baseTcl; // Reverter se inválido
                }

                result.Success = true;
                _logger.LogInformation("Geração de TCL com ML concluída. {SuggestionCount} sugestões geradas", improvements.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao gerar TCL com aprendizado de máquina");
                result.Success = false;
                result.Warnings.Add($"Erro: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Extrai padrões do TCL gerado
        /// </summary>
        private async Task<List<LearnedPattern>> ExtractPatternsFromTclAsync(string tclContent)
        {
            var patterns = new List<LearnedPattern>();

            try
            {
                var doc = XDocument.Parse(tclContent);
                var mapElement = doc.Descendants("MAP").FirstOrDefault();
                if (mapElement == null)
                    return patterns;

                var lineElements = mapElement.Elements("LINE").ToList();
                foreach (var lineElement in lineElements)
                {
                    var lineType = lineElement.Attribute("identifier")?.Value ?? 
                                  lineElement.Attribute("name")?.Value ?? "UNKNOWN";
                    
                    var fields = lineElement.Elements("FIELD").ToList();
                    var structure = string.Join("|", fields.Select(f => 
                        $"{f.Attribute("name")?.Value}:{f.Attribute("length")?.Value}"));

                    patterns.Add(new LearnedPattern
                    {
                        Type = "LineStructure",
                        Name = lineType,
                        Pattern = structure,
                        Frequency = 1,
                        Confidence = 1.0,
                        Metadata = new Dictionary<string, object>
                        {
                            ["FieldCount"] = fields.Count
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao extrair padrões do TCL");
            }

            return patterns;
        }

        /// <summary>
        /// Aplica regras aprendidas para melhorar o TCL
        /// </summary>
        private async Task<string> ApplyLearnedRulesAsync(string baseTcl, LearnedTclModel learnedModel)
        {
            try
            {
                var doc = XDocument.Parse(baseTcl);
                var mapElement = doc.Descendants("MAP").FirstOrDefault();
                if (mapElement == null)
                    return baseTcl;

                // Aplicar melhorias baseadas em regras de mapeamento aprendidas
                foreach (var rule in learnedModel.MappingRules.OrderByDescending(r => r.Confidence))
                {
                    // Implementar lógica para aplicar regras aprendidas
                    // Por exemplo: ajustar tipos de campo, posições, etc.
                }

                return doc.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao aplicar regras aprendidas");
                return baseTcl;
            }
        }

        /// <summary>
        /// Valida estrutura do TCL
        /// </summary>
        private async Task<ValidationResult> ValidateTclStructureAsync(string tclContent)
        {
            var result = new ValidationResult { IsValid = true, Errors = new List<string>() };

            try
            {
                var doc = XDocument.Parse(tclContent);
                var mapElement = doc.Descendants("MAP").FirstOrDefault();
                
                if (mapElement == null)
                {
                    result.IsValid = false;
                    result.Errors.Add("Elemento MAP não encontrado");
                    return result;
                }

                var lines = mapElement.Elements("LINE").ToList();
                if (!lines.Any())
                {
                    result.IsValid = false;
                    result.Errors.Add("Nenhuma linha encontrada no TCL");
                    return result;
                }

                // Validar que cada linha tem campos
                foreach (var line in lines)
                {
                    var fields = line.Elements("FIELD").ToList();
                    if (!fields.Any())
                    {
                        result.Errors.Add($"Linha '{line.Attribute("name")?.Value}' não tem campos");
                    }
                }
            }
            catch (Exception ex)
            {
                result.IsValid = false;
                result.Errors.Add($"Erro ao validar TCL: {ex.Message}");
            }

            return result;
        }
    }

    /// <summary>
    /// Resultado da geração melhorada de TCL
    /// </summary>
    public class ImprovedTclGenerationResult
    {
        public bool Success { get; set; }
        public string GeneratedTcl { get; set; }
        public string SuggestedTcl { get; set; }
        public List<string> Suggestions { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }

    /// <summary>
    /// Resultado de validação
    /// </summary>
    public class ValidationResult
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new();
    }
}

