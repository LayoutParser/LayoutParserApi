using System.Collections.Generic;

namespace LayoutParserApi.Models.XmlAnalysis
{
    public class XsdValidationResult
    {
        public bool IsValid { get; set; }
        public List<XsdValidationError> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public string TransformedXml { get; set; }
        public string DocumentType { get; set; }
        public string XsdVersion { get; set; }
        public XsdOrientationResult Orientations { get; set; }
    }

    public class XsdValidationError
    {
        public int LineNumber { get; set; }
        public int LinePosition { get; set; }
        public string Severity { get; set; }
        public string Message { get; set; }
    }

    public class XsdOrientationResult
    {
        public bool Success { get; set; }
        public List<string> Orientations { get; set; } = new();
    }
}

