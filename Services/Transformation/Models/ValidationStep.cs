namespace LayoutParserApi.Services.Transformation.Models
{
    public class ValidationStep
    {
        public string Step { get; set; }
        public bool Success { get; set; }
        public string Message { get; set; }
        public string Details { get; set; }
    }
}