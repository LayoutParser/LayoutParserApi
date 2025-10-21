namespace LayoutParserApi.Models.Structure
{
    public class LineDetail
    {
        public int LineNumber { get; set; }
        public int Occurrences { get; set; }
        public bool IsRequired { get; set; }
        public bool IsPresent { get; set; }
        public int FieldsCount { get; set; }
        public string SampleContent { get; set; }
        public int TotalLength { get; set; }
    }
}
