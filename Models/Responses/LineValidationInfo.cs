namespace LayoutParserApi.Models.Responses
{
    /// <summary>
    /// Informações de validação e posições calculadas de uma linha do layout
    /// </summary>
    public class LineValidationInfo
    {
        public string LineName { get; set; }
        public string InitialValue { get; set; }
        public int InitialValueLength { get; set; }
        public int SequenceFromPreviousLine { get; set; }
        public int FieldsLength { get; set; }
        public int SequenciaLength { get; set; }
        public int TotalLength { get; set; }
        public bool IsValid { get; set; }
        public bool HasChildren { get; set; }
        public int FieldCount { get; set; }
        
        /// <summary>
        /// Dicionário com as posições calculadas (1-based) de cada campo
        /// Key: Nome do campo, Value: Posição inicial (1-based)
        /// </summary>
        public Dictionary<string, int> CalculatedPositions { get; set; } = new Dictionary<string, int>();
    }
}

