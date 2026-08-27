namespace LayoutParserApi.Models.Transformation
{
    /// <summary>
    /// Um candidato de transformação, normalizado a partir de QUALQUER um dos dois pathways
    /// (sysmiddle/low-code ou tcl-xsl/canônico), para consumo pelo front-end via
    /// <c>POST /api/transformation-execution/execute-candidates</c>. Ver contrato em
    /// docs/architecture/multi-candidato-e-diagnostico-ia-contrato.md (Gap 1).
    /// </summary>
    public class TransformationCandidate
    {
        public string CandidateId { get; set; } = "";

        /// <summary>"sysmiddle" | "tcl-xsl"</summary>
        public string Pathway { get; set; } = "";

        public string TransformedXml { get; set; } = "";

        public double? Score { get; set; }

        public Dictionary<string, string>? SegmentMappings { get; set; }

        public object? Validation { get; set; }

        /// <summary>Preenchido só quando o candidato falhou parcialmente — hoje não usado no array final
        /// (candidatos que falham não aparecem no array, ver tabela de decisão do contrato), mas mantido
        /// no schema porque o contrato o define explicitamente.</summary>
        public string? FailureReason { get; set; }
    }

    public class TransformationExecutionCandidatesResponse
    {
        public bool Success { get; set; }
        public List<TransformationCandidate> Candidates { get; set; } = new();
        public string? RecommendedCandidateId { get; set; }
        public List<string> Warnings { get; set; } = new();

        /// <summary>Diagnóstico estruturado por pathway (Issue LayoutParserReact #86) — ADITIVO,
        /// não substitui <see cref="Warnings"/>. Vazio hoje: a população dos valores por pathway
        /// (sysmiddle/tcl-xsl/ai-fallback) é feita em cima desta estrutura, ver
        /// <see cref="PathwayDiagnostic"/>.</summary>
        public List<PathwayDiagnostic> PathwayDiagnostics { get; set; } = new();

        /// <summary>CorrelationId da request (<see cref="LayoutParserApi.Services.Logging.CorrelationContext.CurrentId"/>),
        /// permite ao suporte cruzar com o log estruturado completo (não sanitizado) desta chamada.</summary>
        public string? CorrelationId { get; set; }
    }
}
