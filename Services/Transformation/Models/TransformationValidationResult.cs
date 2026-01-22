using System.Collections.Generic;

namespace LayoutParserApi.Services.Transformation.Models
{
    // Modelos de resultado
    public class TransformationValidationResult
    {
        public bool Success { get; set; }
        public string TransformedXml { get; set; }
        public List<ValidationStep> ValidationSteps { get; set; } = new();
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }
}