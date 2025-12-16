namespace LayoutParserApi.Services.XmlAnalysis.Models
{
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
