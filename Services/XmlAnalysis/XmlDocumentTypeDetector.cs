using System;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace LayoutParserApi.Services.XmlAnalysis
{
    /// <summary>
    /// Detecta automaticamente o tipo de documento fiscal (NFe, CTE, NFCom, etc.)
    /// </summary>
    public class XmlDocumentTypeDetector
    {
        private readonly ILogger<XmlDocumentTypeDetector> _logger;

        public XmlDocumentTypeDetector(ILogger<XmlDocumentTypeDetector> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Detecta o tipo de documento fiscal baseado no conteúdo XML
        /// </summary>
        public DocumentTypeInfo DetectDocumentType(string xmlContent)
        {
            try
            {
                var doc = XDocument.Parse(xmlContent);
                var root = doc.Root;

                if (root == null)
                    return new DocumentTypeInfo { Type = "UNKNOWN", XsdVersion = null };

                var rootName = root.Name.LocalName;
                var namespaceUri = root.Name.NamespaceName;

                // Verificar se é enviNFe (wrapper de envio)
                if (rootName == "enviNFe")
                {
                    var nfeElement = root.Elements().FirstOrDefault(e => e.Name.LocalName == "NFe");
                    if (nfeElement != null)
                    {
                        return new DocumentTypeInfo
                        {
                            Type = "NFe",
                            XsdVersion = "PL_010b_NT2025_002_v1.30",
                            Namespace = namespaceUri,
                            RootElement = "NFe"
                        };
                    }
                }

                // Verificar diretamente pelo elemento raiz
                if (rootName == "NFe" || rootName == "nfeProc")
                {
                    return new DocumentTypeInfo
                    {
                        Type = "NFe",
                        XsdVersion = "PL_010b_NT2025_002_v1.30",
                        Namespace = namespaceUri,
                        RootElement = rootName
                    };
                }

                // Verificar CTE
                if (rootName == "enviCTe" || rootName == "CTe" || rootName == "cteProc")
                {
                    return new DocumentTypeInfo
                    {
                        Type = "CTE",
                        XsdVersion = "PL_CTe_300", // Ajustar conforme versão real
                        Namespace = namespaceUri,
                        RootElement = rootName == "enviCTe" ? "CTe" : rootName
                    };
                }

                // Verificar NFCom
                if (rootName == "enviNFCom" || rootName == "NFCom" || rootName == "nfcomProc")
                {
                    return new DocumentTypeInfo
                    {
                        Type = "NFCom",
                        XsdVersion = "PL_NFCom_100", // Ajustar conforme versão real
                        Namespace = namespaceUri,
                        RootElement = rootName == "enviNFCom" ? "NFCom" : rootName
                    };
                }

                // Verificar MDFe
                if (rootName == "enviMDFe" || rootName == "MDFe" || rootName == "mdfeProc")
                {
                    return new DocumentTypeInfo
                    {
                        Type = "MDFe",
                        XsdVersion = "PL_MDFe_300", // Ajustar conforme versão real
                        Namespace = namespaceUri,
                        RootElement = rootName == "enviMDFe" ? "MDFe" : rootName
                    };
                }

                // Verificar pelo namespace
                if (namespaceUri.Contains("portalfiscal.inf.br/nfe"))
                {
                    return new DocumentTypeInfo
                    {
                        Type = "NFe",
                        XsdVersion = "PL_010b_NT2025_002_v1.30",
                        Namespace = namespaceUri,
                        RootElement = rootName
                    };
                }

                if (namespaceUri.Contains("portalfiscal.inf.br/cte"))
                {
                    return new DocumentTypeInfo
                    {
                        Type = "CTE",
                        XsdVersion = "PL_CTe_300",
                        Namespace = namespaceUri,
                        RootElement = rootName
                    };
                }

                if (namespaceUri.Contains("portalfiscal.inf.br/nfcom"))
                {
                    return new DocumentTypeInfo
                    {
                        Type = "NFCom",
                        XsdVersion = "PL_NFCom_100",
                        Namespace = namespaceUri,
                        RootElement = rootName
                    };
                }

                _logger.LogWarning("Tipo de documento não identificado. Root: {RootName}, Namespace: {Namespace}", 
                    rootName, namespaceUri);

                return new DocumentTypeInfo
                {
                    Type = "UNKNOWN",
                    XsdVersion = null,
                    Namespace = namespaceUri,
                    RootElement = rootName
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao detectar tipo de documento XML");
                return new DocumentTypeInfo
                {
                    Type = "UNKNOWN",
                    XsdVersion = null,
                    RootElement = null
                };
            }
        }

        /// <summary>
        /// Detecta o tipo de documento baseado no nome do layout
        /// </summary>
        public DocumentTypeInfo DetectFromLayoutName(string layoutName)
        {
            if (string.IsNullOrWhiteSpace(layoutName))
                return new DocumentTypeInfo { Type = "UNKNOWN", XsdVersion = null };

            var layoutUpper = layoutName.ToUpper();

            if (layoutUpper.Contains("NFE") || layoutUpper.Contains("NFe") || layoutUpper.Contains("NF-E"))
            {
                return new DocumentTypeInfo
                {
                    Type = "NFe",
                    XsdVersion = "PL_010b_NT2025_002_v1.30"
                };
            }

            if (layoutUpper.Contains("CTE") || layoutUpper.Contains("CT-E"))
            {
                return new DocumentTypeInfo
                {
                    Type = "CTE",
                    XsdVersion = "PL_CTe_300"
                };
            }

            if (layoutUpper.Contains("NFCOM") || layoutUpper.Contains("NFCom") || layoutUpper.Contains("NF-COM"))
            {
                return new DocumentTypeInfo
                {
                    Type = "NFCom",
                    XsdVersion = "PL_NFCom_100"
                };
            }

            if (layoutUpper.Contains("MDFE") || layoutUpper.Contains("MDFe") || layoutUpper.Contains("MDF-E"))
            {
                return new DocumentTypeInfo
                {
                    Type = "MDFe",
                    XsdVersion = "PL_MDFe_300"
                };
            }

            return new DocumentTypeInfo { Type = "UNKNOWN", XsdVersion = null };
        }
    }

    /// <summary>
    /// Informações sobre o tipo de documento detectado
    /// </summary>
    public class DocumentTypeInfo
    {
        public string Type { get; set; } = "UNKNOWN";
        public string XsdVersion { get; set; }
        public string Namespace { get; set; }
        public string RootElement { get; set; }
    }
}

