namespace LayoutParserApi.Services.XmlAnalysis.Models
{
    /// <summary>
    /// Informações sobre a estrutura de um XML de exemplo
    /// </summary>
    public class XmlStructureInfo
    {
        public string RootElementName { get; set; }
        public string DefaultNamespace { get; set; }
        public Dictionary<string, string> Namespaces { get; set; } = new();
        public bool HasIdLoteAndIndSinc { get; set; }
        public string Versao { get; set; }
    }
}
