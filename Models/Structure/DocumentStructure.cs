using LayoutParserApi.Models.Validation;

namespace LayoutParserApi.Models.Structure
{
    public class DocumentStructure
    {
        public List<string> LinesPresent { get; set; } = new();
        public List<string> LinesExpected { get; set; } = new();
        public List<string> MissingRequiredLines { get; set; } = new();
        public Dictionary<string, LineDetail> LineDetails { get; set; } = new();
        public DocumentValidation Validation { get; set; } = new();
    }
}
