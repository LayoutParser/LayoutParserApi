namespace LayoutParserApi.Services.Transformation.Models
{
    public class MappingRule
    {
        public string SourceField { get; set; }
        public string TargetElement { get; set; }
        public string TransformType { get; set; }
        public int SourcePosition { get; set; }
        public int SourceLength { get; set; }
        public string SourceType { get; set; }
        public string TargetXPath { get; set; }
        public double Confidence { get; set; }
    }
}
