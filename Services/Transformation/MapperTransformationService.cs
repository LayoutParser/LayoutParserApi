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
                    mapper = mappers?.FirstOrDefault(m => 
                        (m.TargetLayoutGuid == targetLayoutGuid || m.TargetLayoutGuidFromXml == targetLayoutGuid));
                    
                    if (mapper == null)
                    {
                        result.Errors.Add($"Mapeador não encontrado para InputLayoutGuid={inputLayoutGuid} e TargetLayoutGuid={targetLayoutGuid}");
                        return result;
                    }
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

                var inputLayout = layoutsResponse.Layouts.FirstOrDefault(l => l.LayoutGuid.Equals(inputLayoutGuid) || l.LayoutGuid.ToString() == inputLayoutGuid);

                if (inputLayout == null)
                {
                    result.Errors.Add($"Layout de entrada não encontrado: {inputLayoutGuid}");
                    return result;
                }

                // 3. Fazer parse do texto usando o layout de entrada (igual ao ParseAsync)
                var parsedResult = await ParseTextWithLayoutAsync(inputText, inputLayout.DecryptedContent);
                if (!parsedResult.Success)
                {
                    result.Errors.Add(parsedResult.ErrorMessage ?? "Erro ao fazer parse do texto");
                    return result;
                }

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
                var intermediateXml = await ApplyTclTransformationAsync(parsedXml, transformationScripts.TclScript);
                if (string.IsNullOrEmpty(intermediateXml))
                {
                    result.Errors.Add("Erro ao aplicar transformação TCL");
                    return result;
                }

                result.IntermediateXml = intermediateXml;

                // 6. Aplicar transformação XSL para gerar XML final
                var finalXml = await ApplyXslTransformationAsync(intermediateXml, transformationScripts.XslScript);
                if (string.IsNullOrEmpty(finalXml))
                {
                    result.Errors.Add("Erro ao aplicar transformação XSL");
                    return result;
                }

                result.FinalXml = finalXml;
                result.Success = true;
                _logger.LogInformation("Transformação concluída com sucesso");

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
                // TODO: Implementar conversão do mapeador XML para TCL e XSL
                // Por enquanto, retornar estrutura vazia
                // Futuramente, usar aprendizado de máquina para fazer essa conversão
                
                _logger.LogInformation("Convertendo mapeador para scripts de transformação");
                
                // Aqui seria onde chamaríamos o serviço de aprendizado de máquina
                // para converter o Mapper XML em TCL e XSL
                
                return new TransformationScripts
                {
                    TclScript = "", // Será gerado pelo ML
                    XslScript = ""  // Será gerado pelo ML
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

