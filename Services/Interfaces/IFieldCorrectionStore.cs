using LayoutParserApi.Models.Fiscal;

namespace LayoutParserApi.Services.Interfaces
{
    /// <summary>
    /// Persistência do contrato de correção guiada por humano (ADR
    /// docs/architecture/adr-contrato-correcao-guiada-humano-2026-09-08.md, issue #345):
    /// contexto de documento (gravado best-effort em <c>execute-candidates</c>) + reportes de
    /// campo divergente (gravados por <c>POST field-correction</c>). Banco <c>IdentityDatabase:*</c>
    /// — nunca <c>Database:*</c>/172.31.249.51 (ver .claude/rules/security.md).
    /// </summary>
    public interface IFieldCorrectionStore
    {
        /// <summary>
        /// Grava o contexto do documento — idempotente por <c>DocumentId</c> (determinístico):
        /// reenviar o mesmo documento não sobrescreve nem duplica a linha já gravada.
        /// </summary>
        Task SaveContextAsync(FieldCorrectionContext context, CancellationToken cancellationToken);

        /// <summary>Retorna <c>null</c> quando o contexto nunca foi gravado ou já expirou (fora de escopo desta issue).</summary>
        Task<FieldCorrectionContext?> GetContextAsync(string documentId, CancellationToken cancellationToken);

        /// <summary>Cria o reporte com <c>Status = pending</c> (ADR §6) e retorna o <c>ReportId</c> gerado.</summary>
        Task<Guid> CreateReportAsync(FieldCorrectionReportInput input, Guid reportedByUserId, CancellationToken cancellationToken);

        /// <summary>
        /// Fila de curadoria (issue #346): reportes ainda <c>pending</c>, mais antigos primeiro,
        /// limitados a <paramref name="limit"/>.
        /// </summary>
        Task<IReadOnlyList<FieldCorrectionReportSummary>> ListPendingReportsAsync(int limit, CancellationToken cancellationToken);

        /// <summary>Retorna uma linha de reporte por id, ou <c>null</c> se não existir.</summary>
        Task<FieldCorrectionReportSummary?> GetReportAsync(Guid reportId, CancellationToken cancellationToken);

        /// <summary>
        /// Transição de curadoria (issue #346): grava <paramref name="newStatus"/> +
        /// <paramref name="reviewedByUserId"/> + <c>ReviewedAtUtc</c> <b>somente</b> se o reporte
        /// estava <c>pending</c>. Retorna <c>false</c> se o reporte não existe ou já foi revisado
        /// (idempotente — uma segunda chamada não reverte nem re-transiciona).
        /// </summary>
        Task<bool> TransitionStatusAsync(Guid reportId, string newStatus, Guid reviewedByUserId, CancellationToken cancellationToken);
    }
}
