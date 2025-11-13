using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Parsing;
using LayoutParserApi.Models.Database;
using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Parsing.Interfaces;
using LayoutParserApi.Services.XmlAnalysis;
using LayoutParserApi.Services.Transformation;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Xsl;
using Microsoft.Extensions.Logging;

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
                
                // Log detalhado do XML intermediário para visualização
                try
                {
                    // Formatar XML para facilitar visualização
                    var formattedXml = FormatXmlForLogging(intermediateXml);
                    _logger.LogInformation("═══════════════════════════════════════════════════════════════");
                    _logger.LogInformation("📄 XML INTERMEDIÁRIO GERADO (TXT -> XML via TCL)");
                    _logger.LogInformation("═══════════════════════════════════════════════════════════════");
                    _logger.LogInformation("{IntermediateXml}", formattedXml);
                    _logger.LogInformation("═══════════════════════════════════════════════════════════════");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Erro ao formatar XML intermediário para logging. Logando XML original.");
                    _logger.LogInformation("📄 XML INTERMEDIÁRIO (não formatado):");
                    _logger.LogInformation("{IntermediateXml}", intermediateXml);
                }

                // 6. Gerar ou carregar XSL a partir do mapeador
                // Passar o XML intermediário para melhorar a geração do XSL usando exemplos
                var xslPath = await GenerateOrLoadXslAsync(mapper, inputLayout, targetLayoutGuid, intermediateXml);
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
        /// Carrega ou gera arquivo XSL a partir do mapeador
        /// PRIORIDADE: 1. XSL do mapper (Redis), 2. Arquivo XSL existente, 3. Geração (apenas se necessário)
        /// </summary>
        private async Task<string> GenerateOrLoadXslAsync(Mapper mapper, LayoutRecord inputLayout, string targetLayoutGuid, string intermediateXml = null)
        {
            try
            {
                // PRIORIDADE 1: Verificar se o mapper tem XSL armazenado (do Redis/cache)
                if (!string.IsNullOrEmpty(mapper.XslContent))
                {
                    _logger.LogInformation("✅ XSL encontrado no mapeador {Name} (ID: {Id}) do Redis - tamanho: {Size} chars", 
                        mapper.Name, mapper.Id, mapper.XslContent.Length);
                    
                    // Limpar XSL do mapper (remove namespace 'ng', corrige namespaces)
                    var cleanedXsl = CleanXslContent(mapper.XslContent);
                    
                    // Salvar XSL limpo em arquivo temporário para uso na transformação
                    var xslFileName = SanitizeFileName($"{mapper.Name}_{inputLayout.Name}.xsl");
                    var xslPath = Path.Combine(_xslBasePath, xslFileName);
                    
                    // Garantir que o diretório existe
                    Directory.CreateDirectory(_xslBasePath);
                    
                    // Salvar XSL do mapper em arquivo
                    await File.WriteAllTextAsync(xslPath, cleanedXsl, Encoding.UTF8);
                    _logger.LogInformation("✅ XSL do mapper salvo em arquivo: {Path}", xslPath);
                    
                    return xslPath;
                }
                
                // PRIORIDADE 2: Verificar se o arquivo XSL já existe no disco
                var xslFileName2 = SanitizeFileName($"{mapper.Name}_{inputLayout.Name}.xsl");
                var xslPath2 = Path.Combine(_xslBasePath, xslFileName2);
                
                if (File.Exists(xslPath2))
                {
                    _logger.LogInformation("✅ Arquivo XSL já existe: {Path}", xslPath2);
                    return xslPath2;
                }
                
                // PRIORIDADE 3: Gerar XSL apenas se não existir no mapper nem no disco
                // NOTA: A geração de XSL deve ser extinta no futuro, exceto para atualizações de NT ou demandas novas
                _logger.LogWarning("⚠️ XSL não encontrado no mapeador nem em arquivo. Gerando XSL para mapeador: {Name} (ID: {Id})", 
                    mapper.Name, mapper.Id);
                _logger.LogWarning("⚠️ ATENÇÃO: Geração de XSL deve ser extinta no futuro. XSL deve vir do mapeador no Redis.");
                
                if (string.IsNullOrEmpty(mapper.DecryptedContent))
                {
                    _logger.LogError("❌ DecryptedContent vazio para mapeador: {Name}. Não é possível gerar XSL.", mapper.Name);
                    return null;
                }
                
                // Buscar layout de destino para obter nome
                var targetLayout = await _cachedLayoutService.GetLayoutByGuidAsync(targetLayoutGuid);
                var targetLayoutName = targetLayout?.Name ?? targetLayoutGuid;
                
                // Salvar mapeador XML temporariamente para o gerador
                var tempMapperPath = Path.Combine(Path.GetTempPath(), $"mapper_{mapper.Id}_{Guid.NewGuid()}.xml");
                try
                {
                    await File.WriteAllTextAsync(tempMapperPath, mapper.DecryptedContent, Encoding.UTF8);
                    
                    // Gerar XSL usando aprendizado de máquina (apenas como fallback)
                    var xslResult = await _improvedXslGenerator.GenerateXslWithMLAsync(
                        tempMapperPath,
                        targetLayoutName,
                        xslPath2,
                        targetLayoutGuid: targetLayoutGuid,
                        intermediateXml: intermediateXml);
                    
                    if (xslResult.Success && !string.IsNullOrEmpty(xslResult.SuggestedXsl))
                    {
                        // Salvar arquivo XSL gerado
                        await File.WriteAllTextAsync(xslPath2, xslResult.SuggestedXsl, Encoding.UTF8);
                        _logger.LogWarning("⚠️ XSL gerado (fallback) usando ML: {Path}", xslPath2);
                        _logger.LogWarning("⚠️ RECOMENDAÇÃO: Adicionar XSL ao mapeador no banco de dados para evitar geração futura.");
                        
                        if (xslResult.Warnings.Any())
                        {
                            foreach (var warning in xslResult.Warnings)
                            {
                                _logger.LogWarning("⚠️ Aviso XSL: {Warning}", warning);
                            }
                        }
                        
                        return xslPath2;
                    }
                    else if (!string.IsNullOrEmpty(xslResult.GeneratedXsl))
                    {
                        // Usar XSL base se ML não retornou sugestão
                        await File.WriteAllTextAsync(xslPath2, xslResult.GeneratedXsl, Encoding.UTF8);
                        _logger.LogWarning("⚠️ XSL gerado (base, fallback): {Path}", xslPath2);
                        _logger.LogWarning("⚠️ RECOMENDAÇÃO: Adicionar XSL ao mapeador no banco de dados para evitar geração futura.");
                        return xslPath2;
                    }
                    else
                    {
                        // Fallback final: usar gerador base diretamente
                        var baseXsl = await _xslGenerator.GenerateXslFromMapAsync(tempMapperPath, xslPath2);
                        if (!string.IsNullOrEmpty(baseXsl))
                        {
                            _logger.LogWarning("⚠️ XSL gerado (fallback final): {Path}", xslPath2);
                            _logger.LogWarning("⚠️ RECOMENDAÇÃO: Adicionar XSL ao mapeador no banco de dados para evitar geração futura.");
                            return xslPath2;
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
                
                _logger.LogError("❌ Não foi possível gerar XSL para mapeador: {Name}", mapper.Name);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erro ao carregar/gerar XSL para mapeador: {Name}", mapper.Name);
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
                
                // Log: Listar todas as definições de LINE disponíveis no TCL
                var availableLineDefinitions = tclDoc.Descendants("LINE").ToList();
                _logger.LogInformation("📋 TCL contém {Count} definições de LINE:", availableLineDefinitions.Count);
                foreach (var lineDef in availableLineDefinitions)
                {
                    var identifier = lineDef.Attribute("identifier")?.Value ?? "null";
                    var name = lineDef.Attribute("name")?.Value ?? "null";
                    var fieldCount = lineDef.Descendants("FIELD").Count();
                    _logger.LogInformation("  - identifier: '{Identifier}', name: '{Name}', campos: {FieldCount}",
                        identifier, name, fieldCount);
                }
                
                // Processar TXT usando o TCL (MAP)
                // O TCL define a estrutura de linhas e campos
                var txtLines = txtContent.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .ToList();
                
                _logger.LogInformation("📄 TXT contém {Count} linhas (após remover linhas vazias)", txtLines.Count);
                
                var root = new XElement("ROOT");
                var currentLineIndex = 0;
                var processedLines = 0;
                var skippedLines = 0;
                var skippedIdentifiers = new Dictionary<string, int>();
                
                // Processar cada linha do TXT
                foreach (var txtLine in txtLines)
                {
                    if (string.IsNullOrWhiteSpace(txtLine))
                        continue;
                    
                    // Log: Mostrar primeiros caracteres da linha para debug
                    var linePreview = txtLine.Length > 50 ? txtLine.Substring(0, 50) + "..." : txtLine;
                    _logger.LogInformation("🔍 Processando linha {Index}: '{LinePreview}' (tamanho: {Length})",
                        currentLineIndex + 1, linePreview, txtLine.Length);
                    
                    // Detectar tipo de linha baseado no identificador
                    var lineIdentifier = DetectLineIdentifierFromTxt(txtLine);
                    _logger.LogInformation("  → Identificador detectado: '{Identifier}'", lineIdentifier);
                    
                    // Encontrar definição correspondente no TCL (MAP)
                    // Tentar múltiplas estratégias de correspondência
                    var lineDefinition = tclDoc.Descendants("LINE")
                        .FirstOrDefault(l => 
                        {
                            var lIdentifier = l.Attribute("identifier")?.Value?.Trim();
                            var lName = l.Attribute("name")?.Value?.Trim();
                            
                            // Estratégia 1: Correspondência exata (case-insensitive)
                            if (!string.IsNullOrEmpty(lIdentifier) && 
                                lIdentifier.Equals(lineIdentifier, StringComparison.OrdinalIgnoreCase))
                                return true;
                            
                            if (!string.IsNullOrEmpty(lName) && 
                                lName.Equals(lineIdentifier, StringComparison.OrdinalIgnoreCase))
                                return true;
                            
                            // Estratégia 2: HEADER pode corresponder a "H" ou "HEADER"
                            if (lineIdentifier.Equals("HEADER", StringComparison.OrdinalIgnoreCase))
                            {
                                if (lIdentifier?.Equals("H", StringComparison.OrdinalIgnoreCase) == true ||
                                    lName?.Equals("H", StringComparison.OrdinalIgnoreCase) == true)
                                    return true;
                            }
                            
                            if (lineIdentifier.Equals("H", StringComparison.OrdinalIgnoreCase))
                            {
                                if (lIdentifier?.Equals("HEADER", StringComparison.OrdinalIgnoreCase) == true ||
                                    lName?.Equals("HEADER", StringComparison.OrdinalIgnoreCase) == true)
                                    return true;
                            }
                            
                            // Estratégia 3: TRAILER pode corresponder a "T" ou "TRAILER"
                            if (lineIdentifier.Equals("TRAILER", StringComparison.OrdinalIgnoreCase))
                            {
                                if (lIdentifier?.Equals("T", StringComparison.OrdinalIgnoreCase) == true ||
                                    lName?.Equals("T", StringComparison.OrdinalIgnoreCase) == true)
                                    return true;
                            }
                            
                            // Estratégia 4: Correspondência parcial (se um contém o outro)
                            if (!string.IsNullOrEmpty(lIdentifier) && 
                                (lIdentifier.Contains(lineIdentifier, StringComparison.OrdinalIgnoreCase) ||
                                 lineIdentifier.Contains(lIdentifier, StringComparison.OrdinalIgnoreCase)))
                                return true;
                            
                            if (!string.IsNullOrEmpty(lName) && 
                                (lName.Contains(lineIdentifier, StringComparison.OrdinalIgnoreCase) ||
                                 lineIdentifier.Contains(lName, StringComparison.OrdinalIgnoreCase)))
                                return true;
                            
                            return false;
                        });
                    
                    if (lineDefinition != null)
                    {
                        _logger.LogInformation("  ✅ Definição de LINE encontrada no TCL para identificador '{Identifier}'", lineIdentifier);
                        
                        // Extrair campos da linha baseado na definição do TCL
                        var lineElement = ExtractLineFromTxtUsingTcl(txtLine, lineDefinition);
                        if (lineElement != null)
                        {
                            var fieldCount = lineElement.Elements().Count();
                            _logger.LogInformation("  ✅ Linha processada: {FieldCount} campos extraídos", fieldCount);
                            root.Add(lineElement);
                            processedLines++;
                        }
                        else
                        {
                            _logger.LogWarning("  ⚠️ Linha {Index}: Falha ao extrair campos (lineElement é null)", currentLineIndex + 1);
                            skippedLines++;
                        }
                    }
                    else
                    {
                        _logger.LogWarning("  ❌ Linha {Index}: Identificador '{Identifier}' não encontrado no TCL", 
                            currentLineIndex + 1, lineIdentifier);
                        
                        // Contar quantas vezes cada identificador foi ignorado
                        if (skippedIdentifiers.ContainsKey(lineIdentifier))
                            skippedIdentifiers[lineIdentifier]++;
                        else
                            skippedIdentifiers[lineIdentifier] = 1;
                        
                        skippedLines++;
                    }
                    
                    currentLineIndex++;
                }
                
                // Log resumo do processamento
                _logger.LogInformation("📊 Resumo do processamento TCL:");
                _logger.LogInformation("  - Total de linhas: {Total}", txtLines.Count);
                _logger.LogInformation("  - Linhas processadas: {Processed}", processedLines);
                _logger.LogInformation("  - Linhas ignoradas: {Skipped}", skippedLines);
                if (skippedIdentifiers.Any())
                {
                    _logger.LogWarning("  - Identificadores ignorados: {SkippedIds}",
                        string.Join(", ", skippedIdentifiers.Select(kvp => $"{kvp.Key}({kvp.Value}x)")));
                }
                
                var doc = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root);
                var intermediateXml = doc.ToString();
                
                _logger.LogInformation("XML intermediário gerado via TCL: {Size} chars, {LineCount} linhas processadas", 
                    intermediateXml.Length, currentLineIndex);
                
                // Log detalhado da estrutura do XML intermediário
                try
                {
                    var rootElement = doc.Root;
                    if (rootElement != null)
                    {
                        var elementCount = rootElement.Elements().Count();
                        var elementNames = rootElement.Elements().Select(e => e.Name.LocalName).Distinct().ToList();
                        _logger.LogInformation("📊 Estrutura do XML intermediário: {ElementCount} elementos raiz, tipos: {ElementTypes}",
                            elementCount, string.Join(", ", elementNames));
                        
                        // Log de cada elemento raiz com seus campos
                        foreach (var element in rootElement.Elements())
                        {
                            var fieldCount = element.Elements().Count();
                            var fieldNames = element.Elements().Take(10).Select(f => f.Name.LocalName).ToList();
                            _logger.LogInformation("  - {ElementName}: {FieldCount} campos (primeiros: {FieldNames})",
                                element.Name.LocalName, fieldCount, string.Join(", ", fieldNames));
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Erro ao analisar estrutura do XML intermediário");
                }
                
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
                
                // Ler e limpar XSL antes de usar (remove namespace 'ng' e corrige namespaces)
                var xslContent = await File.ReadAllTextAsync(xslPath, Encoding.UTF8);
                xslContent = CleanXslContent(xslContent);
                
                // Salvar XSL limpo temporariamente (ou usar MemoryStream)
                var tempXslPath = Path.Combine(Path.GetTempPath(), $"xsl_{Guid.NewGuid()}.xsl");
                try
                {
                    await File.WriteAllTextAsync(tempXslPath, xslContent, Encoding.UTF8);
                    
                    // Configurar XslCompiledTransform
                    var xslt = new XslCompiledTransform();
                    xslt.Load(tempXslPath, new XsltSettings { EnableDocumentFunction = true }, new XmlUrlResolver());
                
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
                finally
                {
                    // Limpar arquivo temporário
                    try
                    {
                        if (File.Exists(tempXslPath))
                            File.Delete(tempXslPath);
                    }
                    catch { }
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
                        var rootElementFullPattern = $@"<{rootElementName}[^>]*xmlns=""[^""]*""[^>]*>";
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

        /// <summary>
        /// Detecta identificador da linha a partir do TXT
        /// </summary>
        private string DetectLineIdentifierFromTxt(string txtLine)
        {
            if (string.IsNullOrWhiteSpace(txtLine))
                return "UNKNOWN";
            
            // Normalizar linha (trim)
            var normalizedLine = txtLine.Trim();
            if (normalizedLine.Length == 0)
                return "UNKNOWN";
            
            // Estratégia 1: Verificar padrões conhecidos (HEADER, TRAILER, etc.)
            if (normalizedLine.StartsWith("HEADER", StringComparison.OrdinalIgnoreCase) ||
                normalizedLine.Contains("HEADER", StringComparison.OrdinalIgnoreCase))
                return "HEADER";
            
            if (normalizedLine.StartsWith("TRAILER", StringComparison.OrdinalIgnoreCase) ||
                normalizedLine.Contains("TRAILER", StringComparison.OrdinalIgnoreCase))
                return "TRAILER";
            
            if (normalizedLine.Contains("LINHA000", StringComparison.OrdinalIgnoreCase))
                return "LINHA000";
            
            if (normalizedLine.Contains("LINHA001", StringComparison.OrdinalIgnoreCase))
                return "LINHA001";
            
            // Estratégia 2: Para MQSeries: pode ter identificador no início (ex: "A", "B", "H")
            // Verificar se o primeiro caractere é uma letra
            var firstChar = normalizedLine[0];
            if (char.IsLetter(firstChar))
            {
                // Verificar se é um identificador simples seguido de espaço ou dígito
                if (normalizedLine.Length > 1)
                {
                    var secondChar = normalizedLine[1];
                    if (char.IsDigit(secondChar) || char.IsWhiteSpace(secondChar))
                    {
                        // Retornar primeira letra como identificador (A, B, C, H, etc.)
                        return firstChar.ToString().ToUpperInvariant();
                    }
                }
                else
                {
                    // Linha com apenas uma letra
                    return firstChar.ToString().ToUpperInvariant();
                }
            }
            
            // Estratégia 3: Verificar se começa com dígito seguido de letra (ex: "1A", "2B")
            if (normalizedLine.Length >= 2 && char.IsDigit(normalizedLine[0]) && char.IsLetter(normalizedLine[1]))
            {
                return normalizedLine[1].ToString().ToUpperInvariant();
            }
            
            // Estratégia 4: Verificar padrões numéricos comuns (ex: "0001", "0010")
            if (normalizedLine.Length >= 4 && normalizedLine.All(char.IsDigit))
            {
                // Se for um número, pode ser um código de linha
                return normalizedLine.Substring(0, Math.Min(4, normalizedLine.Length));
            }
            
            // Estratégia 5: Usar os primeiros caracteres alfanuméricos como identificador
            var alphanumericStart = new System.Text.StringBuilder();
            foreach (var c in normalizedLine)
            {
                if (char.IsLetterOrDigit(c))
                {
                    alphanumericStart.Append(c);
                    if (alphanumericStart.Length >= 10) // Limitar a 10 caracteres
                        break;
                }
                else if (alphanumericStart.Length > 0)
                {
                    // Parar ao encontrar caractere não alfanumérico
                    break;
                }
            }
            
            if (alphanumericStart.Length > 0)
            {
                var identifier = alphanumericStart.ToString();
                // Se for muito longo, usar apenas os primeiros caracteres
                if (identifier.Length > 10)
                    identifier = identifier.Substring(0, 10);
                return identifier;
            }
            
            // Fallback: usar "UNKNOWN"
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
        /// Formata XML para logging (indenta e facilita visualização)
        /// </summary>
        private string FormatXmlForLogging(string xml)
        {
            try
            {
                if (string.IsNullOrEmpty(xml))
                    return xml;

                // Tentar parsear e formatar o XML
                var doc = XDocument.Parse(xml);
                var formatted = doc.ToString();
                
                // Se o XML for muito grande, truncar mas manter a estrutura
                if (formatted.Length > 10000)
                {
                    var truncated = formatted.Substring(0, 10000);
                    truncated += $"\n\n... (XML truncado - tamanho total: {formatted.Length} chars) ...";
                    return truncated;
                }
                
                return formatted;
            }
            catch
            {
                // Se não conseguir formatar, retornar original
                if (xml.Length > 10000)
                {
                    return xml.Substring(0, 10000) + $"\n\n... (XML truncado - tamanho total: {xml.Length} chars) ...";
                }
                return xml;
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

