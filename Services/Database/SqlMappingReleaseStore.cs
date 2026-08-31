using System.Text.Json;

using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Services.Interfaces;

using Microsoft.Data.SqlClient;

namespace LayoutParserApi.Services.Database
{
    /// <summary>
    /// Implementação SQL de <see cref="IMappingReleaseStore"/> — Slice 5 (issue #231). Mesmo banco
    /// <c>ConnectUS_Macgyver</c> e mesmo padrão ADO.NET cru de <see cref="SqlMappingDraftStore"/> (DDL
    /// idempotente por processo, JSON em <c>NVARCHAR(MAX)</c> para as coleções).
    /// </summary>
    public sealed class SqlMappingReleaseStore : IMappingReleaseStore
    {
        private readonly ILogger<SqlMappingReleaseStore> _logger;
        private readonly string _connectionString;

        private static bool _schemaEnsured;
        private static readonly SemaphoreSlim _schemaLock = new(1, 1);

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public SqlMappingReleaseStore(ILogger<SqlMappingReleaseStore> logger, IConfiguration configuration)
        {
            _logger = logger;
            var server = configuration["Database:Server"];
            var database = configuration["Database:Database"];
            var userId = configuration["Database:UserId"];
            var password = configuration["Database:Password"];

            _connectionString = $"Server={server};Database={database};User Id={userId};Password={password};TrustServerCertificate=True;";
        }

        public async Task<MappingReleaseDetail> CreateOrGetCompiledReleaseAsync(
            Guid workspaceId,
            Guid draftId,
            string engine,
            string rulesSnapshotHash,
            IReadOnlyList<Guid> sourceRuleIds,
            IReadOnlyList<MappingReleaseArtifact> artifacts,
            IReadOnlyList<MappingReleaseCompileDiagnostic> compileDiagnostics,
            string correlationId,
            Guid jobId,
            CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            // Idempotência (design §2): mesmo DraftId + mesmo hash do snapshot de regras já compilado
            // devolve a release existente, não duplica.
            using (var existing = new SqlCommand(
                @"SELECT TOP 1 ReleaseId FROM dbo.tbMappingRelease
                  WHERE DraftId = @DraftId AND RulesSnapshotHash = @RulesSnapshotHash
                  ORDER BY CreatedAt DESC;",
                connection))
            {
                existing.Parameters.AddWithValue("@DraftId", draftId);
                existing.Parameters.AddWithValue("@RulesSnapshotHash", rulesSnapshotHash);
                var existingId = await existing.ExecuteScalarAsync(cancellationToken);
                if (existingId is Guid releaseId)
                {
                    var detail = await GetReleaseAsync(connection, releaseId, cancellationToken);
                    if (detail != null)
                    {
                        _logger.LogInformation("Compilação idempotente: reusando release {ReleaseId} para o draft {DraftId}.", releaseId, draftId);
                        return detail;
                    }
                }
            }

            var newReleaseId = Guid.NewGuid();
            using (var insert = new SqlCommand(
                @"INSERT INTO dbo.tbMappingRelease
                    (ReleaseId, WorkspaceId, DraftId, Engine, ArtifactsJson, SourceRuleIdsJson, CompileDiagnosticsJson,
                     RulesSnapshotHash, TestRunSummaryJson, Status, CorrelationId, CreatedByJobId, CreatedAt)
                  VALUES
                    (@ReleaseId, @WorkspaceId, @DraftId, @Engine, @ArtifactsJson, @SourceRuleIdsJson, @CompileDiagnosticsJson,
                     @RulesSnapshotHash, NULL, @Status, @CorrelationId, @CreatedByJobId, SYSUTCDATETIME());",
                connection))
            {
                insert.Parameters.AddWithValue("@ReleaseId", newReleaseId);
                insert.Parameters.AddWithValue("@WorkspaceId", workspaceId);
                insert.Parameters.AddWithValue("@DraftId", draftId);
                insert.Parameters.AddWithValue("@Engine", engine);
                insert.Parameters.AddWithValue("@ArtifactsJson", JsonSerializer.Serialize(artifacts, JsonOptions));
                insert.Parameters.AddWithValue("@SourceRuleIdsJson", JsonSerializer.Serialize(sourceRuleIds, JsonOptions));
                insert.Parameters.AddWithValue("@CompileDiagnosticsJson", JsonSerializer.Serialize(compileDiagnostics, JsonOptions));
                insert.Parameters.AddWithValue("@RulesSnapshotHash", rulesSnapshotHash);
                insert.Parameters.AddWithValue("@Status", MappingReleaseStatus.DraftCompiled);
                insert.Parameters.AddWithValue("@CorrelationId", correlationId);
                insert.Parameters.AddWithValue("@CreatedByJobId", jobId);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            var created = await GetReleaseAsync(connection, newReleaseId, cancellationToken);
            return created ?? throw new InvalidOperationException("Falha ao ler a release recém-criada.");
        }

        public async Task<MappingReleaseDetail?> GetReleaseIfMemberAsync(Guid releaseId, Guid userId, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            using var command = new SqlCommand(
                @"SELECT r.ReleaseId, r.WorkspaceId, r.DraftId, r.Engine, r.ArtifactsJson, r.SourceRuleIdsJson,
                         r.CompileDiagnosticsJson, r.RulesSnapshotHash, r.TestRunSummaryJson, r.Status, r.CorrelationId,
                         r.CreatedAt, r.RowVersion
                  FROM dbo.tbMappingRelease r
                  JOIN dbo.tbWorkspaceMembership m ON m.WorkspaceId = r.WorkspaceId AND m.UserId = @UserId
                  WHERE r.ReleaseId = @ReleaseId;",
                connection);
            command.Parameters.AddWithValue("@ReleaseId", releaseId);
            command.Parameters.AddWithValue("@UserId", userId);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null; // Não existe OU não é seu — indistinguível, mesmo padrão dos Slices anteriores.

            return ReadReleaseDetail(reader);
        }

        public async Task<MappingReleaseDetail?> ApplyTestRunResultAsync(Guid releaseId, MappingTestRunSummary summary, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            var newStatus = summary.RequiredGatesPassed ? MappingReleaseStatus.TestPassed : MappingReleaseStatus.TestFailed;

            using (var update = new SqlCommand(
                @"UPDATE dbo.tbMappingRelease
                  SET TestRunSummaryJson = @TestRunSummaryJson, Status = @Status
                  WHERE ReleaseId = @ReleaseId;",
                connection))
            {
                update.Parameters.AddWithValue("@TestRunSummaryJson", JsonSerializer.Serialize(summary, JsonOptions));
                update.Parameters.AddWithValue("@Status", newStatus);
                update.Parameters.AddWithValue("@ReleaseId", releaseId);
                var rows = await update.ExecuteNonQueryAsync(cancellationToken);
                if (rows == 0)
                    return null;
            }

            return await GetReleaseAsync(connection, releaseId, cancellationToken);
        }

        private static async Task<MappingReleaseDetail?> GetReleaseAsync(SqlConnection connection, Guid releaseId, CancellationToken cancellationToken)
        {
            using var command = new SqlCommand(
                @"SELECT ReleaseId, WorkspaceId, DraftId, Engine, ArtifactsJson, SourceRuleIdsJson,
                         CompileDiagnosticsJson, RulesSnapshotHash, TestRunSummaryJson, Status, CorrelationId,
                         CreatedAt, RowVersion
                  FROM dbo.tbMappingRelease WHERE ReleaseId = @ReleaseId;",
                connection);
            command.Parameters.AddWithValue("@ReleaseId", releaseId);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            return ReadReleaseDetail(reader);
        }

        private static MappingReleaseDetail ReadReleaseDetail(SqlDataReader reader)
        {
            var rowVersion = (byte[])reader["RowVersion"];
            var testRunSummaryOrdinal = reader.GetOrdinal("TestRunSummaryJson");
            var testRunSummary = reader.IsDBNull(testRunSummaryOrdinal)
                ? null
                : JsonSerializer.Deserialize<MappingTestRunSummary>(reader.GetString(testRunSummaryOrdinal), JsonOptions);

            return new MappingReleaseDetail(
                reader.GetGuid(reader.GetOrdinal("ReleaseId")),
                reader.GetGuid(reader.GetOrdinal("WorkspaceId")),
                reader.GetGuid(reader.GetOrdinal("DraftId")),
                reader.GetString(reader.GetOrdinal("Engine")),
                JsonSerializer.Deserialize<List<MappingReleaseArtifact>>(reader.GetString(reader.GetOrdinal("ArtifactsJson")), JsonOptions) ?? new(),
                JsonSerializer.Deserialize<List<Guid>>(reader.GetString(reader.GetOrdinal("SourceRuleIdsJson")), JsonOptions) ?? new(),
                JsonSerializer.Deserialize<List<MappingReleaseCompileDiagnostic>>(reader.GetString(reader.GetOrdinal("CompileDiagnosticsJson")), JsonOptions) ?? new(),
                reader.GetString(reader.GetOrdinal("RulesSnapshotHash")),
                testRunSummary,
                reader.GetString(reader.GetOrdinal("Status")),
                reader.GetString(reader.GetOrdinal("CorrelationId")),
                new DateTimeOffset(reader.GetDateTime(reader.GetOrdinal("CreatedAt")), TimeSpan.Zero),
                Convert.ToBase64String(rowVersion));
        }

        private static async Task EnsureSchemaAsync(SqlConnection connection, CancellationToken cancellationToken)
        {
            if (_schemaEnsured)
                return;

            await _schemaLock.WaitAsync(cancellationToken);
            try
            {
                if (_schemaEnsured)
                    return;

                const string ddl = @"
IF OBJECT_ID('dbo.tbMappingRelease', 'U') IS NULL
CREATE TABLE dbo.tbMappingRelease (
    ReleaseId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    WorkspaceId UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.tbFiscalWorkspace(WorkspaceId),
    DraftId UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.tbMappingDraft(DraftId),
    Engine NVARCHAR(16) NOT NULL,
    ArtifactsJson NVARCHAR(MAX) NOT NULL,
    SourceRuleIdsJson NVARCHAR(MAX) NOT NULL,
    CompileDiagnosticsJson NVARCHAR(MAX) NOT NULL,
    RulesSnapshotHash NVARCHAR(64) NOT NULL,
    TestRunSummaryJson NVARCHAR(MAX) NULL,
    Status NVARCHAR(16) NOT NULL,
    CorrelationId NVARCHAR(64) NOT NULL,
    CreatedByJobId UNIQUEIDENTIFIER NOT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    RowVersion ROWVERSION NOT NULL
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_tbMappingRelease_DraftId' AND object_id = OBJECT_ID('dbo.tbMappingRelease'))
CREATE INDEX IX_tbMappingRelease_DraftId ON dbo.tbMappingRelease(DraftId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_tbMappingRelease_DraftId_Hash' AND object_id = OBJECT_ID('dbo.tbMappingRelease'))
CREATE INDEX IX_tbMappingRelease_DraftId_Hash ON dbo.tbMappingRelease(DraftId, RulesSnapshotHash);";

                using var command = new SqlCommand(ddl, connection);
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
