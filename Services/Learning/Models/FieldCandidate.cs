namespace LayoutParserApi.Services.Learning.Models
{
    internal class FieldCandidate
    {
        public int StartPosition { get; set; }
        public int EndPosition { get; set; }
        public List<string> Values { get; set; } = new();
    }
}
