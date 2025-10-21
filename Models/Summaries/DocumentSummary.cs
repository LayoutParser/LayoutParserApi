namespace LayoutParserApi.Models.Summaries
{
    public class DocumentSummary
    {
        public int TotalLines { get; set; }
        public int TotalFields { get; set; }
        public int ValidFields { get; set; }
        public int WarningFields { get; set; }
        public int ErrorFields { get; set; }

        public string DocumentType { get; set; }
        public string LayoutVersion { get; set; }
        public DateTime ProcessingDate { get; set; }

        public int ExpectedLines { get; set; }
        public int PresentLines { get; set; }
        public int MissingLines { get; set; }

        public double ComplianceRate => TotalFields > 0 ? (double)ValidFields / TotalFields * 100 : 0;
        public double StructureRate => ExpectedLines > 0 ? (double)PresentLines / ExpectedLines * 100 : 0;
    }
}
