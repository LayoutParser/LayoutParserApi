using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Summaries;

namespace LayoutParserApi.Models.Parsing
{
    public class ParsingResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public Layout Layout { get; set; }
        public List<ParsedField> ParsedFields { get; set; }
        public string RawText { get; set; }        
        public DocumentSummary Summary { get; set; }

        public List<string> DetectedLines { get; set; } = new();
        public List<LineInfo> LineInfos { get; set; } = new();
    }
}
