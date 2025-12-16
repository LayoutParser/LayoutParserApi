namespace LayoutParserApi.Models
{
    public class ValidationResult
    {
        public bool IsValid { get; set; }
        public decimal? ActualValue { get; set; }
        public decimal? ExpectedValue { get; set; }
        public decimal? Difference { get; set; }
        public string Message { get; set; }
    }
}
