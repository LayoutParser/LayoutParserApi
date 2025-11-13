using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Parsing;
using LayoutParserApi.Models.Database;
using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Parsing.Interfaces;
using LayoutParserApi.Services.XmlAnalysis;
using LayoutParserApi.Services.Transformation;
using Microsoft.Extensions.Configuration;
using System.Text;
using System.Xml.Linq;
using System.Xml.Xsl;
using System.Xml;
using System.IO;

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
        private readonly ImprovedTclGeneratorService _improvedTclGenerator;
        private readonly ImprovedXslGeneratorService _improvedXslGenerator;
        private readonly TclGeneratorService _tclGenerator;
        private readonly XslGeneratorService _xslGenerator;
        private readonly TransformationLearningService _learningService;
        private readonly IConfiguration _configuration;
        private readonly string _tclBasePath;
        private readonly string _xslBasePath;

        public MapperTransformationService(
            ICachedMapperService cachedMapperService,
            ICachedLayoutService cachedLayoutService,
            ILayoutParserService layoutParserService,
            ImprovedTclGeneratorService improvedTclGenerator,
            ImprovedXslGeneratorService improvedXslGenerator,
            TclGeneratorService tclGenerator,
            XslGeneratorService xslGenerator,
            TransformationLearningService learningService,
            IConfiguration configuration,
            ILogger<MapperTransformationService> logger)
        {
            _cachedMapperService = cachedMapperService;
            _cachedLayoutService = cachedLayoutService;
            _layoutParserService = layoutParserService;
            _improvedTclGenerator = improvedTclGenerator;
            _improvedXslGenerator = improvedXslGenerator;
            _tclGenerator = tclGenerator;
            _xslGenerator = xslGenerator;
            _learningService = learningService;
            _configuration = configuration;
            _logger = logger;
            
            // Configurar caminhos para TCL e XSL
            _tclBasePath = configuration["TransformationPipeline:TclPath"] 
                ?? @"C:\inetpub\wwwroot\layoutparser\tcl";
            _xslBasePath = configuration["TransformationPipeline:XslPath"] 
                ?? @"C:\inetpub\wwwroot\layoutparser\xsl";
            
            // Garantir que os diretórios existam
            try
            {
                if (!Directory.Exists(_tclBasePath))
                {
                    Directory.CreateDirectory(_tclBasePath);
                    _logger.LogInformation("Diretório TCL criado: {Path}", _tclBasePath);
                }
                
                if (!Directory.Exists(_xslBasePath))
                {
                    Directory.CreateDirectory(_xslBasePath);
                    _logger.LogInformation("Diretório XSL criado: {Path}", _xslBasePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao criar diretórios TCL/XSL");
            }
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

                // 3. Validar layout de entrada
                if (string.IsNullOrEmpty(inputLayout.DecryptedContent))
                {
                    _logger.LogError("DecryptedContent vazio para layout de entrada: {Name} (ID: {Id})", 
                        inputLayout.Name, inputLayout.Id);
                    result.Errors.Add("Layout de entrada não possui conteúdo descriptografado");
                    return result;
                }
                
                _logger.LogInformation("Validando layout de entrada: {Name} (ID: {Id})", 
                    inputLayout.Name, inputLayout.Id);
                _logger.LogInformation("Tamanho do texto de entrada: {Size} chars", inputText?.Length ?? 0);
                _logger.LogInformation("Tamanho do XML do layout: {Size} chars", inputLayout.DecryptedContent?.Length ?? 0);

                // 4. Gerar ou carregar TCL a partir do layout de entrada
                var tclPath = await GenerateOrLoadTclAsync(inputLayout, mapper);
                if (string.IsNullOrEmpty(tclPath))
                {
                    result.Errors.Add("Não foi possível gerar ou carregar o script TCL");
                    return result;
                }
                
                // 5. Aplicar transformação TCL para gerar XML intermediário
                // O TCL processa o TXT original e gera XML estruturado
                var intermediateXml = await ApplyTclTransformationAsync(inputText, tclPath, inputLayout.Name);
                if (string.IsNullOrEmpty(intermediateXml))
                {
                    result.Errors.Add("Erro ao aplicar transformação TCL");
                    return result;
                }

                result.IntermediateXml = intermediateXml;
                _logger.LogInformation("XML intermediário gerado: {Size} chars", intermediateXml.Length);

                // 6. Gerar ou carregar XSL a partir do mapeador
                var xslPath = await GenerateOrLoadXslAsync(mapper, inputLayout, targetLayoutGuid);
                if (string.IsNullOrEmpty(xslPath))
                {
                    result.Errors.Add("Não foi possível gerar ou carregar o script XSL");
                    return result;
                }

                // 7. Aplicar transformação XSL para gerar XML final
                var finalXml = await ApplyXslTransformationAsync(intermediateXml, xslPath);
                if (string.IsNullOrEmpty(finalXml))
                {
                    result.Errors.Add("Erro ao aplicar transformação XSL");
                    return result;
                }

                result.FinalXml = finalXml;
                result.Success = true;
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

        /// <summary>
        /// Gera ou carrega arquivo TCL a partir do layout de entrada usando aprendizado de máquina
        /// </summary>
        private async Task<string> GenerateOrLoadTclAsync(LayoutRecord inputLayout, Mapper mapper)
        {
            try
            {
                // Gerar nome do arquivo TCL baseado no layout de entrada
                var tclFileName = SanitizeFileName($"{inputLayout.Name}.tcl");
                var tclPath = Path.Combine(_tclBasePath, tclFileName);
                
                // Verificar se o arquivo TCL já existe
                if (File.Exists(tclPath))
                {
                    _logger.LogInformation("Arquivo TCL já existe: {Path}", tclPath);
                    return tclPath;
                }
                
                // Se não existe, gerar usando aprendizado de máquina
                _logger.LogInformation("Gerando TCL para layout: {Name} (ID: {Id})", inputLayout.Name, inputLayout.Id);
                
                // Salvar layout XML temporariamente para o gerador
                var tempLayoutPath = Path.Combine(Path.GetTempPath(), $"layout_{inputLayout.Id}_{Guid.NewGuid()}.xml");
                try
                {
                    await File.WriteAllTextAsync(tempLayoutPath, inputLayout.DecryptedContent, Encoding.UTF8);
                    
                    // Gerar TCL usando aprendizado de máquina
                    var tclResult = await _improvedTclGenerator.GenerateTclWithMLAsync(
                        tempLayoutPath,
                        inputLayout.Name,
                        tclPath);
                    
                    if (tclResult.Success && !string.IsNullOrEmpty(tclResult.SuggestedTcl))
                    {
                        // Salvar arquivo TCL gerado
                        await File.WriteAllTextAsync(tclPath, tclResult.SuggestedTcl, Encoding.UTF8);
                        _logger.LogInformation("TCL gerado com sucesso usando ML: {Path}", tclPath);
                        
                        if (tclResult.Warnings.Any())
                        {
                            foreach (var warning in tclResult.Warnings)
                            {
                                _logger.LogWarning("⚠️ Aviso TCL: {Warning}", warning);
                            }
                        }
                        
                        return tclPath;
                    }
                    else if (!string.IsNullOrEmpty(tclResult.GeneratedTcl))
                    {
                        // Usar TCL base se ML não retornou sugestão
                        await File.WriteAllTextAsync(tclPath, tclResult.GeneratedTcl, Encoding.UTF8);
                        _logger.LogInformation("TCL gerado (base): {Path}", tclPath);
                        return tclPath;
                    }
                    else
                    {
                        // Fallback: usar gerador base diretamente
                        var baseTcl = await _tclGenerator.GenerateTclFromLayoutAsync(tempLayoutPath, tclPath);
                        if (!string.IsNullOrEmpty(baseTcl))
                        {
                            _logger.LogInformation("TCL gerado (fallback): {Path}", tclPath);
                            return tclPath;
                        }
                    }
                }
                finally
                {
                    // Limpar arquivo temporário
                    try
                    {
                        if (File.Exists(tempLayoutPath))
                            File.Delete(tempLayoutPath);
                    }
                    catch { }
                }
                
                _logger.LogWarning("Não foi possível gerar TCL para layout: {Name}", inputLayout.Name);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao gerar/carregar TCL para layout: {Name}", inputLayout.Name);
                return null;
            }
        }

        /// <summary>
        /// Gera ou carrega arquivo XSL a partir do mapeador usando aprendizado de máquina
        /// </summary>
        private async Task<string> GenerateOrLoadXslAsync(Mapper mapper, LayoutRecord inputLayout, string targetLayoutGuid)
        {
            try
            {
                // Gerar nome do arquivo XSL baseado no mapeador e layouts
                var xslFileName = SanitizeFileName($"{mapper.Name}_{inputLayout.Name}.xsl");
                var xslPath = Path.Combine(_xslBasePath, xslFileName);
                
                // Verificar se o arquivo XSL já existe
                if (File.Exists(xslPath))
                {
                    _logger.LogInformation("Arquivo XSL já existe: {Path}", xslPath);
                    return xslPath;
                }
                
                // Se não existe, gerar usando aprendizado de máquina
                _logger.LogInformation("Gerando XSL para mapeador: {Name} (ID: {Id})", mapper.Name, mapper.Id);
                
                if (string.IsNullOrEmpty(mapper.DecryptedContent))
                {
                    _logger.LogWarning("DecryptedContent vazio para mapeador: {Name}", mapper.Name);
                    return null;
                }
                
                // Salvar mapeador XML temporariamente para o gerador
                var tempMapperPath = Path.Combine(Path.GetTempPath(), $"mapper_{mapper.Id}_{Guid.NewGuid()}.xml");
                try
                {
                    await File.WriteAllTextAsync(tempMapperPath, mapper.DecryptedContent, Encoding.UTF8);
                    
                    // Gerar XSL usando aprendizado de máquina
                    var xslResult = await _improvedXslGenerator.GenerateXslWithMLAsync(
                        tempMapperPath,
                        mapper.Name,
                        xslPath);
                    
                    if (xslResult.Success && !string.IsNullOrEmpty(xslResult.SuggestedXsl))
                    {
                        // Salvar arquivo XSL gerado
                        await File.WriteAllTextAsync(xslPath, xslResult.SuggestedXsl, Encoding.UTF8);
                        _logger.LogInformation("XSL gerado com sucesso usando ML: {Path}", xslPath);
                        
                        if (xslResult.Warnings.Any())
                        {
                            foreach (var warning in xslResult.Warnings)
                            {
                                _logger.LogWarning("⚠️ Aviso XSL: {Warning}", warning);
                            }
                        }
                        
                        return xslPath;
                    }
                    else if (!string.IsNullOrEmpty(xslResult.GeneratedXsl))
                    {
                        // Usar XSL base se ML não retornou sugestão
                        await File.WriteAllTextAsync(xslPath, xslResult.GeneratedXsl, Encoding.UTF8);
                        _logger.LogInformation("XSL gerado (base): {Path}", xslPath);
                        return xslPath;
                    }
                    else
                    {
                        // Fallback: usar gerador base diretamente
                        var baseXsl = await _xslGenerator.GenerateXslFromMapAsync(tempMapperPath, xslPath);
                        if (!string.IsNullOrEmpty(baseXsl))
                        {
                            _logger.LogInformation("XSL gerado (fallback): {Path}", xslPath);
                            return xslPath;
                        }
                    }
                }
                finally
                {
                    // Limpar arquivo temporário
                    try
                    {
                        if (File.Exists(tempMapperPath))
                            File.Delete(tempMapperPath);
                    }
                    catch { }
                }
                
                _logger.LogWarning("Não foi possível gerar XSL para mapeador: {Name}", mapper.Name);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao gerar/carregar XSL para mapeador: {Name}", mapper.Name);
                return null;
            }
        }

        /// <summary>
        /// Aplica transformação TCL: TXT -> XML intermediário
        /// O TCL processa o TXT original e gera XML estruturado
        /// </summary>
        private async Task<string> ApplyTclTransformationAsync(string txtContent, string tclPath, string layoutName)
        {
            try
            {
                _logger.LogInformation("Aplicando transformação TCL: {Path}", tclPath);
                
                if (!File.Exists(tclPath))
                {
                    _logger.LogError("Arquivo TCL não encontrado: {Path}", tclPath);
                    return null;
                }
                
                // Ler conteúdo do TCL (MAP)
                var tclContent = await File.ReadAllTextAsync(tclPath, Encoding.UTF8);
                var tclDoc = XDocument.Parse(tclContent);
                
                // Processar TXT usando o TCL (MAP)
                // O TCL define a estrutura de linhas e campos
                var txtLines = txtContent.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .ToList();
                
                var root = new XElement("ROOT");
                var currentLineIndex = 0;
                
                // Processar cada linha do TXT
                foreach (var txtLine in txtLines)
                {
                    if (string.IsNullOrWhiteSpace(txtLine))
                        continue;
                    
                    // Detectar tipo de linha baseado no identificador
                    var lineIdentifier = DetectLineIdentifierFromTxt(txtLine);
                    
                    // Encontrar definição correspondente no TCL (MAP)
                    var lineDefinition = tclDoc.Descendants("LINE")
                        .FirstOrDefault(l => 
                            l.Attribute("identifier")?.Value == lineIdentifier ||
                            l.Attribute("name")?.Value == lineIdentifier ||
                            l.Attribute("name")?.Value?.Equals(lineIdentifier, StringComparison.OrdinalIgnoreCase) == true);
                    
                    if (lineDefinition != null)
                    {
                        // Extrair campos da linha baseado na definição do TCL
                        var lineElement = ExtractLineFromTxtUsingTcl(txtLine, lineDefinition);
                        if (lineElement != null)
                        {
                            root.Add(lineElement);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Linha {Index}: Identificador '{Identifier}' não encontrado no TCL", 
                            currentLineIndex + 1, lineIdentifier);
                    }
                    
                    currentLineIndex++;
                }
                
                var doc = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root);
                var intermediateXml = doc.ToString();
                
                _logger.LogInformation("XML intermediário gerado via TCL: {Size} chars, {LineCount} linhas processadas", 
                    intermediateXml.Length, currentLineIndex);
                
                return intermediateXml;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao aplicar transformação TCL: {Message}", ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Aplica transformação XSL: XML intermediário -> XML final
        /// </summary>
        private async Task<string> ApplyXslTransformationAsync(string intermediateXml, string xslPath)
        {
            try
            {
                _logger.LogInformation("Aplicando transformação XSL: {Path}", xslPath);
                
                if (!File.Exists(xslPath))
                {
                    _logger.LogError("Arquivo XSL não encontrado: {Path}", xslPath);
                    return null;
                }
                
                // Configurar XslCompiledTransform
                var xslt = new XslCompiledTransform();
                xslt.Load(xslPath, new XsltSettings { EnableDocumentFunction = true }, new XmlUrlResolver());
                
                // Carregar XML de entrada
                using (var xmlReader = XmlReader.Create(new StringReader(intermediateXml)))
                using (var stringWriter = new StringWriter())
                using (var xmlWriter = XmlWriter.Create(stringWriter, new XmlWriterSettings
                {
                    Indent = true,
                    IndentChars = "  ",
                    OmitXmlDeclaration = false,
                    Encoding = Encoding.UTF8
                }))
                {
                    // Aplicar transformação
                    xslt.Transform(xmlReader, xmlWriter);
                    var finalXml = stringWriter.ToString();
                    
                    _logger.LogInformation("XML final gerado via XSL: {Size} chars", finalXml.Length);
                    return finalXml;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao aplicar transformação XSL: {Message}", ex.Message);
                _logger.LogError(ex, "Stack trace: {StackTrace}", ex.StackTrace);
                return null;
            }
        }

        /// <summary>
        /// Detecta identificador da linha a partir do TXT
        /// </summary>
        private string DetectLineIdentifierFromTxt(string txtLine)
        {
            if (string.IsNullOrWhiteSpace(txtLine))
                return "UNKNOWN";
            
            // Para MQSeries: pode ter identificador no início (ex: "A", "B", "H")
            if (txtLine.Length >= 1)
            {
                var firstChar = txtLine[0];
                if (char.IsLetter(firstChar))
                {
                    // Verificar se é um identificador simples (A, B, C, etc.)
                    if (txtLine.Length > 1 && (char.IsDigit(txtLine[1]) || char.IsWhiteSpace(txtLine[1])))
                    {
                        return firstChar.ToString();
                    }
                }
            }
            
            // Verificar padrões conhecidos
            if (txtLine.Contains("HEADER", StringComparison.OrdinalIgnoreCase)) return "HEADER";
            if (txtLine.Contains("TRAILER", StringComparison.OrdinalIgnoreCase)) return "TRAILER";
            if (txtLine.Contains("LINHA000", StringComparison.OrdinalIgnoreCase)) return "LINHA000";
            if (txtLine.Contains("LINHA001", StringComparison.OrdinalIgnoreCase)) return "LINHA001";
            
            return "UNKNOWN";
        }

        /// <summary>
        /// Extrai campos de uma linha TXT baseado na definição do TCL
        /// </summary>
        private XElement ExtractLineFromTxtUsingTcl(string txtLine, XElement lineDefinition)
        {
            try
            {
                var lineName = lineDefinition.Attribute("name")?.Value ?? "Line";
                var lineElement = new XElement(lineName);
                
                var fields = lineDefinition.Descendants("FIELD").ToList();
                var currentPosition = 0;
                
                foreach (var fieldDef in fields)
                {
                    var fieldName = fieldDef.Attribute("name")?.Value;
                    var lengthAttr = fieldDef.Attribute("length")?.Value;
                    
                    if (string.IsNullOrEmpty(fieldName) || string.IsNullOrEmpty(lengthAttr))
                        continue;
                    
                    // Parsear length (pode ser "60" ou "15,2,0" para decimais)
                    var lengthParts = lengthAttr.Split(',');
                    if (!int.TryParse(lengthParts[0], out var fieldLength) || fieldLength <= 0)
                        continue;
                    
                    if (currentPosition + fieldLength <= txtLine.Length)
                    {
                        var fieldValue = txtLine.Substring(currentPosition, fieldLength).Trim();
                        lineElement.Add(new XElement(fieldName, fieldValue));
                        currentPosition += fieldLength;
                    }
                    else
                    {
                        // Campo além do tamanho da linha, usar o que restar
                        if (currentPosition < txtLine.Length)
                        {
                            var fieldValue = txtLine.Substring(currentPosition).Trim();
                            lineElement.Add(new XElement(fieldName, fieldValue));
                        }
                        break;
                    }
                }
                
                return lineElement;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao extrair linha do TXT usando TCL");
                return null;
            }
        }

        /// <summary>
        /// Sanitiza nome de arquivo removendo caracteres inválidos
        /// </summary>
        private string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return "unknown";
            
            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = string.Join("_", fileName.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));
            
            // Limitar tamanho do nome
            if (sanitized.Length > 200)
                sanitized = sanitized.Substring(0, 200);
            
            return sanitized;
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

