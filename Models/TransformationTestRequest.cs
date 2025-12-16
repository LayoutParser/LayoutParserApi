namespace LayoutParserApi.Models
{
    public class TransformationTestRequest
    {
        public string InputTxt { get; set; }
        public string LayoutName { get; set; }
        public string TargetDocumentType { get; set; }
        public string ExpectedOutputXml { get; set; }
    }
}
