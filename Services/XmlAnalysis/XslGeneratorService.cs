using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using LayoutParserApi.Models.Entities;
using Microsoft.Extensions.Logging;

namespace LayoutParserApi.Services.XmlAnalysis
{
    /// <summary>
    /// Gera arquivo XSL a partir do MAP de transformação
    /// </summary>
    public class XslGeneratorService
    {
        private readonly ILogger<XslGeneratorService> _logger;

        public XslGeneratorService(ILogger<XslGeneratorService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Gera arquivo XSL a partir do MAP
        /// </summary>
        public async Task<string> GenerateXslFromMapAsync(string mapXmlPath, string outputPath = null, string exampleXmlPath = null)
        {
            try
            {
                _logger.LogInformation("Iniciando geração de XSL a partir do MAP: {Path}", mapXmlPath);

                // Ler MAP XML
                var mapXml = await File.ReadAllTextAsync(mapXmlPath, Encoding.UTF8);
                var mapDoc = XDocument.Parse(mapXml);

                // Carregar estrutura do XML de exemplo se fornecido
                XmlStructureInfo exampleStructure = null;
                if (!string.IsNullOrEmpty(exampleXmlPath) && File.Exists(exampleXmlPath))
                {
                    try
                    {
                        var exampleXml = await File.ReadAllTextAsync(exampleXmlPath, Encoding.UTF8);
                        exampleStructure = AnalyzeXmlStructure(exampleXml);
                        _logger.LogInformation("Estrutura do XML exemplo analisada: Raiz={RootElement}, Namespaces={NamespacesCount}", 
                            exampleStructure.RootElementName, exampleStructure.Namespaces.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Erro ao analisar estrutura do XML exemplo, usando estrutura padrao");
                    }
                }

                // Gerar XSL
                var xslContent = GenerateXslContent(mapDoc, exampleStructure);

                // Limpar e corrigir XSL gerado (remover namespaces inválidos, garantir namespaces corretos)
                xslContent = CleanAndFixXsl(xslContent);

                // Salvar arquivo se outputPath fornecido
                if (!string.IsNullOrEmpty(outputPath))
                {
                    await File.WriteAllTextAsync(outputPath, xslContent, Encoding.UTF8);
                    _logger.LogInformation("Arquivo XSL gerado com sucesso: {Path}", outputPath);
                }

                return xslContent;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao gerar XSL a partir do MAP");
                throw;
            }
        }

        /// <summary>
        /// Gera conteúdo XSL a partir do MAP
        /// Agora processa Rules (código C#) e LinkMappings (mapeamento direto)
        /// </summary>
        private string GenerateXslContent(XDocument mapDoc, XmlStructureInfo exampleStructure = null)
        {
            var sb = new StringBuilder();

            // Parsear MapperVO para estrutura tipada
            var mapperVo = MapperVo.FromXml(mapDoc);

            // Determinar estrutura baseada no exemplo ou usar padrão
            var rootElementName = exampleStructure?.RootElementName ?? "enviNFe";
            var defaultNamespace = exampleStructure?.DefaultNamespace ?? "http://www.portalfiscal.inf.br/nfe";
            var hasIdLoteAndIndSinc = exampleStructure?.HasIdLoteAndIndSinc ?? true;

            // Cabeçalho XSL
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine("<xsl:stylesheet version=\"1.0\"");
            sb.AppendLine("\txmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\">");
            sb.AppendLine();
            sb.AppendLine("\t<xsl:output method=\"xml\" encoding=\"UTF-8\" indent=\"yes\"/>");
            sb.AppendLine();

            // Template raiz
            sb.AppendLine("\t<xsl:template match=\"/\">");
            
            // Gerar elemento raiz baseado no exemplo (enviNFe se disponível)
            if (rootElementName.Equals("enviNFe", StringComparison.OrdinalIgnoreCase))
            {
                // Estrutura correta: enviNFe como raiz
                sb.AppendLine($"\t\t<enviNFe xmlns=\"{defaultNamespace}\"");
                sb.AppendLine("\t\t\t xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"");
                sb.AppendLine("\t\t\t xsi:schemaLocation=\"http://www.portalfiscal.inf.br/nfe\"");
                sb.AppendLine("\t\t\t versao=\"4.00\">");
                
                // Adicionar idLote e indSinc se estiverem no exemplo
                if (hasIdLoteAndIndSinc)
                {
                    sb.AppendLine("\t\t\t<idLote>");
                    sb.AppendLine("\t\t\t\t<xsl:value-of select=\"normalize-space(ROOT/HEADER/sequencia)\"/>");
                    sb.AppendLine("\t\t\t</idLote>");
                    sb.AppendLine("\t\t\t<indSinc>0</indSinc>");
                }
                
                sb.AppendLine("\t\t\t<NFe>");
                sb.AppendLine("\t\t\t\t<infNFe versao=\"4.00\">");
                sb.AppendLine("\t\t\t\t\t<xsl:attribute name=\"Id\">");
                sb.AppendLine("\t\t\t\t\t\t<xsl:text>NFe</xsl:text>");
                sb.AppendLine("\t\t\t\t\t\t<xsl:value-of select=\"normalize-space(ROOT/chave/chNFe)\"/>");
                sb.AppendLine("\t\t\t\t\t</xsl:attribute>");
            }
            else
            {
                // Estrutura antiga: NFe como raiz (fallback)
                sb.AppendLine($"\t\t<NFe xmlns=\"{defaultNamespace}\"");
                sb.AppendLine("\t\t\t xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">");
                sb.AppendLine("\t\t\t<infNFe versao=\"4.00\">");
                sb.AppendLine("\t\t\t\t<xsl:attribute name=\"Id\">");
                sb.AppendLine("\t\t\t\t\t<xsl:text>NFe</xsl:text>");
                sb.AppendLine("\t\t\t\t\t<xsl:value-of select=\"normalize-space(ROOT/chave/chNFe)\"/>");
                sb.AppendLine("\t\t\t\t</xsl:attribute>");
            }
            sb.AppendLine();

            // Processar Rules (código C#) se existirem
            if (mapperVo != null && mapperVo.Rules != null && mapperVo.Rules.Any())
            {
                _logger.LogInformation("Processando {Count} Rules do MapperVO", mapperVo.Rules.Count);
                ProcessMapperRules(sb, mapperVo.Rules, mapDoc);
            }
            else
            {
                // Fallback: processar Rules do XML diretamente
                var rules = mapDoc.Descendants("Rule").ToList();
                if (rules.Any())
                {
                    _logger.LogInformation("Processando {Count} Rules do XML (fallback)", rules.Count);
                    ProcessRules(sb, rules, mapDoc);
                }
            }

            // Processar LinkMappings (mapeamento direto) se existirem
            if (mapperVo != null && mapperVo.LinkMappings != null && mapperVo.LinkMappings.Any())
            {
                _logger.LogInformation("Processando {Count} LinkMappings do MapperVO", mapperVo.LinkMappings.Count);
                ProcessLinkMappings(sb, mapperVo.LinkMappings, mapDoc);
            }
            else
            {
                // Tentar processar LinkMappings do XML diretamente
                var linkMappings = mapDoc.Descendants("LinkMappingItem").ToList();
                if (linkMappings.Any())
                {
                    _logger.LogInformation("Processando {Count} LinkMappings do XML (fallback)", linkMappings.Count);
                    ProcessLinkMappingsFromXml(sb, linkMappings, mapDoc);
                }
            }

            sb.AppendLine("\t\t\t\t</infNFe>");
            
            // Fechar elementos baseado na estrutura
            if (rootElementName.Equals("enviNFe", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("\t\t\t</NFe>");
                sb.AppendLine("\t\t</enviNFe>");
            }
            else
            {
                sb.AppendLine("\t\t</NFe>");
            }
            
            sb.AppendLine("\t</xsl:template>");
            sb.AppendLine();
            sb.AppendLine("</xsl:stylesheet>");

            return sb.ToString();
        }
        
        /// <summary>
        /// Informações sobre a estrutura de um XML de exemplo
        /// </summary>
        private class XmlStructureInfo
        {
            public string RootElementName { get; set; }
            public string DefaultNamespace { get; set; }
            public Dictionary<string, string> Namespaces { get; set; } = new();
            public bool HasIdLoteAndIndSinc { get; set; }
            public string Versao { get; set; }
        }
        
        /// <summary>
        /// Analisa a estrutura de um XML de exemplo para determinar como gerar o XSL
        /// </summary>
        private XmlStructureInfo AnalyzeXmlStructure(string exampleXml)
        {
            var structure = new XmlStructureInfo();
            
            try
            {
                var doc = XDocument.Parse(exampleXml);
                var root = doc.Root;
                
                if (root == null)
                    return structure;
                
                // Extrair nome do elemento raiz
                structure.RootElementName = root.Name.LocalName;
                
                // Extrair namespace padrão
                structure.DefaultNamespace = root.Name.Namespace.NamespaceName;
                
                // Extrair todos os namespaces
                foreach (var attr in root.Attributes())
                {
                    if (attr.IsNamespaceDeclaration)
                    {
                        var prefix = attr.Name.LocalName == "xmlns" ? "" : attr.Name.LocalName;
                        structure.Namespaces[prefix] = attr.Value;
                    }
                    else if (attr.Name == XName.Get("versao"))
                    {
                        structure.Versao = attr.Value;
                    }
                }
                
                // Verificar se tem idLote e indSinc como filhos diretos do raiz
                var idLote = root.Element(root.Name.Namespace + "idLote");
                var indSinc = root.Element(root.Name.Namespace + "indSinc");
                structure.HasIdLoteAndIndSinc = idLote != null && indSinc != null;
                
                _logger.LogInformation("Estrutura XML analisada: Raiz={Root}, Namespace={Namespace}, TemIdLoteIndSinc={HasIdLote}", 
                    structure.RootElementName, structure.DefaultNamespace, structure.HasIdLoteAndIndSinc);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao analisar estrutura do XML exemplo");
            }
            
            return structure;
        }

        /// <summary>
        /// Processa Rules do MapperVO (código C#)
        /// Transforma código C# como GetConfigParametersValue para XSL
        /// </summary>
        private void ProcessMapperRules(StringBuilder sb, List<MapperRule> rules, XDocument mapDoc)
        {
            // Ordenar Rules por Sequence
            var orderedRules = rules.OrderBy(r => r.Sequence).ToList();

            foreach (var rule in orderedRules)
            {
                if (string.IsNullOrEmpty(rule.ContentValue))
                    continue;

                _logger.LogInformation("Processando Rule: {Name} (Sequence: {Sequence})", rule.Name, rule.Sequence);

                // Processar ContentValue que contém código C#
                ProcessRuleContentValue(sb, rule, mapDoc);
            }
        }

        /// <summary>
        /// Processa ContentValue de uma Rule que contém código C#
        /// Transforma funções C# como GetConfigParametersValue para XSL
        /// </summary>
        private void ProcessRuleContentValue(StringBuilder sb, MapperRule rule, XDocument mapDoc)
        {
            var contentValue = rule.ContentValue ?? "";

            // Remover marcadores %beginRuleContent; e %endRuleContent; se existirem
            contentValue = System.Text.RegularExpressions.Regex.Replace(
                contentValue,
                @"%beginRuleContent;|%endRuleContent;",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // Remover quebras de linha e normalizar espaços para facilitar parsing
            contentValue = System.Text.RegularExpressions.Regex.Replace(contentValue, @"\r\n|\n|\r", " ");
            contentValue = System.Text.RegularExpressions.Regex.Replace(contentValue, @"\s+", " ");

            // Processar atribuições do tipo: T.enviNFe/NFe/dadosAdic/B2BDirectory = GetConfigParametersValue('B2B_Directory');
            // Padrão: T.caminho/do/elemento = FuncaoCSharp('parametro');
            var assignmentPattern = @"T\.([^\s=]+)\s*=\s*([^;]+);";
            var assignments = System.Text.RegularExpressions.Regex.Matches(contentValue, assignmentPattern);

            // Rastrear caminhos já processados para evitar duplicação
            var processedPaths = new HashSet<string>();

            foreach (System.Text.RegularExpressions.Match assignment in assignments)
            {
                var targetPath = assignment.Groups[1].Value.Trim(); // Ex: enviNFe/NFe/dadosAdic/B2BDirectory
                var functionCall = assignment.Groups[2].Value.Trim(); // Ex: GetConfigParametersValue('B2B_Directory')

                // Pular se já processamos este caminho
                if (processedPaths.Contains(targetPath))
                    continue;

                processedPaths.Add(targetPath);

                // Extrair elementos do caminho
                var targetPathParts = targetPath.Split('/').Where(p => !string.IsNullOrEmpty(p)).ToList();
                if (!targetPathParts.Any())
                    continue;

                var targetElementName = SanitizeElementName(targetPathParts.Last());

                // Gerar estrutura hierárquica se necessário
                var indentLevel = 3; // infNFe já tem indentação 3
                foreach (var part in targetPathParts.Take(targetPathParts.Count - 1))
                {
                    var sanitizedPart = SanitizeElementName(part);
                    var indent = new string('\t', indentLevel);
                    sb.AppendLine($"{indent}<{sanitizedPart}>");
                    indentLevel++;
                }

                // Gerar elemento final
                var elementIndent = new string('\t', indentLevel);
                sb.AppendLine($"{elementIndent}<{targetElementName}>");

                // Processar função C# e converter para XSL
                var xslValue = ConvertCSharpFunctionToXsl(functionCall);
                var valueIndent = new string('\t', indentLevel + 1);
                sb.AppendLine($"{valueIndent}{xslValue}");

                sb.AppendLine($"{elementIndent}</{targetElementName}>");

                // Fechar elementos hierárquicos
                for (int i = targetPathParts.Count - 2; i >= 0; i--)
                {
                    indentLevel--;
                    var indent = new string('\t', indentLevel);
                    var sanitizedPart = SanitizeElementName(targetPathParts[i]);
                    sb.AppendLine($"{indent}</{sanitizedPart}>");
                }
            }

            // Processar também mapeamentos diretos do tipo: I.LINHA000/Campo = T.enviNFe/...
            var mappings = ExtractMappings(contentValue);
            foreach (var mapping in mappings)
            {
                var sourcePath = mapping.Key; // Ex: I.LINHA000/Campo
                var targetPath = mapping.Value; // Ex: T.enviNFe/NFe/infNFe/ide/cUF

                // Converter para XPath e XSL
                var xpath = ConvertToXPath(sourcePath);
                var targetElement = GetElementFromPath(targetPath);
                
                // Sanitizar nome do elemento (já está sanitizado em GetElementFromPath, mas garantir)
                targetElement = SanitizeElementName(targetElement);

                // Gerar elemento XSL
                sb.AppendLine($"\t\t\t\t\t<{targetElement}>");
                sb.AppendLine($"\t\t\t\t\t\t<xsl:value-of select=\"{xpath}\"/>");
                sb.AppendLine($"\t\t\t\t\t</{targetElement}>");
            }
        }

        /// <summary>
        /// Converte função C# para XSL
        /// Exemplo: GetConfigParametersValue('B2B_Directory') -> texto fixo ou XSL equivalente
        /// </summary>
        private string ConvertCSharpFunctionToXsl(string functionCall)
        {
            // GetConfigParametersValue('parametro') -> retorna texto fixo ou XSL equivalente
            var getConfigPattern = @"GetConfigParametersValue\s*\(\s*'([^']+)'\s*\)";
            var getConfigMatch = System.Text.RegularExpressions.Regex.Match(functionCall, getConfigPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            
            if (getConfigMatch.Success)
            {
                var parameter = getConfigMatch.Groups[1].Value;
                // Por enquanto, retornar texto vazio (pode ser expandido para buscar do config)
                // Em XSL, isso seria um valor fixo ou uma variável
                return "<xsl:text></xsl:text>"; // Valor vazio por padrão
            }

            // ConcatString('texto1', 'texto2') -> concat('texto1', 'texto2')
            var concatPattern = @"ConcatString\s*\(\s*'([^']+)'\s*,\s*'([^']+)'\s*\)";
            var concatMatch = System.Text.RegularExpressions.Regex.Match(functionCall, concatPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            
            if (concatMatch.Success)
            {
                var text1 = concatMatch.Groups[1].Value;
                var text2 = concatMatch.Groups[2].Value;
                return $"<xsl:value-of select=\"concat('{text1}', '{text2}')\"/>";
            }

            // Se não reconhecer a função, tentar processar como expressão XPath
            // Por enquanto, retornar texto vazio
            _logger.LogWarning("Função C# não reconhecida: {FunctionCall}", functionCall);
            return "<xsl:text></xsl:text>";
        }

        /// <summary>
        /// Processa LinkMappings do MapperVO (mapeamento direto de campos)
        /// LinkMappingItem mapeia campos diretamente do layout de entrada para o layout de saída
        /// O XML intermediário (ROOT) contém os campos extraídos do TXT via TCL
        /// </summary>
        private void ProcessLinkMappings(StringBuilder sb, List<LinkMappingItem> linkMappings, XDocument mapDoc)
        {
            // Ordenar LinkMappings por Sequence
            var orderedLinkMappings = linkMappings.OrderBy(lm => lm.Sequence).ToList();

            foreach (var linkMapping in orderedLinkMappings)
            {
                _logger.LogInformation("Processando LinkMapping: {Name} (InputGuid: {InputGuid}, TargetGuid: {TargetGuid})", 
                    linkMapping.Name, linkMapping.InputLayoutGuid, linkMapping.TargetLayoutGuid);

                if (string.IsNullOrEmpty(linkMapping.Name))
                    continue;

                // Extrair nome do elemento (remover prefixo se houver)
                var elementName = linkMapping.Name;
                if (elementName.Contains("_"))
                {
                    var parts = elementName.Split('_');
                    elementName = parts.Last();
                }
                
                // Sanitizar nome do elemento (remover caracteres inválidos, prefixos reservados)
                elementName = SanitizeElementName(elementName);

                // Gerar XPath para buscar o valor no XML intermediário (ROOT)
                // Estratégias de mapeamento:
                // 1. Tentar buscar pelo nome completo do LinkMapping
                // 2. Tentar buscar pelo nome do elemento (sem prefixo)
                // 3. Tentar buscar em diferentes estruturas do XML intermediário (HEADER, LINHA000, etc.)
                var xpathOptions = GenerateXPathOptionsForLinkMapping(linkMapping.Name, elementName);
                
                // Gerar elemento XSL que busca valor do XML intermediário usando múltiplas estratégias
                sb.AppendLine($"\t\t\t\t\t<{elementName}>");
                sb.AppendLine($"\t\t\t\t\t\t<!-- LinkMapping: {linkMapping.Name} (InputGuid: {linkMapping.InputLayoutGuid}, TargetGuid: {linkMapping.TargetLayoutGuid}) -->");
                
                // Gerar XSL que tenta múltiplos XPaths até encontrar um valor
                // Usar xsl:choose para tentar cada XPath em ordem
                GenerateXslWithMultipleXPaths(sb, elementName, xpathOptions, linkMapping.DefaultValue, linkMapping.AllowEmpty);
                
                sb.AppendLine($"\t\t\t\t\t</{elementName}>");
            }
        }

        /// <summary>
        /// Gera opções de XPath para buscar um campo no XML intermediário baseado no nome do LinkMapping
        /// </summary>
        private List<string> GenerateXPathOptionsForLinkMapping(string linkMappingName, string elementName)
        {
            var xpathOptions = new List<string>();

            // Normalizar nome (remover caracteres especiais, converter para lowercase)
            var normalizedName = linkMappingName.ToLowerInvariant()
                .Replace("_", "")
                .Replace("-", "")
                .Replace(" ", "");

            // Estratégia 1: Buscar diretamente pelo nome do elemento em estruturas comuns (mais provável)
            var commonStructures = new[] { "HEADER", "LINHA000", "LINHA001", "LINHA002", "LINHA003", "TRAILER", "chave", "A", "B", "C", "H" };
            foreach (var structure in commonStructures)
            {
                // Tentar nome do elemento diretamente
                xpathOptions.Add($"ROOT/{structure}/{elementName}");
                // Tentar nome completo do LinkMapping
                xpathOptions.Add($"ROOT/{structure}/{linkMappingName}");
                // Tentar usando local-name() para correspondência case-insensitive
                xpathOptions.Add($"ROOT/{structure}/*[translate(local-name(), 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')='{elementName.ToLowerInvariant()}']");
            }

            // Estratégia 2: Buscar pelo nome completo em qualquer lugar do ROOT (busca recursiva)
            xpathOptions.Add($"ROOT//*[local-name()='{elementName}']");
            xpathOptions.Add($"ROOT//*[local-name()='{linkMappingName}']");
            
            // Estratégia 3: Buscar usando correspondência parcial (se o nome contém o elemento)
            if (linkMappingName.ToLowerInvariant().Contains(elementName.ToLowerInvariant()))
            {
                xpathOptions.Add($"ROOT//*[contains(translate(local-name(), 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz'), '{elementName.ToLowerInvariant()}')]");
            }

            // Estratégia 4: Buscar pelo nome normalizado (sem prefixos, underscores, etc.)
            if (normalizedName != elementName.ToLowerInvariant())
            {
                xpathOptions.Add($"ROOT//*[translate(translate(local-name(), '_', ''), 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')='{normalizedName}']");
            }

            // Estratégia 5: Buscar diretamente no ROOT (menos provável, mas possível)
            xpathOptions.Add($"ROOT/{elementName}");
            xpathOptions.Add($"ROOT/{linkMappingName}");

            // Estratégia 6: Buscar em qualquer elemento filho direto do ROOT
            xpathOptions.Add($"ROOT/*/{elementName}");
            xpathOptions.Add($"ROOT/*/{linkMappingName}");

            // Remover duplicatas mantendo a ordem
            var uniqueOptions = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var option in xpathOptions)
            {
                if (!seen.Contains(option))
                {
                    seen.Add(option);
                    uniqueOptions.Add(option);
                }
            }

            return uniqueOptions;
        }

        /// <summary>
        /// Gera XSL que tenta múltiplos XPaths até encontrar um valor
        /// </summary>
        private void GenerateXslWithMultipleXPaths(StringBuilder sb, string elementName, List<string> xpathOptions, string defaultValue, bool allowEmpty)
        {
            if (xpathOptions == null || !xpathOptions.Any())
            {
                // Se não há opções de XPath, usar valor padrão ou vazio
                if (!string.IsNullOrEmpty(defaultValue))
                {
                    sb.AppendLine($"\t\t\t\t\t\t<xsl:value-of select=\"{EscapeXslString(defaultValue)}\"/>");
                }
                else if (allowEmpty)
                {
                    sb.AppendLine($"\t\t\t\t\t\t<xsl:text></xsl:text>");
                }
                return;
            }

            // Se temos apenas um XPath, usar diretamente
            if (xpathOptions.Count == 1)
            {
                var xpath = xpathOptions[0];
                if (!string.IsNullOrEmpty(defaultValue))
                {
                    sb.AppendLine($"\t\t\t\t\t\t<xsl:choose>");
                    sb.AppendLine($"\t\t\t\t\t\t\t<xsl:when test=\"string-length({xpath}) > 0\">");
                    sb.AppendLine($"\t\t\t\t\t\t\t\t<xsl:value-of select=\"normalize-space({xpath})\"/>");
                    sb.AppendLine($"\t\t\t\t\t\t\t</xsl:when>");
                    sb.AppendLine($"\t\t\t\t\t\t\t<xsl:otherwise>");
                    sb.AppendLine($"\t\t\t\t\t\t\t\t<xsl:value-of select=\"{EscapeXslString(defaultValue)}\"/>");
                    sb.AppendLine($"\t\t\t\t\t\t\t</xsl:otherwise>");
                    sb.AppendLine($"\t\t\t\t\t\t</xsl:choose>");
                }
                else
                {
                    if (!allowEmpty)
                    {
                        sb.AppendLine($"\t\t\t\t\t\t<xsl:if test=\"string-length({xpath}) > 0\">");
                        sb.AppendLine($"\t\t\t\t\t\t\t<xsl:value-of select=\"normalize-space({xpath})\"/>");
                        sb.AppendLine($"\t\t\t\t\t\t</xsl:if>");
                    }
                    else
                    {
                        sb.AppendLine($"\t\t\t\t\t\t<xsl:value-of select=\"normalize-space({xpath})\"/>");
                    }
                }
                return;
            }

            // Se temos múltiplos XPaths, tentar cada um em ordem
            // Usar xsl:choose aninhado para tentar cada XPath
            sb.AppendLine($"\t\t\t\t\t\t<xsl:choose>");
            
            // Tentar cada XPath em ordem (usar apenas os primeiros 5 para não tornar o XSL muito complexo)
            var xpathsToTry = xpathOptions.Take(5).ToList();
            for (int i = 0; i < xpathsToTry.Count; i++)
            {
                var xpath = xpathsToTry[i];
                var isLast = i == xpathsToTry.Count - 1;
                
                sb.AppendLine($"\t\t\t\t\t\t\t<xsl:when test=\"string-length({xpath}) > 0\">");
                sb.AppendLine($"\t\t\t\t\t\t\t\t<xsl:value-of select=\"normalize-space({xpath})\"/>");
                sb.AppendLine($"\t\t\t\t\t\t\t</xsl:when>");
                
                if (!isLast)
                {
                    // Continuar para o próximo XPath
                }
                else
                {
                    // Último XPath: usar default value ou vazio
                    if (!string.IsNullOrEmpty(defaultValue))
                    {
                        sb.AppendLine($"\t\t\t\t\t\t\t<xsl:otherwise>");
                        sb.AppendLine($"\t\t\t\t\t\t\t\t<xsl:value-of select=\"{EscapeXslString(defaultValue)}\"/>");
                        sb.AppendLine($"\t\t\t\t\t\t\t</xsl:otherwise>");
                    }
                    else if (allowEmpty)
                    {
                        sb.AppendLine($"\t\t\t\t\t\t\t<xsl:otherwise>");
                        sb.AppendLine($"\t\t\t\t\t\t\t\t<xsl:text></xsl:text>");
                        sb.AppendLine($"\t\t\t\t\t\t\t</xsl:otherwise>");
                    }
                    // Se não permite vazio e não tem default, não criar o elemento (já tratado acima)
                }
            }
            
            sb.AppendLine($"\t\t\t\t\t\t</xsl:choose>");
        }

        /// <summary>
        /// Escapa string para uso em XSL (evita problemas com aspas)
        /// </summary>
        private string EscapeXslString(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "''";
            
            // Se contém aspas simples, usar concat
            if (value.Contains("'"))
            {
                var parts = value.Split('\'');
                var concatParts = parts.Select(p => $"'{p}'").ToList();
                return $"concat({string.Join(", \"'\", ", concatParts)})";
            }
            
            return $"'{value}'";
        }

        /// <summary>
        /// Processa LinkMappings do XML diretamente (fallback)
        /// </summary>
        private void ProcessLinkMappingsFromXml(StringBuilder sb, List<XElement> linkMappings, XDocument mapDoc)
        {
            foreach (var linkMappingElement in linkMappings)
            {
                var name = linkMappingElement.Element("Name")?.Value;
                var inputGuid = linkMappingElement.Element("InputLayoutGuid")?.Value;
                var targetGuid = linkMappingElement.Element("TargetLayoutGuid")?.Value;
                var defaultValue = linkMappingElement.Element("DefaultValue")?.Value;
                var allowEmptyElement = linkMappingElement.Element("AllowEmpty")?.Value;
                var allowEmpty = allowEmptyElement != null && bool.TryParse(allowEmptyElement, out var allow) && allow;

                if (string.IsNullOrEmpty(name))
                    continue;

                var elementName = name;
                if (elementName.Contains("_"))
                {
                    var parts = elementName.Split('_');
                    elementName = parts.Last();
                }
                
                // Sanitizar nome do elemento (remover caracteres inválidos, prefixos reservados)
                elementName = SanitizeElementName(elementName);

                // Gerar XPath para buscar o valor no XML intermediário
                var xpathOptions = GenerateXPathOptionsForLinkMapping(name, elementName);

                // Gerar elemento XSL
                sb.AppendLine($"\t\t\t\t\t<{elementName}>");
                sb.AppendLine($"\t\t\t\t\t\t<!-- LinkMapping: {name} (InputGuid: {inputGuid}, TargetGuid: {targetGuid}) -->");
                
                // Gerar XSL que tenta múltiplos XPaths até encontrar um valor
                GenerateXslWithMultipleXPaths(sb, elementName, xpathOptions, defaultValue, allowEmpty);
                
                sb.AppendLine($"\t\t\t\t\t</{elementName}>");
            }
        }

        /// <summary>
        /// Processa regras do MAP e gera XSL correspondente
        /// </summary>
        private void ProcessRules(StringBuilder sb, List<XElement> rules, XDocument mapDoc)
        {
            // Agrupar regras por elemento de destino
            var rulesByTarget = rules
                .GroupBy(r => GetTargetElement(r))
                .ToList();

            foreach (var group in rulesByTarget)
            {
                var targetElement = SanitizeElementName(group.Key);
                var targetRules = group.ToList();

                // Gerar elemento XML baseado no target
                GenerateXslElement(sb, targetElement, targetRules, mapDoc);
            }
        }

        /// <summary>
        /// Obtém elemento de destino de uma regra
        /// </summary>
        private string GetTargetElement(XElement rule)
        {
            // Procurar por TargetElementGuid ou analisar ContentValue
            var targetGuid = rule.Element("TargetElementGuid")?.Value;
            var contentValue = rule.Element("ContentValue")?.Value ?? "";

            // Extrair caminho de destino do ContentValue
            // Exemplo: T.enviNFe/NFe/infNFe/Id
            var match = System.Text.RegularExpressions.Regex.Match(
                contentValue,
                @"T\.([^\s=]+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            // Fallback: usar nome da regra
            var ruleName = rule.Element("Name")?.Value ?? "Unknown";
            return MapRuleNameToElement(ruleName);
        }

        /// <summary>
        /// Mapeia nome da regra para elemento XML
        /// </summary>
        private string MapRuleNameToElement(string ruleName)
        {
            if (ruleName.Contains("chave") || ruleName.Contains("Chave")) return "ide";
            if (ruleName.Contains("Cabecalho") || ruleName.Contains("Cabecalho")) return "ide";
            if (ruleName.Contains("Emit")) return "emit";
            if (ruleName.Contains("Dest")) return "dest";
            if (ruleName.Contains("Det") || ruleName.Contains("Produto")) return "det";
            if (ruleName.Contains("Total") || ruleName.Contains("ICMS")) return "total";
            if (ruleName.Contains("Transp")) return "transp";
            if (ruleName.Contains("Cobr") || ruleName.Contains("Pag")) return "cobr";
            if (ruleName.Contains("InfAdic")) return "infAdic";

            return "ide"; // Default
        }

        /// <summary>
        /// Gera elemento XSL
        /// </summary>
        private void GenerateXslElement(StringBuilder sb, string targetElement, List<XElement> rules, XDocument mapDoc)
        {
            var elementPath = targetElement.Split('/');
            var elementName = SanitizeElementName(elementPath.Last());

            // Gerar estrutura hierárquica
            foreach (var part in elementPath)
            {
                if (part == elementPath.Last())
                {
                    var sanitizedPart = SanitizeElementName(part);
                    sb.AppendLine($"\t\t\t\t<{sanitizedPart}>");
                }
            }

            // Processar cada regra
            foreach (var rule in rules)
            {
                GenerateXslRule(sb, rule, mapDoc);
            }

            // Fechar elementos
            foreach (var part in System.Linq.Enumerable.Reverse(elementPath))
            {
                if (part == elementPath.Last())
                {
                    var sanitizedPart = SanitizeElementName(part);
                    sb.AppendLine($"\t\t\t\t</{sanitizedPart}>");
                }
            }
        }

        /// <summary>
        /// Gera XSL para uma regra específica
        /// </summary>
        private void GenerateXslRule(StringBuilder sb, XElement rule, XDocument mapDoc)
        {
            var contentValue = rule.Element("ContentValue")?.Value ?? "";
            var ruleName = rule.Element("Name")?.Value ?? "";

            // Extrair mapeamentos do ContentValue
            // Formato: I.LINHA000/Campo ou I.LINHA001/Campo
            var mappings = ExtractMappings(contentValue);

            foreach (var mapping in mappings)
            {
                var sourcePath = mapping.Key; // Ex: I.LINHA000/Campo
                var targetPath = mapping.Value; // Ex: T.enviNFe/NFe/infNFe/ide/cUF

                // Converter para XPath e XSL
                var xpath = ConvertToXPath(sourcePath);
                var targetElement = GetElementFromPath(targetPath);
                
                // Sanitizar nome do elemento (já está sanitizado em GetElementFromPath, mas garantir)
                targetElement = SanitizeElementName(targetElement);

                // Gerar elemento XSL
                sb.AppendLine($"\t\t\t\t\t<{targetElement}>");
                sb.AppendLine($"\t\t\t\t\t\t<xsl:value-of select=\"{xpath}\"/>");
                sb.AppendLine($"\t\t\t\t\t</{targetElement}>");
            }
        }

        /// <summary>
        /// Extrai mapeamentos do ContentValue
        /// </summary>
        private Dictionary<string, string> ExtractMappings(string contentValue)
        {
            var mappings = new Dictionary<string, string>();

            // Procurar por padrões como: I.LINHA000/Campo = T.enviNFe/...
            var pattern = @"I\.([^/\s]+)/([^\s=]+)\s*=\s*T\.([^\s;]+)";
            var matches = System.Text.RegularExpressions.Regex.Matches(contentValue, pattern);

            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                var source = $"I.{match.Groups[1].Value}/{match.Groups[2].Value}";
                var target = $"T.{match.Groups[3].Value}";
                mappings[source] = target;
            }

            return mappings;
        }

        /// <summary>
        /// Converte caminho de origem para XPath
        /// </summary>
        private string ConvertToXPath(string sourcePath)
        {
            // I.LINHA000/Campo -> ROOT/LINHA000/Campo
            var xpath = sourcePath.Replace("I.", "ROOT/");
            return xpath;
        }

        /// <summary>
        /// Obtém nome do elemento a partir do caminho
        /// </summary>
        private string GetElementFromPath(string targetPath)
        {
            // T.enviNFe/NFe/infNFe/ide/cUF -> cUF
            var parts = targetPath.Split('/');
            return SanitizeElementName(parts.Last());
        }
        
        /// <summary>
        /// Sanitiza nome de elemento XML:
        /// - Remove ou substitui prefixos reservados (xmlns, xml)
        /// - Remove caracteres inválidos para nomes XML
        /// - Garante que o nome segue as regras do XML
        /// </summary>
        private string SanitizeElementName(string elementName)
        {
            if (string.IsNullOrWhiteSpace(elementName))
                return "element";
            
            var sanitized = elementName.Trim();
            
            // Remover prefixo "xmlns" se houver (reservado pelo XML)
            // Não substituir por "ns" pois isso criaria prefixos não declarados
            if (sanitized.StartsWith("xmlns", StringComparison.OrdinalIgnoreCase))
            {
                // Se começa com "xmlns", remover completamente
                if (sanitized.Length > 5 && sanitized[5] == ':')
                {
                    // Caso "xmlns:xxx", remover "xmlns:" e usar apenas "xxx"
                    sanitized = sanitized.Substring(6); // Remover "xmlns:"
                }
                else if (sanitized.Length == 5)
                {
                    // Caso apenas "xmlns", substituir por "element"
                    sanitized = "element";
                }
                else
                {
                    // Caso "xmlnsAlgo", remover "xmlns" e usar apenas "Algo"
                    sanitized = sanitized.Substring(5);
                }
                
                // Se após remover "xmlns" ficou vazio, usar "element"
                if (string.IsNullOrWhiteSpace(sanitized))
                {
                    sanitized = "element";
                }
            }
            
            // Remover prefixo "xml" se houver no início (reservado pelo XML)
            // Mas apenas se for exatamente "xml" ou "xml:" ou "xmlAlgo"
            if (sanitized.StartsWith("xml", StringComparison.OrdinalIgnoreCase))
            {
                if (sanitized.Length == 3)
                {
                    // Caso apenas "xml", substituir por "element"
                    sanitized = "element";
                }
                else if (sanitized.Length > 3 && sanitized[3] == ':')
                {
                    // Caso "xml:xxx", remover "xml:" e usar apenas "xxx" (sem criar prefixo)
                    sanitized = sanitized.Substring(4); // Remover "xml:"
                }
                else if (sanitized.Length > 3 && (char.IsLetter(sanitized[3]) || char.IsDigit(sanitized[3])))
                {
                    // Caso "xmlAlgo", remover "xml" e usar apenas "Algo"
                    sanitized = sanitized.Substring(3);
                }
                
                // Se após remover "xml" ficou vazio, usar "element"
                if (string.IsNullOrWhiteSpace(sanitized))
                {
                    sanitized = "element";
                }
            }
            
            // Remover ou substituir caracteres inválidos para nomes XML
            // XML não permite: < > " ' & espaços no início, : em algumas posições
            var invalidChars = new[] { '<', '>', '"', '\'', '&', ' ', '\t', '\n', '\r' };
            foreach (var invalidChar in invalidChars)
            {
                sanitized = sanitized.Replace(invalidChar, '_');
            }
            
            // Remover caracteres de controle
            sanitized = new string(sanitized.Where(c => !char.IsControl(c)).ToArray());
            
            // Garantir que o nome não começa com número (inválido em XML)
            if (sanitized.Length > 0 && char.IsDigit(sanitized[0]))
            {
                sanitized = "elem" + sanitized;
            }
            
            // Garantir que o nome não está vazio
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                sanitized = "element";
            }
            
            // Remover caracteres inválidos adicionais (caracteres especiais exceto _ - .)
            // IMPORTANTE: Remover ":" para evitar prefixos de namespace não declarados
            sanitized = System.Text.RegularExpressions.Regex.Replace(
                sanitized,
                @"[^\w._-]",
                "_");
            
            // Remover múltiplos underscores consecutivos
            sanitized = System.Text.RegularExpressions.Regex.Replace(
                sanitized,
                @"_+",
                "_");
            
            // Remover underscore no início ou fim
            sanitized = sanitized.Trim('_');
            
            // Garantir que o nome não está vazio após sanitização
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                sanitized = "element";
            }
            
            return sanitized;
        }

        /// <summary>
        /// Limpa e corrige XSL gerado:
        /// - Remove namespace 'ng' (com.neogrid.integrator.XSLFunctions)
        /// - Remove referências ao namespace 'ng' (exclude-result-prefixes, extension-element-prefixes)
        /// - Garante que namespace 'xsi' esteja declarado se for usado
        /// </summary>
        private string CleanAndFixXsl(string xslContent)
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
                // (já está sendo feito no GenerateXslContent, mas vamos garantir)
                if (usesXsi && !System.Text.RegularExpressions.Regex.IsMatch(
                    xslContent,
                    @"<NFe[^>]*xmlns:xsi=""http://www\.w3\.org/2001/XMLSchema-instance""",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    // Adicionar xmlns:xsi no elemento NFe
                    xslContent = System.Text.RegularExpressions.Regex.Replace(
                        xslContent,
                        @"(<NFe[^>]*xmlns=""http://www\.portalfiscal\.inf\.br/nfe"")(>)",
                        @"$1" + Environment.NewLine + "\t\t\t xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"$2",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                }

                _logger.LogInformation("XSL limpo e corrigido: namespace 'ng' removido, namespace 'xsi' verificado");
                return xslContent;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao limpar XSL. Retornando XSL original.");
                return xslContent;
            }
        }
    }
}

