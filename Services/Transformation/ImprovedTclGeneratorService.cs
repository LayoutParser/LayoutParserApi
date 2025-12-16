using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

using LayoutParserApi.Models;
using LayoutParserApi.Services.Transformation.Models;
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

        public ImprovedTclGeneratorService(ILogger<ImprovedTclGeneratorService> logger,TclGeneratorService baseGenerator,TransformationLearningService learningService,PatternComparisonService comparisonService)
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
                    var similarPatterns = _comparisonService.FindMostSimilarPatterns(generatedPattern,learnedModel.Patterns,threshold: 0.7);

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
                if (!validationResult.Success)
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
                    var lineType = lineElement.Attribute("identifier")?.Value ?? lineElement.Attribute("name")?.Value ?? "UNKNOWN";
                    
                    var fields = lineElement.Elements("FIELD").ToList();
                    var structure = string.Join("|", fields.Select(f => $"{f.Attribute("name")?.Value}:{f.Attribute("length")?.Value}"));

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
                var appliedRules = new List<string>();
                var skippedRules = new List<string>();
                const double minConfidenceThreshold = 0.6; // Aplicar apenas regras com confiança >= 60%

                foreach (var rule in learnedModel.MappingRules.OrderByDescending(r => r.Confidence))
                {
                    // Ignorar regras com baixa confiança
                    if (rule.Confidence < minConfidenceThreshold)
                    {
                        skippedRules.Add($"Regra '{rule.SourceField} -> {rule.TargetElement}' ignorada (confiança: {rule.Confidence:P0} < {minConfidenceThreshold:P0})");
                        continue;
                    }

                    // Parsear SourceField (formato: "LINHA000.CampoNome")
                    var sourceFieldParts = rule.SourceField?.Split('.');
                    if (sourceFieldParts == null || sourceFieldParts.Length != 2)
                    {
                        skippedRules.Add($"Regra com SourceField inválido: '{rule.SourceField}'");
                        continue;
                    }

                    var sourceLineName = sourceFieldParts[0];
                    var sourceFieldName = sourceFieldParts[1];

                    // Encontrar a linha correspondente no TCL
                    var lineElement = mapElement.Elements("LINE")
                        .FirstOrDefault(l => 
                            (l.Attribute("identifier")?.Value ?? l.Attribute("name")?.Value ?? "")
                            .Equals(sourceLineName, StringComparison.OrdinalIgnoreCase));

                    if (lineElement == null)
                    {
                        skippedRules.Add($"Linha '{sourceLineName}' não encontrada no TCL para regra '{rule.SourceField}'");
                        continue;
                    }

                    // Encontrar o campo correspondente na linha
                    var fieldElement = lineElement.Elements("FIELD")
                        .FirstOrDefault(f => 
                            (f.Attribute("name")?.Value ?? "")
                            .Equals(sourceFieldName, StringComparison.OrdinalIgnoreCase));

                    if (fieldElement == null)
                    {
                        skippedRules.Add($"Campo '{sourceFieldName}' não encontrado na linha '{sourceLineName}' para regra '{rule.SourceField}'");
                        continue;
                    }

                    // Aplicar ajustes baseados na regra aprendida
                    bool ruleApplied = false;

                    // 1. Ajustar posição inicial (startPosition)
                    if (rule.SourcePosition > 0)
                    {
                        var currentStartPos = fieldElement.Attribute("startPosition")?.Value;
                        var currentStartPosInt = int.TryParse(currentStartPos, out var pos) ? pos : 0;

                        // Aplicar apenas se houver diferença significativa (>= 2 posições)
                        if (Math.Abs(currentStartPosInt - rule.SourcePosition) >= 2)
                        {
                            fieldElement.SetAttributeValue("startPosition", rule.SourcePosition);
                            ruleApplied = true;
                            _logger.LogDebug(
                                "Ajustada posição do campo '{SourceField}': {OldPos} -> {NewPos} (confiança: {Confidence:P0})",
                                rule.SourceField, currentStartPosInt, rule.SourcePosition, rule.Confidence);
                        }
                    }

                    // 2. Ajustar comprimento (length)
                    if (rule.SourceLength > 0)
                    {
                        var currentLength = fieldElement.Attribute("length")?.Value;
                        var currentLengthInt = ParseLengthFromAttribute(currentLength);

                        // Aplicar apenas se houver diferença significativa (>= 1 posição)
                        if (Math.Abs(currentLengthInt - rule.SourceLength) >= 1)
                        {
                            // Preservar formato decimal se existir (ex: "15,2,0")
                            if (currentLength?.Contains(',') == true)
                            {
                                var parts = currentLength.Split(',');
                                if (parts.Length >= 3)
                                {
                                    fieldElement.SetAttributeValue("length", $"{rule.SourceLength},{parts[1]},{parts[2]}");
                                }
                                else
                                {
                                    fieldElement.SetAttributeValue("length", rule.SourceLength);
                                }
                            }
                            else
                            {
                                fieldElement.SetAttributeValue("length", rule.SourceLength);
                            }
                            ruleApplied = true;
                            _logger.LogDebug(
                                "Ajustado comprimento do campo '{SourceField}': {OldLength} -> {NewLength} (confiança: {Confidence:P0})",
                                rule.SourceField, currentLengthInt, rule.SourceLength, rule.Confidence);
                        }
                    }

                    // 3. Ajustar tipo de campo (fieldType)
                    if (!string.IsNullOrEmpty(rule.SourceType))
                    {
                        var currentFieldType = fieldElement.Attribute("fieldType")?.Value;
                        if (!string.Equals(currentFieldType, rule.SourceType, StringComparison.OrdinalIgnoreCase))
                        {
                            fieldElement.SetAttributeValue("fieldType", rule.SourceType);
                            ruleApplied = true;
                            _logger.LogDebug(
                                "Ajustado tipo do campo '{SourceField}': {OldType} -> {NewType} (confiança: {Confidence:P0})",
                                rule.SourceField, currentFieldType ?? "null", rule.SourceType, rule.Confidence);
                        }
                    }

                    // 4. Ajustar mapeamento XML (mapping)
                    if (!string.IsNullOrEmpty(rule.TargetElement) || !string.IsNullOrEmpty(rule.TargetXPath))
                    {
                        var currentMapping = fieldElement.Attribute("mapping")?.Value;
                        var newMapping = !string.IsNullOrEmpty(rule.TargetXPath) 
                            ? rule.TargetXPath 
                            : rule.TargetElement;

                        if (!string.Equals(currentMapping, newMapping, StringComparison.OrdinalIgnoreCase))
                        {
                            fieldElement.SetAttributeValue("mapping", newMapping);
                            ruleApplied = true;
                            _logger.LogDebug(
                                "Ajustado mapeamento do campo '{SourceField}': {OldMapping} -> {NewMapping} (confiança: {Confidence:P0})",
                                rule.SourceField, currentMapping ?? "null", newMapping, rule.Confidence);
                        }
                    }

                    // 5. Aplicar transformação (transformType)
                    if (!string.IsNullOrEmpty(rule.TransformType) && 
                        !string.Equals(rule.TransformType, "Direct", StringComparison.OrdinalIgnoreCase))
                    {
                        var currentTransform = fieldElement.Attribute("transform")?.Value;
                        if (!string.Equals(currentTransform, rule.TransformType, StringComparison.OrdinalIgnoreCase))
                        {
                            fieldElement.SetAttributeValue("transform", rule.TransformType);
                            ruleApplied = true;
                            _logger.LogDebug(
                                "Ajustada transformação do campo '{SourceField}': {OldTransform} -> {NewTransform} (confiança: {Confidence:P0})",
                                rule.SourceField, currentTransform ?? "Direct", rule.TransformType, rule.Confidence);
                        }
                    }

                    if (ruleApplied)
                    {
                        appliedRules.Add(
                            $"Regra aplicada: '{rule.SourceField}' -> '{rule.TargetElement}' " +
                            $"(confiança: {rule.Confidence:P0}, tipo: {rule.TransformType})");
                    }
                    else
                    {
                        skippedRules.Add(
                            $"Regra '{rule.SourceField} -> {rule.TargetElement}' não aplicada " +
                            $"(sem diferenças significativas ou valores já corretos)");
                    }
                }

                // Log resumo das regras aplicadas
                if (appliedRules.Any())
                {
                    _logger.LogInformation(
                        "Aplicadas {AppliedCount} regras aprendidas de {TotalCount} regras disponíveis",
                        appliedRules.Count, learnedModel.MappingRules.Count);
                }

                if (skippedRules.Any() && _logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Regras ignoradas/não aplicadas: {SkippedCount}", skippedRules.Count);
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
            var result = new ValidationResult { Success = true, Errors = new List<string>() };

            try
            {
                var doc = XDocument.Parse(tclContent);
                var mapElement = doc.Descendants("MAP").FirstOrDefault();
                
                if (mapElement == null)
                {
                    result.Success = false;
                    result.Errors.Add("Elemento MAP não encontrado");
                    return result;
                }

                var lines = mapElement.Elements("LINE").ToList();
                if (!lines.Any())
                {
                    result.Success = false;
                    result.Errors.Add("Nenhuma linha encontrada no TCL");
                    return result;
                }

                // Validar que cada linha tem campos
                foreach (var line in lines)
                {
                    var fields = line.Elements("FIELD").ToList();
                    if (!fields.Any())
                        result.Errors.Add($"Linha '{line.Attribute("name")?.Value}' não tem campos");
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"Erro ao validar TCL: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Parseia o comprimento de um atributo length (pode ser "60" ou "15,2,0" para decimais)
        /// </summary>
        private int ParseLengthFromAttribute(string lengthAttr)
        {
            if (string.IsNullOrEmpty(lengthAttr))
                return 0;

            // Se contém vírgula, é formato decimal (ex: "15,2,0")
            if (lengthAttr.Contains(','))
            {
                var parts = lengthAttr.Split(',');
                if (parts.Length > 0 && int.TryParse(parts[0], out var length))
                {
                    return length;
                }
            }

            // Formato simples (ex: "60")
            return int.TryParse(lengthAttr, out var simpleLength) ? simpleLength : 0;
        }
    }
}