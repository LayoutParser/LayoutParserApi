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
        /// <param name="userId">
        /// Dono do ticket (issue #92 — <c>ICurrentUser.Name</c> resolvido pelo controller). Particiona
        /// a store para que outro usuário nunca consiga ler o status/candidato deste ticket.
        /// </param>
        Task EnqueueAsync(
            string userId,
            string ticket,
            string layoutName,
            Guid layoutGuid,
            string mapperGuid,
            string inputContent,
            string groundTruthXml,
            CancellationToken cancellationToken);

        /// <param name="userId">Mesmo dono passado a <see cref="EnqueueAsync"/> — ticket de outro
        /// usuário não é encontrado (comporta-se como inexistente, não como erro).</param>
        Task<AiCandidateStatus> GetStatusAsync(string userId, string ticket, CancellationToken cancellationToken);
    }
}
