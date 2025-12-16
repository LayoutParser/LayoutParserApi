namespace LayoutParserApi.Services.Transformation.Models
{
    public class XslTemplateInfo
    {
        public string TemplateName { get; set; }
        public string TemplateStructure { get; set; }
        public string MatchPattern { get; set; }
        public bool HasApplyTemplates { get; set; }
        public bool HasForEach { get; set; }
        public bool HasChoose { get; set; }
        public int ElementCount { get; set; }
    }
}
