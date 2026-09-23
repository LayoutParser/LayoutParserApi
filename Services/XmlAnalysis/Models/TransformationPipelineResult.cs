namespace LayoutParserApi.Services.XmlAnalysis.Models
{
    /// <summary>
    /// Resultado do pipeline de transformação
    /// </summary>
    public class TransformationPipelineResult
    {
        public bool Success { get; set; }
        public string TransformedXml { get; set; }
        public string TclPath { get; set; }
        public string XslPath { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();

        /// <summary>
        /// Código estável da causa de falha (Issue LayoutParserReact #86, pathwayDiagnostics).
        /// "map_not_found" | "xsl_not_found" | null (sucesso ou erro interno não classificado —
        /// nesse caso o chamador cai no "execution_error" genérico). Populado no ponto de origem
        /// (<see cref="TransformationPipelineService"/>), nunca inferido depois por regex sobre
        /// <see cref="Errors"/> — mesma disciplina já usada para <c>FailureKind</c> no controller.
        /// </summary>
        public string ErrorCode { get; set; }
        public Dictionary<string, string> StepResults { get; set; } = new();
        public Dictionary<int, string> SegmentMappings { get; set; } = new();
    }
}
