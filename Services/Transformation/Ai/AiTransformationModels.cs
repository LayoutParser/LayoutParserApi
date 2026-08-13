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

        /// <summary>0 quando convergiu (diff canônico contra o gabarito sysmiddle vazio).</summary>
        public int RemainingDiffs { get; set; }

        public bool XsdValid { get; set; }

        /// <summary>Preenchido só em "failed".</summary>
        public string? LastError { get; set; }
    }
}
