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
        /// <param name="groundTruthXml">
        /// Gabarito sysmiddle (Issue #40, modo COM gabarito — critério de convergência: diff
        /// canônico zero + XSD válido). <c>null</c>/vazio aciona o modo SEM gabarito do fallback
        /// automático de IA (Estado A — docs/architecture/design-fallback-ia-automatico-2026-08-16.md
        /// §6): critério de convergência vira XSD válido + validação de negócio, teto de iterações
        /// <see cref="AiTransformationCandidateOptions.MaxIterationsFallback"/> e o
        /// <see cref="AiCandidateDiagnostics"/> resultante deve marcar <c>HasGroundTruth = false</c>.
        /// A implementação completa desse modo é responsabilidade de <c>@lp-parser-llm</c>
        /// (branch <c>feat/ai-fallback-automatico</c>) — este parâmetro nullable é o contrato que
        /// a orquestração do controller já assume.
        /// </param>
        Task EnqueueAsync(
            string userId,
            string ticket,
            string layoutName,
            Guid layoutGuid,
            string mapperGuid,
            string inputContent,
            string? groundTruthXml,
            CancellationToken cancellationToken);

        /// <param name="userId">Mesmo dono passado a <see cref="EnqueueAsync"/> — ticket de outro
        /// usuário não é encontrado (comporta-se como inexistente, não como erro).</param>
        Task<AiCandidateStatus> GetStatusAsync(string userId, string ticket, CancellationToken cancellationToken);
    }
}
