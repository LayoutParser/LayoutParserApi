namespace LayoutParserApi.Services.Transformation.Models
{
    // Info classes para parsing
    public class TclLineInfo
    {
        public string LineType { get; set; }
        public string Structure { get; set; }
        public int FieldCount { get; set; }
        public Dictionary<string, string> Attributes { get; set; } = new();
    }
}
