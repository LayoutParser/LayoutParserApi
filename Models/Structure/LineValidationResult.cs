namespace LayoutParserApi.Models.Structure
{
    public class LineValidationResult
    {
        public string LineName { get; set; }
        public string InitialValue { get; set; }
        public int TotalLength { get; set; }
        public bool IsValid { get; set; }
        public bool HasChildren { get; set; }
        public int FieldCount { get; set; }
        public int ChildCount { get; set; }
    }
}
