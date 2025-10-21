namespace LayoutParserApi.Models.Entities
{
    public class LineInfo
    {
        public string LineName { get; set; } = string.Empty;
        public int LineNumber { get; set; }
        public int Occurrence { get; set; } = 1;
        public int StartPosition { get; set; }
        public int Length { get; set; }
        public string Content { get; set; } = string.Empty;
    }
}
