namespace LayoutParserApi.Services.Transformation.Ai
{
    /// <summary>
    /// Pathway IA de <c>execute-candidates</c> (Issue #40): gera TCL/XSL/XSLT via loop
    /// gerar → aplicar → diff → validar XSD → corrigir, usando SEMPRE o output do pathway
    /// sysmiddle como gabarito. Ver docs/architecture/pathway-ia-execute-candidates.md.
    /// </summary>
    public interface IAiTransformationCandidateService
    {
        /// <summary>
        /// Dispara o job assíncrono. NUNCA lança para o chamador (fire-and-forget) — toda
        /// falha vira estado "failed" consultável por <see cref="GetStatusAsync"/>.
        /// </summary>
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
}
