using LayoutParserApi.Models.Entities;

namespace LayoutParserApi.Models.Generation
{
    public class SyntheticDataRequest
    {
        public Layout Layout { get; set; }
        public int NumberOfRecords { get; set; }
        public List<ParsedField> SampleRealData { get; set; } = new();
        public ExcelDataContext ExcelContext { get; set; }
        public Dictionary<string, object> CustomRules { get; set; } = new();
        public bool UseAI { get; set; } = true;
        public string AIPrompt { get; set; }
    }
}