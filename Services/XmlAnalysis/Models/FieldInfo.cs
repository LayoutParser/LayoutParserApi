namespace LayoutParserApi.Services.XmlAnalysis.Models
{
    /// <summary>
    /// Informações de um campo
    /// </summary>
    public class FieldInfo
    {
        public string Name { get; set; }
        public int Length { get; set; }
        public int StartPosition { get; set; }
        public bool IsDecimal { get; set; }
        public int DecimalPlaces { get; set; }
    }
}
