namespace LayoutParserApi.Services.Transformation.Models
{
    public class TclFieldInfo
    {
        public string FieldType { get; set; }
        public string Mapping { get; set; }
        public int StartPosition { get; set; }
        public int Length { get; set; }
        public string FieldName { get; set; }
        public string LineName { get; set; }
    }
}
