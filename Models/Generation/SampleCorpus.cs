namespace LayoutParserApi.Models.Generation
{
    public class FieldExample
    {
        public string FieldName { get; set; }
        public string DataType { get; set; }
        public string Example { get; set; }
        public string Constraint { get; set; }
    }

    public class SampleCorpus
    {
        public List<FieldExample> Examples { get; set; } = new();
    }
}


