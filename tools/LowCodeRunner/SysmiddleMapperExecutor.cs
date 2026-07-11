using System;
using System.IO;
using System.Security;
using System.Text;
using System.Xml;
using SysMiddle.API;                 // APIManager + APIExecutor (SysMiddle.Base.dll)
using SysMiddle.Base.Model.API;      // MapperBasicVO, MapperResultBasicVO

namespace LayoutParserLowCodeRunner
{
    /// <summary>
    /// Bootstrap do SDK Sysmiddle — idêntico ao MappersHelper.LoadApiExecutor original:
    /// aponta o global.config no APIManager (prop estática) e obtém o APIExecutor pelo package.
    /// A LICENÇA é resolvida via app.config (Spring/InstanceFactory), não em código.
    /// </summary>
    public static class SysmiddleRuntime
    {
        public static APIExecutor Create(string globalFolder, string packageGuid)
        {
            if (!string.IsNullOrEmpty(globalFolder))
                APIManager.GlobalConfigurationFileName = Path.Combine(globalFolder, "global.config");

            return APIManager.Instance.GetApiExecutorByIdentifier(string.Empty, packageGuid);
        }
    }

    /// <summary>
    /// Executa um mapeador Sysmiddle e devolve o documento transformado (thread-safe).
    /// Lógica e pós-processamento NF-e portados do appConnector.Client MappersHelper.
    /// </summary>
    public sealed class SysmiddleMapperExecutor
    {
        private readonly object _lockObj = new object();
        private readonly APIExecutor _apiManager;

        public SysmiddleMapperExecutor(APIExecutor apiManager)
        {
            _apiManager = apiManager ?? throw new ArgumentNullException(nameof(apiManager));
        }

        public string ExecuteMappingDocument(MapperBasicVO mapper, string document, string fileName, bool isEscapeXml = false)
        {
            if (mapper == null) throw new ArgumentNullException(nameof(mapper));

            lock (_lockObj)
            {
                var resultMapper = string.Empty;
                var isTxt = isEscapeXml && !LooksLikeXml(document);

                try
                {
                    if (isEscapeXml && !isTxt)
                        document = ValidateAndEscapeXml(document);

                    if (!string.IsNullOrEmpty(document) && document.Trim().StartsWith("<") && document.Trim().EndsWith(">"))
                        document = InsertDeclaration(document);

                    Log.Info("Iniciando mapeamento - {0}", mapper.IdentifierGuid);
                    var mapperResult = _apiManager.ExecuteMapper(mapper.IdentifierGuid, document, true, fileName);
                    Log.Info("Finalizando mapeamento - {0}", mapper.IdentifierGuid);

                    if (mapperResult != null && mapperResult.ResultMessage != null)
                        foreach (var message in mapperResult.ResultMessage.GetTransformationResultMessages())
                            Log.Info("Resultado transformacao Mapeador {0}: {1}", mapper.IdentifierGuid, message.Message);

                    resultMapper = mapperResult != null ? (mapperResult.TransformedDocument ?? string.Empty) : string.Empty;

                    if (isEscapeXml && isTxt)
                        resultMapper = ValidateAndEscapeXml(resultMapper);
                }
                catch (Exception exception)
                {
                    Log.Error("Falha ao processar o mapeamento: {0}", exception);
                }

                resultMapper = ChangeInfCplValues(resultMapper);
                resultMapper = ChangeInfIdFiscoValues(resultMapper);
                resultMapper = ChangeInfAdProdValues(resultMapper);
                return resultMapper;
            }
        }

        // ---- helpers portados do MappersHelper ----
        private static string InsertDeclaration(string document)
        {
            if (document.Contains("?>"))
                document = document.Remove(0, document.IndexOf("?>", StringComparison.Ordinal) + 2);
            return document.Insert(0, "<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        }

        private static bool LooksLikeXml(string content)
        {
            var t = content.Trim();
            return t.Length >= 2 && t[0] == '<' && (t[1] == '?' || t[1] == '!' || char.IsLetter(t[1]) || t[1] == '/');
        }

        private static string ValidateAndEscapeXml(string content)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            return string.IsNullOrEmpty(content) ? content
                 : (LooksLikeXml(content) ? content : SecurityElement.Escape(content));
        }

        private static string ChangeInfCplValues(string document)   => EscapeNode(document, "descendant::nfeProc/NFe/infNFe/infAdic/infCpl");
        private static string ChangeInfIdFiscoValues(string document) => EscapeNode(document, "descendant::nfeProc/NFe/infNFe/infAdic/infAdFisco");

        private static string ChangeInfAdProdValues(string document)
        {
            try
            {
                var xml = new XmlDocument();
                xml.LoadXml(document);
                var count = xml.DocumentElement?.SelectNodes("/nfeProc/NFe/infNFe/det")?.Count ?? 0;
                for (var i = 1; i <= count; i++)
                {
                    var node = xml.SelectSingleNode(string.Format("nfeProc/NFe/infNFe/det[@nItem={0}]/infAdProd", i));
                    if (node != null)
                        node.InnerText = node.InnerText.Replace(">", "&gt;").Replace("<", "&lt;");
                }
                return WriteXml(xml, document);
            }
            catch { return document; }
        }

        private static string EscapeNode(string document, string xpath)
        {
            try
            {
                var xml = new XmlDocument();
                xml.LoadXml(document);
                var node = xml.SelectSingleNode(xpath);
                if (node != null)
                    node.InnerText = node.InnerText.Replace(">", "&gt;").Replace("<", "&lt;");
                return WriteXml(xml, document);
            }
            catch { return document; }
        }

        private static string WriteXml(XmlDocument xml, string fallback)
        {
            try
            {
                using (var sw = new StringWriter())
                using (var xw = XmlWriter.Create(sw))
                {
                    xml.WriteTo(xw);
                    xw.Flush();
                    return sw.GetStringBuilder().ToString();
                }
            }
            catch { return fallback; }
        }
    }

    internal static class Log
    {
        public static void Info(string f, params object[] a)  => Console.Error.WriteLine("[INFO] "  + string.Format(f, a));
        public static void Error(string f, params object[] a) => Console.Error.WriteLine("[ERROR] " + string.Format(f, a));
    }
}
