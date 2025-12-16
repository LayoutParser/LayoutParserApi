namespace LayoutParserApi.Models
{
    public class ValidationRequest
    {
        public string InputTxt { get; set; }
        public string LayoutName { get; set; }
        public string TclPath { get; set; }
        public string XslPath { get; set; }
        public string ExpectedOutputXml { get; set; }
    }
}
