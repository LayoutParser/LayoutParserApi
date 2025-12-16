namespace LayoutParserApi.Models.XmlAnalysis
{
    public class XsdValidationResult
    {
        public bool IsValid { get; set; }
        public List<XsdValidationError> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public string TransformedXml { get; set; }
        public string DocumentType { get; set; }
        public string XsdVersion { get; set; }
        public XsdOrientationResult Orientations { get; set; }
    }
}