namespace LayoutParserApi.Models.Analysis
{
    public class LineTypeAnalysis
    {
        public string Name { get; set; }
        public string InitialValue { get; set; }
        public int MinimalOccurrence { get; set; }
        public int MaximumOccurrence { get; set; }
        public List<FieldAnalysis> Fields { get; set; } = new();
        public bool IsRequired { get; set; }
        public int TotalLength { get; set; }
    }
}