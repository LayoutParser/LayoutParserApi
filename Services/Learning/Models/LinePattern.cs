namespace LayoutParserApi.Services.Learning.Models
{
    internal class LinePattern
    {
        public string Name { get; set; }
        public string Prefix { get; set; }
        public int SampleCount { get; set; }
        public bool IsMatch(string line) => line.StartsWith(Prefix);
    }
}
