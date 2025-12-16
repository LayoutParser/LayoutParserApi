namespace LayoutParserApi.Services.Transformation.Models
{
    public class XslTransformInfo
    {
        public string TransformType { get; set; }
        public string TransformPattern { get; set; }
        public string SourceXPath { get; set; }
        public string ParentElement { get; set; }
    }
}
