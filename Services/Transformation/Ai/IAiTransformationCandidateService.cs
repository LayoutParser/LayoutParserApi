using LayoutParserApi.Models.Transformation;

namespace LayoutParserApi.Services.Transformation.Ai
{
    /// <summary>
    /// Pathway IA de execute-candidates (Issue #40): gera TCL/XSL/XSLT via loop RAG
    /// (gerar → aplicar → diff canônico → validar XSD → corrigir), usando SEMPRE o output do
    /// pathway sysmiddle como gabarito. Porta o loop de <c>ai/XslSynth</c>
    /// (<c>XslSynth.Core.RepairOrchestrator</c>) para dentro do processo da API como serviço
    /// invocável, sem duplicar a lógica do CLI standalone — ver
    /// docs/architecture/pathway-ia-execute-candidates.md §4.
    /// </summary>
    public interface IAiTransformationCandidateService
    {
        /// <summary>
        /// Dispara o job assíncrono. NUNCA lança para o chamador (fire-and-forget) — toda falha
        /// vira estado "failed" consultável por <see cref="GetStatusAsync"/>. A implementação
        /// decide internamente o mecanismo de fila/persistência — o contrato não prescreve.
        /// </summary>
        /// <param name="ticket">Mesmo ticket usado pelos outros pathways (formato "{sha256}.{layoutGuid}",
        /// ver <c>LowCodeTransformationStore</c>).</param>
        /// <param name="layoutName">Nome do layout (mesmo do request original).</param>
        /// <param name="layoutGuid">Guid do layout de entrada.</param>
        /// <param name="mapperGuid">Mapeador Sysmiddle vencedor no pathway sysmiddle — fonte das
        /// LinkMappings/Rules DSL que a IA traduz.</param>
        /// <param name="inputContent">Mesmo TXT/XML que os outros pathways receberam.</param>
        /// <param name="groundTruthXml">TransformedXml do candidato sysmiddle vencedor — gabarito.</param>
        Task EnqueueAsync(
            string ticket,
            string layoutName,
            Guid layoutGuid,
            string mapperGuid,
            string inputContent,
            string groundTruthXml,
            CancellationToken cancellationToken);

        Task<AiCandidateStatus> GetStatusAsync(string ticket, CancellationToken cancellationToken);
    }

    public class AiCandidateStatus
    {
        /// <summary>"running" | "converged" | "failed" | "not-applicable" | "not-found"</summary>
        public string Status { get; set; } = "not-found";

        /// <summary>Pathway = "ia", preenchido só quando Status == "converged".</summary>
        public TransformationCandidate? Candidate { get; set; }

        public AiCandidateDiagnostics? Diagnostics { get; set; }
    }

    public class AiCandidateDiagnostics
    {
        public int Iterations { get; set; }

        /// <summary>0 quando convergiu.</summary>
        public int RemainingDiffs { get; set; }

        public bool XsdValid { get; set; }

        /// <summary>Preenchido só em "failed".</summary>
        public string? LastError { get; set; }
    }
}
