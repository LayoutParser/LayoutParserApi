namespace LayoutParserApi.Models.Analysis
{
    public class FieldAnalysis
    {
        public string Name { get; set; }
        public string DataType { get; set; }
        public int Length { get; set; }
        public string Pattern { get; set; }
        public List<string> SampleValues { get; set; } = new();
        public bool IsSequential { get; set; }
        public bool IsRequired { get; set; }
        public string ValidationRules { get; set; }
        public string Alignment { get; set; }
        public int StartPosition { get; set; }
        public string Description { get; set; }
        public string LineName { get; set; }
    }
}