namespace LayoutParserApi.Models.RAG
{
    public class FindRelevantRequest
    {
        public string LayoutXml { get; set; } = "";
        public int? MaxExamples { get; set; }
    }
}
