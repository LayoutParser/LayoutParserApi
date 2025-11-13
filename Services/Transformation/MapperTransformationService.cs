using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Parsing;
using LayoutParserApi.Models.Database;
using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Parsing.Interfaces;
using System.Text;
using System.Xml.Linq;

namespace LayoutParserApi.Services.Transformation
{
    public interface IMapperTransformationService
    {
        Task<TransformationResult> TransformAsync(
            string inputText,
            string inputLayoutGuid,
            string targetLayoutGuid,
            string mapperXml = null);
    }

    public class MapperTransformationService : IMapperTransformationService
    {
        private readonly ICachedMapperService _cachedMapperService;
        private readonly ICachedLayoutService _cachedLayoutService;
        private readonly ILayoutParserService _layoutParserService;
        private readonly ILogger<MapperTransformationService> _logger;

        public MapperTransformationService(
            ICachedMapperService cachedMapperService,
            ICachedLayoutService cachedLayoutService,
            ILayoutParserService layoutParserService,
            ILogger<MapperTransformationService> logger)
        {
            _cachedMapperService = cachedMapperService;
            _cachedLayoutService = cachedLayoutService;
            _layoutParserService = layoutParserService;
            _logger = logger;
        }

        public async Task<TransformationResult> TransformAsync(
            string inputText,
            string inputLayoutGuid,
            string targetLayoutGuid,
            string mapperXml = null)
        {
            var result = new TransformationResult
            {
                Success = false,
                Errors = new List<string>(),
                Warnings = new List<string>()
            };

            try
            {
                _logger.LogInformation("Iniciando transformação: InputLayoutGuid={InputGuid}, TargetLayoutGuid={TargetGuid}",
                    inputLayoutGuid, targetLayoutGuid);

                // 1. Buscar mapeador
                Mapper mapper = null;
                if (!string.IsNullOrEmpty(mapperXml))
                {
                    // Usar mapeador fornecido diretamente
                    mapper = ParseMapperFromXml(mapperXml);
                }
                else
                {
                    // Buscar mapeador do cache/banco
                    var mappers = await _cachedMapperService.GetMappersByInputLayoutGuidAsync(inputLayoutGuid);
                    
                    // Normalizar targetLayoutGuid para comparação
                    var normalizedTargetGuid = NormalizeLayoutGuid(targetLayoutGuid);
                    
                    mapper = mappers?.FirstOrDefault(m =>
                    {
                        // Priorizar TargetLayoutGuidFromXml (do XML descriptografado)
                        var mapperTargetGuid = m.TargetLayoutGuidFromXml ?? m.TargetLayoutGuid ?? "";
                        var normalizedMapperTargetGuid = NormalizeLayoutGuid(mapperTargetGuid);
                        
                        // Comparar GUIDs normalizados
                        return string.Equals(normalizedMapperTargetGuid, normalizedTargetGuid, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(mapperTargetGuid, targetLayoutGuid, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(m.TargetLayoutGuid, targetLayoutGuid, StringComparison.OrdinalIgnoreCase);
                    });
                    
                    if (mapper == null)
                    {
                        _logger.LogWarning("Mapeador não encontrado para InputLayoutGuid={InputGuid} e TargetLayoutGuid={TargetGuid}", 
                            inputLayoutGuid, targetLayoutGuid);
                        _logger.LogWarning("Mapeadores encontrados para InputLayoutGuid: {Count}", mappers?.Count ?? 0);
                        
                        if (mappers != null && mappers.Any())
                        {
                            foreach (var m in mappers.Take(3))
                            {
                                _logger.LogWarning("  - Mapeador: {Name}, TargetGuid (XML): {TargetXml}, TargetGuid (DB): {TargetDb}", 
                                    m.Name, m.TargetLayoutGuidFromXml ?? "null", m.TargetLayoutGuid ?? "null");
                            }
                        }
                        
                        result.Errors.Add($"Mapeador não encontrado para InputLayoutGuid={inputLayoutGuid} e TargetLayoutGuid={targetLayoutGuid}");
                        return result;
                    }
                    
                    _logger.LogInformation("Mapeador encontrado: {Name} (ID: {Id}) - TargetGuid (XML): {TargetXml}", 
                        mapper.Name, mapper.Id, mapper.TargetLayoutGuidFromXml ?? mapper.TargetLayoutGuid ?? "null");
                }

                // 2. Buscar layout de entrada do Redis
                var layoutsResponse = await _cachedLayoutService.SearchLayoutsAsync(new Models.Database.LayoutSearchRequest
                {
                    SearchTerm = "all"
                });

                if (!layoutsResponse.Success || layoutsResponse.Layouts == null)
                {
                    result.Errors.Add("Não foi possível buscar layouts do cache");
                    return result;
                }

                // Normalizar inputLayoutGuid para comparação (pode vir com ou sem prefixo LAY_, com ou sem chaves {})
                var normalizedInputGuid = NormalizeLayoutGuid(inputLayoutGuid);
                _logger.LogInformation("Buscando layout de entrada: InputGuid={InputGuid}, Normalized={Normalized}", inputLayoutGuid, normalizedInputGuid);

                // Buscar layout que corresponde ao GUID de entrada
                var inputLayout = layoutsResponse.Layouts.FirstOrDefault(l =>
                {
                    var layoutGuidStr = l.LayoutGuid != null ? l.LayoutGuid.ToString() : "";
                    var normalizedLayoutGuid = NormalizeLayoutGuid(layoutGuidStr);
                    
                    // Comparar GUIDs normalizados (removendo prefixos LAY_ e chaves {})
                    return string.Equals(normalizedLayoutGuid, normalizedInputGuid, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(layoutGuidStr, inputLayoutGuid, StringComparison.OrdinalIgnoreCase) ||
                           (l.LayoutGuid != null && l.LayoutGuid.ToString().Equals(inputLayoutGuid, StringComparison.OrdinalIgnoreCase));
                });

                if (inputLayout == null)
                {
                    _logger.LogError("Layout de entrada não encontrado: InputGuid={InputGuid}, Normalized={Normalized}", 
                        inputLayoutGuid, normalizedInputGuid);
                    _logger.LogError("Total de layouts no cache: {Count}", layoutsResponse.Layouts.Count());
                    
                    // Log dos primeiros layouts para debug
                    foreach (var l in layoutsResponse.Layouts.Take(5))
                    {
                        var layoutGuidStr = l.LayoutGuid != null ? l.LayoutGuid.ToString() : "null";
                        var normalizedLayoutGuidStr = NormalizeLayoutGuid(layoutGuidStr);
                        _logger.LogError("  - Layout: {Name} (ID: {Id}), GUID: {Guid} (normalizado: {Normalized})", 
                            l.Name, l.Id, layoutGuidStr, normalizedLayoutGuidStr);
                    }
                    
                    result.Errors.Add($"Layout de entrada não encontrado: {inputLayoutGuid}");
                    return result;
                }
                
                _logger.LogInformation("Layout de entrada encontrado: {Name} (ID: {Id}) - GUID: {Guid}", 
                    inputLayout.Name, inputLayout.Id, inputLayout.LayoutGuid);

                // 3. Fazer parse do texto usando o layout de entrada (igual ao ParseAsync)
                if (string.IsNullOrEmpty(inputLayout.DecryptedContent))
                {
                    _logger.LogError("DecryptedContent vazio para layout de entrada: {Name} (ID: {Id})", 
                        inputLayout.Name, inputLayout.Id);
                    result.Errors.Add("Layout de entrada não possui conteúdo descriptografado");
                    return result;
                }
                
                _logger.LogInformation("Fazendo parse do texto usando layout de entrada: {Name} (ID: {Id})", 
                    inputLayout.Name, inputLayout.Id);
                _logger.LogInformation("Tamanho do texto de entrada: {Size} chars", inputText?.Length ?? 0);
                _logger.LogInformation("Tamanho do XML do layout: {Size} chars", inputLayout.DecryptedContent?.Length ?? 0);
                
                var parsedResult = await ParseTextWithLayoutAsync(inputText, inputLayout.DecryptedContent);
                if (!parsedResult.Success)
                {
                    _logger.LogError("Erro ao fazer parse do texto: {ErrorMessage}", parsedResult.ErrorMessage);
                    result.Errors.Add(parsedResult.ErrorMessage ?? "Erro ao fazer parse do texto");
                    return result;
                }
                
                _logger.LogInformation("Parse concluído com sucesso: {FieldCount} campos encontrados", 
                    parsedResult.ParsedFields?.Count ?? 0);

                // 4. Converter ParsedFields para XML estruturado (formato intermediário)
                var parsedXml = ConvertParsedFieldsToXml(parsedResult);
                if (string.IsNullOrEmpty(parsedXml))
                {
                    result.Errors.Add("Erro ao converter campos parseados para XML");
                    return result;
                }

                // 5. Converter mapeador XML para TCL e XSL (usando aprendizado de máquina ou conversão direta)
                var transformationScripts = await ConvertMapperToTransformationScriptsAsync(mapper);
                if (transformationScripts == null)
                {
                    result.Errors.Add("Erro ao converter mapeador para scripts de transformação");
                    return result;
                }

                // 6. Aplicar transformação TCL para gerar XML intermediário
                // Se o script TCL estiver vazio, usar o XML parseado diretamente (placeholder)
                var intermediateXml = parsedXml;
                if (!string.IsNullOrEmpty(transformationScripts.TclScript))
                {
                    intermediateXml = await ApplyTclTransformationAsync(parsedXml, transformationScripts.TclScript);
                    if (string.IsNullOrEmpty(intermediateXml))
                    {
                        _logger.LogWarning("Transformação TCL retornou vazio, usando XML parseado como intermediário");
                        intermediateXml = parsedXml;
                    }
                }
                else
                {
                    _logger.LogInformation("Script TCL vazio, usando XML parseado como intermediário (transformação TCL não implementada ainda)");
                }

                result.IntermediateXml = intermediateXml;

                // 7. Aplicar transformação XSL para gerar XML final
                // Se o script XSL estiver vazio, usar o XML intermediário diretamente (placeholder)
                var finalXml = intermediateXml;
                if (!string.IsNullOrEmpty(transformationScripts.XslScript))
                {
                    finalXml = await ApplyXslTransformationAsync(intermediateXml, transformationScripts.XslScript);
                    if (string.IsNullOrEmpty(finalXml))
                    {
                        _logger.LogWarning("Transformação XSL retornou vazio, usando XML intermediário como final");
                        finalXml = intermediateXml;
                    }
                }
                else
                {
                    _logger.LogInformation("Script XSL vazio, usando XML intermediário como final (transformação XSL não implementada ainda)");
                }

                result.FinalXml = finalXml;
                result.Success = true;
                result.Warnings.Add("Transformação TCL/XSL ainda não implementada. Retornando XML parseado como resultado.");
                _logger.LogInformation("Transformação concluída com sucesso. IntermediateXml: {IntermediateSize} chars, FinalXml: {FinalSize} chars", 
                    intermediateXml?.Length ?? 0, finalXml?.Length ?? 0);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro durante transformação");
                result.Errors.Add($"Erro interno: {ex.Message}");
                return result;
            }
        }

        private async Task<Models.Parsing.ParsingResult> ParseTextWithLayoutAsync(string text, string layoutXml)
        {
            try
            {
                using var layoutStream = new MemoryStream(Encoding.UTF8.GetBytes(layoutXml));
                using var txtStream = new MemoryStream(Encoding.UTF8.GetBytes(text));

                return await _layoutParserService.ParseAsync(layoutStream, txtStream);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao fazer parse do texto com layout");
                return new Models.Parsing.ParsingResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        private string ConvertParsedFieldsToXml(Models.Parsing.ParsingResult parsedResult)
        {
            try
            {
                var xml = new XDocument();
                var root = new XElement("ROOT");
                xml.Add(root);

                if (parsedResult.ParsedFields == null || !parsedResult.ParsedFields.Any())
                {
                    return xml.ToString();
                }

                // Agrupar campos por linha
                var fieldsByLine = parsedResult.ParsedFields
                    .GroupBy(f => f.LineName)
                    .ToList();

                foreach (var lineGroup in fieldsByLine)
                {
                    var lineElement = new XElement(lineGroup.Key);
                    root.Add(lineElement);

                    foreach (var field in lineGroup)
                    {
                        var fieldElement = new XElement(field.FieldName ?? "Field", field.Value ?? "");
                        lineElement.Add(fieldElement);
                    }
                }

                return xml.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao converter ParsedFields para XML");
                return null;
            }
        }

        private Mapper ParseMapperFromXml(string mapperXml)
        {
            try
            {
                var doc = XDocument.Parse(mapperXml);
                var root = doc.Root;
                
                var mapper = new Mapper
                {
                    DecryptedContent = mapperXml
                };

                var mapperVo = root?.Name.LocalName == "MapperVO" ? root : root?.Element("MapperVO");
                if (mapperVo != null)
                {
                    mapper.InputLayoutGuidFromXml = mapperVo.Element("InputLayoutGuid")?.Value;
                    mapper.TargetLayoutGuidFromXml = mapperVo.Element("TargetLayoutGuid")?.Value;
                }

                return mapper;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao fazer parse do mapeador XML");
                return null;
            }
        }

        private async Task<TransformationScripts> ConvertMapperToTransformationScriptsAsync(Mapper mapper)
        {
            try
            {
                _logger.LogInformation("Convertendo mapeador {Name} (ID: {Id}) para scripts de transformação", 
                    mapper.Name, mapper.Id);
                
                if (string.IsNullOrEmpty(mapper.DecryptedContent))
                {
                    _logger.LogWarning("DecryptedContent vazio para mapeador {Name} (ID: {Id})", mapper.Name, mapper.Id);
                    return new TransformationScripts
                    {
                        TclScript = "",
                        XslScript = ""
                    };
                }
                
                // TODO: Implementar conversão do mapeador XML para TCL e XSL usando aprendizado de máquina
                // Por enquanto, retornar estrutura vazia (que será usada como placeholder)
                // Futuramente, usar aprendizado de máquina para fazer essa conversão
                
                _logger.LogInformation("Mapeador XML disponível (tamanho: {Size} chars). Conversão para TCL/XSL será implementada com ML.", 
                    mapper.DecryptedContent.Length);
                
                // Por enquanto, retornar scripts vazios (a transformação vai passar direto)
                // Isso permite que a transformação funcione mesmo sem os scripts
                return new TransformationScripts
                {
                    TclScript = "", // Será gerado pelo ML no futuro
                    XslScript = ""  // Será gerado pelo ML no futuro
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao converter mapeador para scripts");
                return null;
            }
        }

        private async Task<string> ApplyTclTransformationAsync(string parsedXml, string tclScript)
        {
            try
            {
                // TODO: Implementar aplicação do TCL
                // Por enquanto, retornar o XML parseado
                _logger.LogInformation("Aplicando transformação TCL");
                return parsedXml;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao aplicar transformação TCL");
                return null;
            }
        }

        private async Task<string> ApplyXslTransformationAsync(string intermediateXml, string xslScript)
        {
            try
            {
                // TODO: Implementar aplicação do XSL
                // Por enquanto, retornar o XML intermediário
                _logger.LogInformation("Aplicando transformação XSL");
                return intermediateXml;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao aplicar transformação XSL");
                return null;
            }
        }

        private static string NormalizeLayoutGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid))
                return "";
            
            // Remover prefixos comuns (LAY_, TAG_, GRT_, MAP_, etc.) se houver
            var normalized = guid.Trim();
            
            // Remover prefixo LAY_ se houver (case-insensitive)
            if (normalized.StartsWith("LAY_", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(4);
            
            // Remover prefixo MAP_ se houver (para mapeadores)
            if (normalized.StartsWith("MAP_", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(4);
            
            // Remover espaços e converter para minúsculas para comparação
            normalized = normalized.Trim().ToLowerInvariant();
            
            // Remover chaves {} se houver (caso venha de Guid.ToString())
            normalized = normalized.Replace("{", "").Replace("}", "");
            
            // Remover espaços em branco extras
            normalized = normalized.Trim();
            
            return normalized;
        }
    }

    public class TransformationResult
    {
        public bool Success { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public string IntermediateXml { get; set; }
        public string FinalXml { get; set; }
    }

    public class TransformationScripts
    {
        public string TclScript { get; set; }
        public string XslScript { get; set; }
    }

}

