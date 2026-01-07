namespace LayoutParserApi.Models.Database
{
    public class LayoutSearchResponse
    {
        public bool Success { get; set; }
        public List<LayoutRecord> Layouts { get; set; } = new();
        public string ErrorMessage { get; set; } = "";
        public int TotalFound { get; set; }
    }
}