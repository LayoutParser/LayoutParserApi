namespace LayoutParserApi.Models.Validation
{
    /// <summary>
    /// Erro encontrado em uma linha específica do documento TXT
    /// </summary>
    public class DocumentLineError
    {
        public int LineIndex { get; set; } // Índice da linha (0-based)
        public string Sequence { get; set; } = ""; // Sequência encontrada (6 dígitos)
        public int ExpectedLength { get; set; } = 600;
        public int ActualLength { get; set; }
        public int StartPosition { get; set; } // Posição inicial da linha no documento
        public int EndPosition { get; set; } // Posição final da linha no documento
        public string ErrorMessage { get; set; } = "";
        public string ExpectedNextSequence { get; set; } = ""; // Sequência esperada na próxima linha
    }
}
