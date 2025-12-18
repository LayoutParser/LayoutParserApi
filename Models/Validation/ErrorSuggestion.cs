namespace LayoutParserApi.Models.Validation
{
    /// <summary>
    /// Sugestão de correção gerada pelo ML
    /// </summary>
    public class ErrorSuggestion
    {
        public string FieldName { get; set; } = "";
        public int CurrentLength { get; set; }
        public int SuggestedLength { get; set; }
        public string Action { get; set; } = ""; // "truncate", "remove", "adjust"
        public string Reason { get; set; } = "";
        public double Confidence { get; set; }
    }
}