using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using LayoutParserApi.Models.XmlAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LayoutParserApi.Services.XmlAnalysis
{
    /// <summary>
    /// Serviço para validação de XML usando XSD (Schema Definition)
    /// </summary>
    public class XsdValidationService
    {
        private readonly ILogger<XsdValidationService> _logger;
        private readonly string _xsdBasePath;
        private readonly string _pdfBasePath;
        private readonly XmlDocumentTypeDetector _documentTypeDetector;
        private readonly IConfiguration _configuration;

        public XsdValidationService(
            ILogger<XsdValidationService> logger,
            IConfiguration configuration,
            XmlDocumentTypeDetector documentTypeDetector)
        {
            _logger = logger;
            _configuration = configuration;
            _documentTypeDetector = documentTypeDetector;
            _xsdBasePath = configuration["XsdValidation:BasePath"] ?? @"C:\inetpub\wwwroot\layoutparser\xsd";
            _pdfBasePath = configuration["XsdValidation:PdfBasePath"] ?? @"C:\inetpub\wwwroot\layoutparser\pdf";
        }

        /// <summary>
        /// Transforma XML de documento fiscal removendo wrapper de envio e adicionando namespace
        /// </summary>
        public string TransformDocumentXml(string xmlContent, DocumentTypeInfo docType = null)
        {
            // Se não forneceu docType, detectar
            if (docType == null)
            {
                docType = _documentTypeDetector.DetectDocumentType(xmlContent);
            }

            // Chamar método específico baseado no tipo
            return docType?.Type switch
            {
                "NFe" => TransformNFeXml(xmlContent),
                "CTE" => TransformCTeXml(xmlContent),
                "NFCom" => TransformNFComXml(xmlContent),
                "MDFe" => TransformMDFeXml(xmlContent),
                _ => TransformNFeXml(xmlContent) // Fallback para NFe
            };
        }

        /// <summary>
        /// Transforma XML NFe removendo tag enviNFe e adicionando namespace
        /// </summary>
        public string TransformNFeXml(string xmlContent)
        {
            try
            {
                var doc = XDocument.Parse(xmlContent);
                var root = doc.Root;

                if (root == null)
                    return xmlContent;

                // Se a tag raiz for enviNFe, remover e pegar o conteúdo de NFe
                if (root.Name.LocalName == "enviNFe")
                {
                    // Encontrar elemento NFe dentro de enviNFe
                    var nfeElement = root.Element(XName.Get("NFe", root.Name.NamespaceName)) ?? 
                                     root.Elements().FirstOrDefault(e => e.Name.LocalName == "NFe");

                    if (nfeElement != null)
                    {
                        // Obter namespace do enviNFe original
                        var nfeNamespace = root.GetDefaultNamespace().NamespaceName;
                        if (string.IsNullOrEmpty(nfeNamespace))
                        {
                            nfeNamespace = "http://www.portalfiscal.inf.br/nfe";
                        }

                        // Criar novo elemento NFe com namespace
                        var newNfeElement = new XElement(XName.Get("NFe", nfeNamespace));
                        
                        // Copiar atributos do NFe original (exceto namespaces que serão definidos)
                        foreach (var attr in nfeElement.Attributes())
                        {
                            if (!attr.Name.LocalName.StartsWith("xmlns") && attr.Name != XNamespace.Xmlns + "xsi")
                            {
                                newNfeElement.SetAttributeValue(attr.Name, attr.Value);
                            }
                        }

                        // Adicionar namespace principal
                        newNfeElement.SetAttributeValue(XName.Get("xmlns"), nfeNamespace);

                        // Adicionar namespace xsi se necessário
                        var xsiAttr = nfeElement.Attribute(XName.Get("xsi", "http://www.w3.org/2000/xmlns/"));
                        if (xsiAttr == null)
                        {
                            var xsiNs = XNamespace.Xmlns + "xsi";
                            newNfeElement.SetAttributeValue(xsiNs, "http://www.w3.org/2001/XMLSchema-instance");
                        }
                        else
                        {
                            newNfeElement.SetAttributeValue(XNamespace.Xmlns + "xsi", xsiAttr.Value);
                        }

                        // Copiar elementos filhos recursivamente preservando estrutura
                        CopyElementsRecursively(nfeElement, newNfeElement);

                        // Criar novo documento
                        var newDoc = new XDocument(newNfeElement);

                        // Salvar XML transformado no resultado (para referência)
                        return newDoc.ToString();
                    }
                }

                return xmlContent;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao transformar XML NFe");
                return xmlContent;
            }
        }

        /// <summary>
        /// Valida XML contra XSD (detecta automaticamente o tipo de documento)
        /// </summary>
        public async Task<XsdValidationResult> ValidateXmlAgainstXsdAsync(string xmlContent, string xsdVersion = null, string layoutName = null)
        {
            var result = new XsdValidationResult
            {
                IsValid = true,
                Errors = new List<XsdValidationError>(),
                Warnings = new List<string>()
            };

            try
            {
                // 1. Detectar tipo de documento automaticamente
                DocumentTypeInfo docType = null;
                
                if (!string.IsNullOrEmpty(layoutName))
                {
                    // Tentar detectar pelo nome do layout primeiro
                    docType = _documentTypeDetector.DetectFromLayoutName(layoutName);
                    _logger.LogInformation("Tipo detectado pelo layout: {Type}, XSD: {XsdVersion}", docType.Type, docType.XsdVersion);
                }

                // Se não detectou pelo layout ou não tinha layout, detectar pelo conteúdo XML
                if (docType == null || docType.Type == "UNKNOWN" || string.IsNullOrEmpty(docType.XsdVersion))
                {
                    docType = _documentTypeDetector.DetectDocumentType(xmlContent);
                    _logger.LogInformation("Tipo detectado pelo XML: {Type}, XSD: {XsdVersion}", docType.Type, docType.XsdVersion);
                }

                // Usar XSD fornecido explicitamente ou o detectado
                var finalXsdVersion = xsdVersion ?? docType?.XsdVersion;
                
                if (string.IsNullOrEmpty(finalXsdVersion))
                {
                    result.IsValid = false;
                    result.Errors.Add(new XsdValidationError
                    {
                        LineNumber = 0,
                        LinePosition = 0,
                        Severity = "Error",
                        Message = "Não foi possível detectar o tipo de documento fiscal. Verifique se o XML é NFe, CTE, NFCom ou MDFe."
                    });
                    _logger.LogWarning("Tipo de documento não detectado");
                    return result;
                }

                // 2. Transformar XML se necessário (remover wrapper de envio)
                var transformedXml = TransformDocumentXml(xmlContent, docType);
                result.TransformedXml = transformedXml;
                result.DocumentType = docType.Type;
                result.XsdVersion = finalXsdVersion;
                _logger.LogInformation("XML transformado para validação XSD. Tipo: {Type}, Versão XSD: {XsdVersion}", docType.Type, finalXsdVersion);

                // 3. Encontrar arquivo XSD
                var xsdPath = FindXsdFile(finalXsdVersion);
                if (string.IsNullOrEmpty(xsdPath) || !File.Exists(xsdPath))
                {
                    result.IsValid = false;
                    result.Errors.Add(new XsdValidationError
                    {
                        LineNumber = 0,
                        LinePosition = 0,
                        Severity = "Error",
                        Message = $"Arquivo XSD não encontrado: {finalXsdVersion}"
                    });
                    _logger.LogWarning("Arquivo XSD não encontrado: {XsdVersion}", finalXsdVersion);
                    return result;
                }

                // 4. Carregar schema XSD com namespace correto
                var schemas = new XmlSchemaSet();
                schemas.ValidationEventHandler += (sender, e) =>
                {
                    _logger.LogWarning("Aviso ao carregar schema XSD: {Message}", e.Message);
                };

                // Obter namespace do tipo de documento
                var targetNamespace = docType?.Namespace;
                if (string.IsNullOrEmpty(targetNamespace))
                {
                    // Tentar obter do XML transformado
                    try
                    {
                        var xDoc = XDocument.Parse(transformedXml);
                        targetNamespace = xDoc.Root?.GetDefaultNamespace().NamespaceName;
                    }
                    catch { }
                }

                // Se ainda não tem namespace, usar padrão baseado no tipo
                if (string.IsNullOrEmpty(targetNamespace))
                {
                    targetNamespace = docType?.Type switch
                    {
                        "NFe" => "http://www.portalfiscal.inf.br/nfe",
                        "CTE" => "http://www.portalfiscal.inf.br/cte",
                        "NFCom" => "http://www.portalfiscal.inf.br/nfcom",
                        "MDFe" => "http://www.portalfiscal.inf.br/mdfe",
                        _ => "http://www.portalfiscal.inf.br/nfe"
                    };
                }

                using (var reader = XmlReader.Create(xsdPath))
                {
                    var schema = XmlSchema.Read(reader, (sender, e) =>
                    {
                        _logger.LogError("Erro ao ler schema XSD: {Message}", e.Message);
                    });
                    
                    if (schema != null)
                    {
                        // Adicionar schema com namespace correto
                        schemas.Add(schema);
                    }
                    else
                    {
                        // Fallback: adicionar diretamente pelo namespace
                        reader.Close();
                        using (var reader2 = XmlReader.Create(xsdPath))
                        {
                            schemas.Add(targetNamespace, reader2);
                        }
                    }
                }

                schemas.Compile();

                // 5. Configurar settings de validação
                var settings = new XmlReaderSettings
                {
                    ValidationType = ValidationType.Schema,
                    Schemas = schemas,
                    ValidationFlags = XmlSchemaValidationFlags.ProcessInlineSchema |
                                     XmlSchemaValidationFlags.ProcessSchemaLocation |
                                     XmlSchemaValidationFlags.ReportValidationWarnings
                };

                // 6. Coletar erros de validação
                var validationErrors = new List<XsdValidationError>();
                settings.ValidationEventHandler += (sender, e) =>
                {
                    if (e.Severity == XmlSeverityType.Error)
                    {
                        validationErrors.Add(new XsdValidationError
                        {
                            LineNumber = e.Exception?.LineNumber ?? 0,
                            LinePosition = e.Exception?.LinePosition ?? 0,
                            Severity = "Error",
                            Message = e.Message
                        });
                    }
                    else if (e.Severity == XmlSeverityType.Warning)
                    {
                        result.Warnings.Add(e.Message);
                    }
                };

                // 6. Validar XML
                using (var xmlReader = XmlReader.Create(new StringReader(transformedXml), settings))
                {
                    try
                    {
                        while (xmlReader.Read()) { }
                    }
                    catch (XmlSchemaValidationException ex)
                    {
                        validationErrors.Add(new XsdValidationError
                        {
                            LineNumber = ex.LineNumber,
                            LinePosition = ex.LinePosition,
                            Severity = "Error",
                            Message = ex.Message
                        });
                    }
                    catch (XmlException ex)
                    {
                        validationErrors.Add(new XsdValidationError
                        {
                            LineNumber = ex.LineNumber,
                            LinePosition = ex.LinePosition,
                            Severity = "Error",
                            Message = ex.Message
                        });
                    }
                }

                result.Errors = validationErrors;
                result.IsValid = validationErrors.Count == 0;

                _logger.LogInformation("Validação XSD concluída: {IsValid}, {ErrorCount} erros, {WarningCount} avisos",
                    result.IsValid, result.Errors.Count, result.Warnings.Count);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro durante validação XSD");
                result.IsValid = false;
                result.Errors.Add(new XsdValidationError
                {
                    LineNumber = 0,
                    LinePosition = 0,
                    Severity = "Error",
                    Message = $"Erro interno durante validação: {ex.Message}"
                });
                return result;
            }
        }

        /// <summary>
        /// Encontra arquivo XSD na pasta especificada
        /// </summary>
        private string FindXsdFile(string version)
        {
            var versionPath = Path.Combine(_xsdBasePath, version);
            
            if (!Directory.Exists(versionPath))
            {
                _logger.LogWarning("Pasta XSD não encontrada: {Path}", versionPath);
                return null;
            }

            // Procurar arquivo .xsd (pode ter vários)
            var xsdFiles = Directory.GetFiles(versionPath, "*.xsd", SearchOption.AllDirectories);
            
            // Priorizar arquivo principal (geralmente o maior ou com nome específico)
            var mainXsd = xsdFiles
                .OrderByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault();

            if (mainXsd != null)
            {
                _logger.LogInformation("Arquivo XSD encontrado: {Path}", mainXsd);
                return mainXsd;
            }

            return null;
        }

        /// <summary>
        /// Obtém orientações do PDF para correção de erros
        /// </summary>
        public async Task<XsdOrientationResult> GetOrientationsAsync(string xsdVersion = "PL_010b_NT2025_002_v1.30", List<string> errorCodes = null)
        {
            var result = new XsdOrientationResult
            {
                Success = false,
                Orientations = new List<string>()
            };

            try
            {
                var pdfPath = Path.Combine(_pdfBasePath, xsdVersion);
                
                if (!Directory.Exists(pdfPath))
                {
                    _logger.LogWarning("Pasta PDF não encontrada: {Path}", pdfPath);
                    result.Orientations.Add("Pasta de orientações PDF não encontrada.");
                    return result;
                }

                // Por enquanto, retornar mensagem genérica
                // TODO: Implementar leitura de PDF (usar biblioteca como PdfSharp ou iTextSharp)
                result.Orientations.Add("Para corrigir os erros de validação XSD:");
                result.Orientations.Add("1. Verifique se todos os campos obrigatórios estão preenchidos");
                result.Orientations.Add("2. Confirme que os valores estão nos formatos corretos (CNPJ, CPF, datas, etc.)");
                result.Orientations.Add("3. Valide que os códigos de produto, CFOP e outras referências estão corretos");
                result.Orientations.Add("4. Consulte a documentação oficial da SEFAZ para a versão " + xsdVersion);
                
                if (errorCodes != null && errorCodes.Any())
                {
                    result.Orientations.Add($"Erros específicos detectados: {string.Join(", ", errorCodes)}");
                }

                result.Success = true;
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao obter orientações do PDF");
                result.Orientations.Add($"Erro ao ler orientações: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// Transforma XML CTE removendo tag enviCTe e adicionando namespace
        /// </summary>
        public string TransformCTeXml(string xmlContent)
        {
            return TransformDocumentWrapper(xmlContent, "enviCTe", "CTe", "http://www.portalfiscal.inf.br/cte");
        }

        /// <summary>
        /// Transforma XML NFCom removendo tag enviNFCom e adicionando namespace
        /// </summary>
        public string TransformNFComXml(string xmlContent)
        {
            return TransformDocumentWrapper(xmlContent, "enviNFCom", "NFCom", "http://www.portalfiscal.inf.br/nfcom");
        }

        /// <summary>
        /// Transforma XML MDFe removendo tag enviMDFe e adicionando namespace
        /// </summary>
        public string TransformMDFeXml(string xmlContent)
        {
            return TransformDocumentWrapper(xmlContent, "enviMDFe", "MDFe", "http://www.portalfiscal.inf.br/mdfe");
        }

        /// <summary>
        /// Método genérico para transformar documentos fiscais removendo wrapper de envio
        /// </summary>
        private string TransformDocumentWrapper(string xmlContent, string wrapperTag, string documentTag, string targetNamespace)
        {
            try
            {
                var doc = XDocument.Parse(xmlContent);
                var root = doc.Root;

                if (root == null)
                    return xmlContent;

                // Se a tag raiz for o wrapper, remover e pegar o conteúdo do documento
                if (root.Name.LocalName == wrapperTag)
                {
                    var documentElement = root.Elements().FirstOrDefault(e => e.Name.LocalName == documentTag);

                    if (documentElement != null)
                    {
                        // Obter namespace do wrapper original
                        var docNamespace = root.GetDefaultNamespace().NamespaceName;
                        if (string.IsNullOrEmpty(docNamespace))
                        {
                            docNamespace = targetNamespace;
                        }

                        // Criar novo elemento com namespace
                        var newDocElement = new XElement(XName.Get(documentTag, docNamespace));
                        
                        // Copiar atributos do documento original (exceto namespaces)
                        foreach (var attr in documentElement.Attributes())
                        {
                            if (!attr.Name.LocalName.StartsWith("xmlns") && 
                                attr.Name != (XNamespace.Xmlns + "xsi") &&
                                !attr.Name.ToString().Contains("schemaLocation"))
                            {
                                newDocElement.SetAttributeValue(attr.Name.LocalName, attr.Value);
                            }
                        }

                        // Adicionar namespace principal
                        newDocElement.SetAttributeValue(XName.Get("xmlns"), docNamespace);

                        // Adicionar namespace xsi se necessário
                        var xsiAttr = root.Attribute(XName.Get("xsi", XNamespace.Xmlns.NamespaceName));
                        if (xsiAttr == null)
                        {
                            var xsiNs = XNamespace.Xmlns + "xsi";
                            newDocElement.SetAttributeValue(xsiNs, "http://www.w3.org/2001/XMLSchema-instance");
                        }
                        else
                        {
                            var xsiNs = XNamespace.Xmlns + "xsi";
                            newDocElement.SetAttributeValue(xsiNs, xsiAttr.Value);
                        }

                        // Copiar elementos filhos recursivamente
                        CopyElementsRecursively(documentElement, newDocElement);

                        // Criar novo documento
                        var newDoc = new XDocument(newDocElement);

                        return newDoc.ToString();
                    }
                }

                return xmlContent;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao transformar XML {DocumentTag}", documentTag);
                return xmlContent;
            }
        }

        /// <summary>
        /// Copia elementos recursivamente preservando estrutura
        /// </summary>
        private void CopyElementsRecursively(XElement source, XElement target)
        {
            foreach (var child in source.Elements())
            {
                var newChild = new XElement(child.Name);
                
                // Copiar atributos
                foreach (var attr in child.Attributes())
                {
                    newChild.SetAttributeValue(attr.Name, attr.Value);
                }

                // Copiar valor de texto se não tiver filhos
                if (!child.HasElements && !string.IsNullOrWhiteSpace(child.Value))
                {
                    newChild.Value = child.Value;
                }

                // Copiar filhos recursivamente
                if (child.HasElements)
                {
                    CopyElementsRecursively(child, newChild);
                }

                target.Add(newChild);
            }
        }
    }
}

