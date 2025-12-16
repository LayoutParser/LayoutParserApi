using LayoutParserApi.Services.Transformation.Models;

namespace LayoutParserApi.Models
{
    public class LearnFromExamplesRequest
    {
        public string LayoutName { get; set; }
        public List<TclExample> TclExamples { get; set; }
        public List<XslExample> XslExamples { get; set; }
    }
}
