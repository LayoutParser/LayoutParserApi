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
    }
}
