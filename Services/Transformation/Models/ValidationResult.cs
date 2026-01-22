using System.Collections.Generic;

namespace LayoutParserApi.Services.Transformation.Models
{
    /// <summary>
    /// Resultado simples para validações estruturais (TCL/XSL/XML) feitas internamente.
    /// </summary>
    public class TransformationCheckResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string Details { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }
}