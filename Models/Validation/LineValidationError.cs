namespace LayoutParserApi.Models.Validation
{
    /// <summary>
    /// Erro encontrado em uma linha específica do layout
    /// </summary>
    public class LineValidationError
    {
        public string LineName { get; set; } = ""; // HEADER, LINHA000, LINHA001, etc.
        public int ExpectedLength { get; set; } = 600;
        public int ActualLength { get; set; }
        public int Difference { get; set; } // Positivo = falta, Negativo = sobra
        public string InitialValue { get; set; } = "";
        public int FieldCount { get; set; }
        public string ErrorMessage { get; set; } = "";
        public bool HasChildren { get; set; }
    }
}
