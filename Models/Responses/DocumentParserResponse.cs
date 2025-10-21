using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Structure;
using LayoutParserApi.Models.Summaries;

namespace LayoutParserApi.Models.Responses
{
    public class DocumentParserResponse
    {
        public bool Success { get; set; }
        public DocumentSummary Summary { get; set; }
        public DocumentStructure DocumentStructure { get; set; }
        public List<ParsedField> Fields { get; set; }
    }
}
