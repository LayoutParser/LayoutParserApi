using LayoutParserApi.Models.Transformation;

namespace LayoutParserApi.Services.Transformation.Ai
{
    /// <summary>
    /// Estado consultável de um job do pathway IA de <c>execute-candidates</c> (Issue #40).
    /// Ver docs/architecture/pathway-ia-execute-candidates.md §4.
    /// </summary>
    public class AiCandidateStatus
    {
        /// <summary>"running" | "converged" | "failed" | "not-applicable" | "not-found"</summary>
        public string Status { get; set; } = "not-found";

        /// <summary>Preenchido só quando <see cref="Status"/> == "converged". Pathway = "ia".</summary>
        public TransformationCandidate? Candidate { get; set; }

        public AiCandidateDiagnostics? Diagnostics { get; set; }

        public const string StatusRunning = "running";
        public const string StatusConverged = "converged";
        public const string StatusFailed = "failed";
        public const string StatusNotApplicable = "not-applicable";
        public const string StatusNotFound = "not-found";
    }

    public class AiCandidateDiagnostics
    {
        public int Iterations { get; set; }

        /// <summary>0 quando convergiu (diff canônico contra o gabarito sysmiddle vazio). Sem
        /// gabarito (<see cref="HasGroundTruth"/> == false) não há diff a contar — fica 0 mesmo
        /// convergindo por XSD + validação de negócio (ver §6 do desenho de fallback automático).</summary>
        public int RemainingDiffs { get; set; }

        public bool XsdValid { get; set; }

        /// <summary>Preenchido só em "failed".</summary>
        public string? LastError { get; set; }

        /// <summary>
        /// <c>true</c> (default) quando o candidato convergiu contra um gabarito real do pathway
        /// sysmiddle (Issue #40 — diff canônico == 0). <c>false</c> no fallback automático (Estado
        /// A — "não encontrado/não modelado", docs/architecture/design-fallback-ia-automatico-2026-08-16.md
        /// §6): não existe <c>groundTruthXml</c> para comparar, então o critério de convergência é
        /// mais fraco (XSD válido + validação de negócio, sem diff estrutural). Consumidores da API
        /// DEVEM tratar <c>HasGroundTruth == false</c> como sugestão para revisão humana, nunca como
        /// resultado pronto para produção — é o único sinal de contrato que expõe essa diferença de
        /// confiança.
        /// </summary>
        public bool HasGroundTruth { get; set; } = true;
    }
}
