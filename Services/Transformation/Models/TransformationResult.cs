namespace LayoutParserApi.Services.Transformation.Models
{
    public class TransformationResult
    {
        public bool Success { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public string IntermediateXml { get; set; }
        public string FinalXml { get; set; }
    }
}
