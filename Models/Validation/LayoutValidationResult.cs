namespace LayoutParserApi.Models.Validation
{
    /// <summary>
    /// Resultado da validação de um layout completo
    /// </summary>
    public class LayoutValidationResult
    {
        public string LayoutGuid { get; set; } = "";
        public string LayoutName { get; set; } = "";
        public bool IsValid { get; set; }
        public List<LineValidationError> Errors { get; set; } = new();
        public DateTime ValidatedAt { get; set; } = DateTime.UtcNow;
        public int TotalLines { get; set; }
        public int ValidLines { get; set; }
        public int InvalidLines { get; set; }
    }

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

    /// <summary>
    /// Resultado da validação de um documento TXT
    /// </summary>
    public class DocumentValidationResult
    {
        public bool IsValid { get; set; }
        public List<DocumentLineError> LineErrors { get; set; } = new();
        public int TotalLinesProcessed { get; set; }
        public int ValidLinesCount { get; set; }
        public int InvalidLinesCount { get; set; }
        public string ErrorMessage { get; set; } = "";
        public bool ProcessingStopped { get; set; } // Se processamento foi interrompido
    }

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

    /// <summary>
    /// Request para validar layout por GUID
    /// </summary>
    public class LayoutValidationRequest
    {
        public List<string> LayoutGuids { get; set; } = new(); // Se vazio, valida todos
        public bool ForceRevalidation { get; set; } = false; // Forçar revalidação mesmo se já validado
    }

    /// <summary>
    /// Sugestão de correção gerada pelo ML
    /// </summary>
    public class ErrorSuggestion
    {
        public string FieldName { get; set; } = "";
        public int CurrentLength { get; set; }
        public int SuggestedLength { get; set; }
        public string Action { get; set; } = ""; // "truncate", "remove", "adjust"
        public string Reason { get; set; } = "";
        public double Confidence { get; set; }
    }
}

