using LayoutParserApi.Models.Fiscal;
using LayoutParserApi.Services.Interfaces;

using Microsoft.Data.SqlClient;

namespace LayoutParserApi.Services.Database
{
    /// <summary>
    /// Implementação SQL de <see cref="IFieldCorrectionStore"/> — issue #345, ADR
    /// docs/architecture/adr-contrato-correcao-guiada-humano-2026-09-08.md. Banco DEDICADO do
    /// projeto (<c>IdentityDatabase:*</c>), mesmo padrão ADO.NET cru de <see cref="SqlMappingDraftStore"/>.
    /// </summary>
    public sealed class SqlFieldCorrectionStore : IFieldCorrectionStore
    {
        private readonly ILogger<SqlFieldCorrectionStore> _logger;
        private readonly string _connectionString;

        private static bool _schemaEnsured;
        private static readonly SemaphoreSlim _schemaLock = new(1, 1);

        public SqlFieldCorrectionStore(ILogger<SqlFieldCorrectionStore> logger, IConfiguration configuration)
        {
            _logger = logger;
            var server = configuration["IdentityDatabase:Server"];
            var database = configuration["IdentityDatabase:Database"];
            var userId = configuration["IdentityDatabase:UserId"];
            var password = configuration["IdentityDatabase:Password"];

            _connectionString = $"Server={server};Database={database};User Id={userId};Password={password};TrustServerCertificate=True;";
        }

        public async Task SaveContextAsync(FieldCorrectionContext context, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            // Idempotente por DocumentId (determinístico — ver DocumentIdCalculator): reenviar o
            // mesmo documento não sobrescreve a linha já gravada (preserva o primeiro contexto visto).
            using var command = new SqlCommand(
                @"IF NOT EXISTS (SELECT 1 FROM dbo.tbFieldCorrectionContext WHERE DocumentId = @DocumentId)
                  INSERT INTO dbo.tbFieldCorrectionContext
                      (DocumentId, MapperGuid, MapperName, LayoutGuid, LayoutName, InputXml, GroundTruthXml, CreatedAtUtc)
                  VALUES
                      (@DocumentId, @MapperGuid, @MapperName, @LayoutGuid, @LayoutName, @InputXml, @GroundTruthXml, SYSUTCDATETIME());",
                connection);
            command.Parameters.AddWithValue("@DocumentId", context.DocumentId);
            command.Parameters.AddWithValue("@MapperGuid", (object?)context.MapperGuid ?? DBNull.Value);
            command.Parameters.AddWithValue("@MapperName", (object?)context.MapperName ?? DBNull.Value);
            command.Parameters.AddWithValue("@LayoutGuid", context.LayoutGuid);
            command.Parameters.AddWithValue("@LayoutName", context.LayoutName);
            command.Parameters.AddWithValue("@InputXml", context.InputXml);
            command.Parameters.AddWithValue("@GroundTruthXml", (object?)context.GroundTruthXml ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task<FieldCorrectionContext?> GetContextAsync(string documentId, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            using var command = new SqlCommand(
                @"SELECT DocumentId, MapperGuid, MapperName, LayoutGuid, LayoutName, InputXml, GroundTruthXml, CreatedAtUtc
                  FROM dbo.tbFieldCorrectionContext WHERE DocumentId = @DocumentId;",
                connection);
            command.Parameters.AddWithValue("@DocumentId", documentId);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            return new FieldCorrectionContext(
                reader.GetString(reader.GetOrdinal("DocumentId")),
                reader.IsDBNull(reader.GetOrdinal("MapperGuid")) ? null : reader.GetString(reader.GetOrdinal("MapperGuid")),
                reader.IsDBNull(reader.GetOrdinal("MapperName")) ? null : reader.GetString(reader.GetOrdinal("MapperName")),
                reader.GetString(reader.GetOrdinal("LayoutGuid")),
                reader.GetString(reader.GetOrdinal("LayoutName")),
                reader.GetString(reader.GetOrdinal("InputXml")),
                reader.IsDBNull(reader.GetOrdinal("GroundTruthXml")) ? null : reader.GetString(reader.GetOrdinal("GroundTruthXml")),
                new DateTimeOffset(reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc")), TimeSpan.Zero));
        }

        public async Task<Guid> CreateReportAsync(FieldCorrectionReportInput input, Guid reportedByUserId, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            var reportId = Guid.NewGuid();
            using var command = new SqlCommand(
                @"INSERT INTO dbo.tbFieldCorrectionReport
                    (ReportId, DocumentId, CandidateId, FieldPath, ObservedValue, ExpectedValue, Justification, ReportedByUserId, Status, CreatedAtUtc)
                  VALUES
                    (@ReportId, @DocumentId, @CandidateId, @FieldPath, @ObservedValue, @ExpectedValue, @Justification, @ReportedByUserId, @Status, SYSUTCDATETIME());",
                connection);
            command.Parameters.AddWithValue("@ReportId", reportId);
            command.Parameters.AddWithValue("@DocumentId", input.DocumentId);
            command.Parameters.AddWithValue("@CandidateId", input.CandidateId);
            command.Parameters.AddWithValue("@FieldPath", input.FieldPath);
            command.Parameters.AddWithValue("@ObservedValue", (object?)input.ObservedValue ?? DBNull.Value);
            command.Parameters.AddWithValue("@ExpectedValue", (object?)input.ExpectedValue ?? DBNull.Value);
            command.Parameters.AddWithValue("@Justification", (object?)input.Justification ?? DBNull.Value);
            command.Parameters.AddWithValue("@ReportedByUserId", reportedByUserId);
            command.Parameters.AddWithValue("@Status", FieldCorrectionReportStatus.Pending);
            await command.ExecuteNonQueryAsync(cancellationToken);

            return reportId;
        }

        public async Task<IReadOnlyList<FieldCorrectionReportSummary>> ListPendingReportsAsync(int limit, CancellationToken cancellationToken)
        {
            if (limit <= 0)
                limit = 100;

            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            using var command = new SqlCommand(
                @"SELECT TOP (@Limit)
                      ReportId, DocumentId, CandidateId, FieldPath, ObservedValue, ExpectedValue,
                      Justification, ReportedByUserId, Status, CreatedAtUtc, ReviewedByUserId, ReviewedAtUtc
                  FROM dbo.tbFieldCorrectionReport
                  WHERE Status = @Pending
                  ORDER BY CreatedAtUtc ASC;",
                connection);
            command.Parameters.AddWithValue("@Limit", limit);
            command.Parameters.AddWithValue("@Pending", FieldCorrectionReportStatus.Pending);

            var result = new List<FieldCorrectionReportSummary>();
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(MapReport(reader));

            return result;
        }

        public async Task<FieldCorrectionReportSummary?> GetReportAsync(Guid reportId, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            using var command = new SqlCommand(
                @"SELECT ReportId, DocumentId, CandidateId, FieldPath, ObservedValue, ExpectedValue,
                         Justification, ReportedByUserId, Status, CreatedAtUtc, ReviewedByUserId, ReviewedAtUtc
                  FROM dbo.tbFieldCorrectionReport WHERE ReportId = @ReportId;",
                connection);
            command.Parameters.AddWithValue("@ReportId", reportId);

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? MapReport(reader) : null;
        }

        public async Task<bool> TransitionStatusAsync(Guid reportId, string newStatus, Guid reviewedByUserId, CancellationToken cancellationToken)
        {
            if (newStatus != FieldCorrectionReportStatus.ReviewedAccepted &&
                newStatus != FieldCorrectionReportStatus.ReviewedRejected)
                throw new ArgumentOutOfRangeException(nameof(newStatus), newStatus, "Só reviewed_accepted/reviewed_rejected são transições válidas.");

            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            // A cláusula Status = @Pending no WHERE é o que torna a transição idempotente e sem corrida:
            // um segundo review (ou dois revisores simultâneos) afeta 0 linhas.
            using var command = new SqlCommand(
                @"UPDATE dbo.tbFieldCorrectionReport
                     SET Status = @NewStatus,
                         ReviewedByUserId = @ReviewedBy,
                         ReviewedAtUtc = SYSUTCDATETIME()
                   WHERE ReportId = @ReportId AND Status = @Pending;",
                connection);
            command.Parameters.AddWithValue("@NewStatus", newStatus);
            command.Parameters.AddWithValue("@ReviewedBy", reviewedByUserId);
            command.Parameters.AddWithValue("@ReportId", reportId);
            command.Parameters.AddWithValue("@Pending", FieldCorrectionReportStatus.Pending);

            var affected = await command.ExecuteNonQueryAsync(cancellationToken);
            return affected == 1;
        }

        private static FieldCorrectionReportSummary MapReport(SqlDataReader reader) => new(
            reader.GetGuid(reader.GetOrdinal("ReportId")),
            reader.GetString(reader.GetOrdinal("DocumentId")),
            reader.GetString(reader.GetOrdinal("CandidateId")),
            reader.GetString(reader.GetOrdinal("FieldPath")),
            reader.IsDBNull(reader.GetOrdinal("ObservedValue")) ? null : reader.GetString(reader.GetOrdinal("ObservedValue")),
            reader.IsDBNull(reader.GetOrdinal("ExpectedValue")) ? null : reader.GetString(reader.GetOrdinal("ExpectedValue")),
            reader.IsDBNull(reader.GetOrdinal("Justification")) ? null : reader.GetString(reader.GetOrdinal("Justification")),
            reader.GetGuid(reader.GetOrdinal("ReportedByUserId")),
            reader.GetString(reader.GetOrdinal("Status")),
            new DateTimeOffset(reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc")), TimeSpan.Zero),
            reader.IsDBNull(reader.GetOrdinal("ReviewedByUserId")) ? null : reader.GetGuid(reader.GetOrdinal("ReviewedByUserId")),
            reader.IsDBNull(reader.GetOrdinal("ReviewedAtUtc")) ? null : new DateTimeOffset(reader.GetDateTime(reader.GetOrdinal("ReviewedAtUtc")), TimeSpan.Zero));

        // ✅ Sem FK entre tbFieldCorrectionReport.DocumentId e tbFieldCorrectionContext.DocumentId
        // (deliberado): o ADR já prevê um cron futuro de retenção/TTL sobre o contexto (§4, "fora de
        // escopo deste ADR") — uma FK travaria essa limpeza. O controller já garante a existência do
        // contexto (404 se ausente) antes de criar o reporte, então a integridade é aplicada em
        // aplicação, não em schema.
        public static readonly string SchemaDdl = @"
IF OBJECT_ID('dbo.tbFieldCorrectionContext', 'U') IS NULL
CREATE TABLE dbo.tbFieldCorrectionContext (
    DocumentId NVARCHAR(32) NOT NULL PRIMARY KEY,
    MapperGuid NVARCHAR(64) NULL,
    MapperName NVARCHAR(256) NULL,
    LayoutGuid NVARCHAR(64) NOT NULL,
    LayoutName NVARCHAR(256) NOT NULL,
    InputXml NVARCHAR(MAX) NOT NULL,
    GroundTruthXml NVARCHAR(MAX) NULL,
    CreatedAtUtc DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

IF OBJECT_ID('dbo.tbFieldCorrectionReport', 'U') IS NULL
CREATE TABLE dbo.tbFieldCorrectionReport (
    ReportId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    DocumentId NVARCHAR(32) NOT NULL,
    CandidateId NVARCHAR(128) NOT NULL,
    FieldPath NVARCHAR(1024) NOT NULL,
    ObservedValue NVARCHAR(MAX) NULL,
    ExpectedValue NVARCHAR(MAX) NULL,
    Justification NVARCHAR(2000) NULL,
    ReportedByUserId UNIQUEIDENTIFIER NOT NULL,
    Status NVARCHAR(24) NOT NULL DEFAULT 'pending',
    CreatedAtUtc DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    ReviewedByUserId UNIQUEIDENTIFIER NULL,
    ReviewedAtUtc DATETIME2 NULL
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_tbFieldCorrectionReport_DocumentId' AND object_id = OBJECT_ID('dbo.tbFieldCorrectionReport'))
CREATE INDEX IX_tbFieldCorrectionReport_DocumentId ON dbo.tbFieldCorrectionReport(DocumentId);";

        internal static async Task EnsureSchemaAsync(SqlConnection connection, CancellationToken cancellationToken)
        {
            if (_schemaEnsured)
                return;

            await _schemaLock.WaitAsync(cancellationToken);
            try
            {
                if (_schemaEnsured)
                    return;

                using var command = new SqlCommand(SchemaDdl, connection);
                await command.ExecuteNonQueryAsync(cancellationToken);
                _schemaEnsured = true;
            }
            finally
            {
                _schemaLock.Release();
            }
        }
    }
}
