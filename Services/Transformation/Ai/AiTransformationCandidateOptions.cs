namespace LayoutParserApi.Services.Transformation.Ai
{
    /// <summary>
    /// Configuração do pathway IA de <c>execute-candidates</c> (seção <c>AiTransformationCandidate</c>
    /// do appsettings). Ver docs/architecture/pathway-ia-execute-candidates.md §6 — "sem SLA de
    /// produto" para o loop em si, mas com teto de sanidade técnica para não vazar jobs "running"
    /// eternamente na store.
    /// </summary>
    public class AiTransformationCandidateOptions
    {
        /// <summary>Máximo de iterações do loop gerar → aplicar → diff → validar → corrigir.</summary>
        public int MaxIterations { get; set; } = 3;

        /// <summary>
        /// Teto de sanidade técnica (não é o timeout de produto, que o dono do projeto disse
        /// explicitamente não querer agora — §2.3 do desenho). Só evita job "running" para sempre
        /// se o Ollama travar/morrer no meio do loop.
        /// </summary>
        public int SanityTimeoutMinutes { get; set; } = 45;

        /// <summary>Onde persistir o job por ticket (padrão: MLData/AiTransformationCandidates).</summary>
        public string? StorePath { get; set; }
    }
}
