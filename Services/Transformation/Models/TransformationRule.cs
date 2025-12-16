namespace LayoutParserApi.Services.Transformation.Models
{
    public class TransformationRule
    {
        public string SourceXPath { get; set; }
        public string TargetXPath { get; set; }
        public string TransformType { get; set; }
        public string SourceElement { get; set; }
        public string TargetElement { get; set; }
        public string TransformPattern { get; set; }
        public double Confidence { get; set; }
    }
}
