namespace LayoutParserApi.Services.XmlAnalysis
{
    /// <summary>
    /// Requisição de diagnóstico de erro de validação (XSD ou parsing) via LLM local (Ollama).
    /// </summary>
    public class ValidationDiagnosticRequest
    {
        public string ErrorMessage { get; set; } = "";
        public string? FieldName { get; set; }
        public string? MqSeriesSegment { get; set; }
        public string? DocumentType { get; set; }
        public string? TransformedXml { get; set; }
    }

    /// <summary>
    /// Diagnóstico produzido pelo modelo: resumo, sugestão de correção (opcional) e confiança (0.0–1.0).
    /// </summary>
    public class ValidationDiagnostic
    {
        public string Summary { get; set; } = "";
        public string? SuggestedFix { get; set; }
        public double? Confidence { get; set; }
    }

    public class ValidationDiagnosticResponse
    {
        public bool Success { get; set; }
        public ValidationDiagnostic? Diagnostic { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    /// Motivo de falha quando o serviço não consegue produzir um diagnóstico (distinto de
    /// "diagnóstico de baixa confiança", que NÃO é falha — ver tabela de decisão do contrato
    /// em docs/architecture/multi-candidato-e-diagnostico-ia-contrato.md, Gap 2).
    /// </summary>
    public enum DiagnosticFailureKind
    {
        None = 0,
        Unavailable,      // Ollama não respondeu (connection refused etc.) -> 503
        Timeout,           // Ollama excedeu o tempo configurado -> 504
        Infrastructure     // Erro genérico não tratado -> 500
    }

    /// <summary>
    /// Resultado interno do serviço — mapeado pelo controller para o contrato HTTP
    /// (200/400/500/503/504) conforme a tabela de decisão do Gap 2.
    /// </summary>
    public class ValidationDiagnosticResult
    {
        public bool Success { get; set; }
        public ValidationDiagnostic? Diagnostic { get; set; }
        public DiagnosticFailureKind FailureKind { get; set; } = DiagnosticFailureKind.None;
        public string? ErrorMessage { get; set; }

        public static ValidationDiagnosticResult Ok(ValidationDiagnostic diagnostic) =>
            new() { Success = true, Diagnostic = diagnostic };

        public static ValidationDiagnosticResult Fail(DiagnosticFailureKind kind, string errorMessage) =>
            new() { Success = false, FailureKind = kind, ErrorMessage = errorMessage };
    }
}
