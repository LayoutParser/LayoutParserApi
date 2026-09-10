namespace LayoutParserApi.Models.Fiscal
{
    /// <summary>
    /// Contexto mínimo de um documento processado por <c>execute-candidates</c>, persistido
    /// best-effort para permitir um reporte de correção humana posterior (ADR
    /// docs/architecture/adr-contrato-correcao-guiada-humano-2026-09-08.md §4/§7, issue #345).
    /// <c>MapperGuid</c>/<c>MapperName</c>/<c>GroundTruthXml</c> são nulos quando a request não
    /// produziu candidato sysmiddle (ex.: entrada XML, ou layout sem mapper cadastrado) — o
    /// reporte de correção ainda é possível, só sem o gabarito sysmiddle ao lado.
    /// </summary>
    public sealed record FieldCorrectionContext(
        string DocumentId,
        string? MapperGuid,
        string? MapperName,
        string LayoutGuid,
        string LayoutName,
        string InputXml,
        string? GroundTruthXml,
        DateTimeOffset CreatedAtUtc);

    /// <summary>
    /// Dados de entrada para <see cref="LayoutParserApi.Services.Interfaces.IFieldCorrectionStore.CreateReportAsync"/> —
    /// já validados pelo controller, autoria (<c>ReportedByUserId</c>) resolvida separadamente via
    /// <c>ICurrentUser</c>.
    /// </summary>
    public sealed record FieldCorrectionReportInput(
        string DocumentId,
        string CandidateId,
        string FieldPath,
        string ObservedValue,
        string ExpectedValue,
        string? Justification);

    /// <summary>
    /// Projeção de leitura de uma linha de <c>tbFieldCorrectionReport</c> — usada pela fila de
    /// curadoria (<c>GET field-correction/pending</c>) e ao montar o exemplo de treino incremental
    /// quando o reporte é aceito (issue #346).
    /// </summary>
    public sealed record FieldCorrectionReportSummary(
        Guid ReportId,
        string DocumentId,
        string CandidateId,
        string FieldPath,
        string? ObservedValue,
        string? ExpectedValue,
        string? Justification,
        Guid ReportedByUserId,
        string Status,
        DateTimeOffset CreatedAtUtc,
        Guid? ReviewedByUserId,
        DateTimeOffset? ReviewedAtUtc);

    /// <summary>
    /// Status do ciclo de vida de um reporte (ADR §6): nunca vira dado de treino direto —
    /// exige curadoria humana (issue #346). <c>Pending → ReviewedAccepted | ReviewedRejected</c>,
    /// transição única e irreversível; só <c>ReviewedAccepted</c> gera linha no dataset incremental.
    /// </summary>
    public static class FieldCorrectionReportStatus
    {
        public const string Pending = "pending";
        public const string ReviewedAccepted = "reviewed_accepted";
        public const string ReviewedRejected = "reviewed_rejected";
    }
}
