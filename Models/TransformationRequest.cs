namespace LayoutParserApi.Models
{
    // Models de request
    public class TransformationRequest
    {
        public string InputContent { get; set; }
        public string LayoutName { get; set; }
        public string SourceDocumentType { get; set; }
        public string TargetDocumentType { get; set; }
        public bool Validate { get; set; } = false;
        public string ExpectedOutput { get; set; }
    }
}
