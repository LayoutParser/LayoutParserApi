namespace LayoutParserApi.Models.Database
{
    public class LayoutSearchRequest
    {
        public string SearchTerm { get; set; } = "all_layouts";
        public int MaxResults { get; set; } = 1000;
    }
}