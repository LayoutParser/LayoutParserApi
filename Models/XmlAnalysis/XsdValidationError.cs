namespace LayoutParserApi.Models.XmlAnalysis
{
    public class XsdValidationError
    {
        public int LineNumber { get; set; }
        public int LinePosition { get; set; }
        public string Severity { get; set; }
        public string Message { get; set; }
    }
}
