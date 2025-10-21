namespace LayoutParserApi.Models.Entities
{
    public class ParsedField
    {
        public string LineName { get; set; }
        public string FieldName { get; set; }
        public int Sequence { get; set; }
        public int Start { get; set; }
        public int Length { get; set; }
        public string Value { get; set; }
        public string Status { get; set; }
        public bool IsRequired { get; set; }
        public string DataType { get; set; }
        public int Occurrence { get; set; } = 1;
        public bool IsMissing { get; set; }
        public string LineSequence { get; set; }
        public string FullPath => $"{LineName}.{FieldName}";
        public bool IsAutoDiscovered { get; set; }

        public string ValidationMessage { get; set; }
    }
}
