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
                         r.CreatedAt, r.RowVersion, r.Environment, r.ApprovedByUserId, r.ApprovedAt, r.ApprovalJustification,
                         r.PublishedByUserId, r.PublishedAt, r.PreviousPublishedReleaseId
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

        public async Task<(IReadOnlyList<MappingReleaseDetail> Items, int TotalCount)> ListByWorkspaceAsync(
            Guid workspaceId, int page, int pageSize, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            var items = new List<MappingReleaseDetail>();
            var totalCount = 0;

            // COUNT(*) OVER() traz o total na mesma ida ao banco — evita um segundo round-trip só
            // para paginação. Isolamento por WorkspaceId direto na cláusula WHERE (nunca em memória).
            using var command = new SqlCommand(
                @"SELECT ReleaseId, WorkspaceId, DraftId, Engine, ArtifactsJson, SourceRuleIdsJson,
                         CompileDiagnosticsJson, RulesSnapshotHash, TestRunSummaryJson, Status, CorrelationId,
                         CreatedAt, RowVersion, Environment, ApprovedByUserId, ApprovedAt, ApprovalJustification,
                         PublishedByUserId, PublishedAt, PreviousPublishedReleaseId,
                         COUNT(*) OVER() AS TotalCount
                  FROM dbo.tbMappingRelease
                  WHERE WorkspaceId = @WorkspaceId
                  ORDER BY CreatedAt DESC
                  OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;",
                connection);
            command.Parameters.AddWithValue("@WorkspaceId", workspaceId);
            command.Parameters.AddWithValue("@Offset", (page - 1) * pageSize);
            command.Parameters.AddWithValue("@PageSize", pageSize);

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(ReadReleaseDetail(reader));
                totalCount = reader.GetInt32(reader.GetOrdinal("TotalCount"));
            }

            return (items, totalCount);
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
                         CreatedAt, RowVersion, Environment, ApprovedByUserId, ApprovedAt, ApprovalJustification,
                         PublishedByUserId, PublishedAt, PreviousPublishedReleaseId
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
                Convert.ToBase64String(rowVersion),
                reader.GetString(reader.GetOrdinal("Environment")),
                reader.IsDBNull(reader.GetOrdinal("ApprovedByUserId")) ? null : reader.GetGuid(reader.GetOrdinal("ApprovedByUserId")),
                reader.IsDBNull(reader.GetOrdinal("ApprovedAt")) ? null : new DateTimeOffset(reader.GetDateTime(reader.GetOrdinal("ApprovedAt")), TimeSpan.Zero),
                reader.IsDBNull(reader.GetOrdinal("ApprovalJustification")) ? null : reader.GetString(reader.GetOrdinal("ApprovalJustification")),
                reader.IsDBNull(reader.GetOrdinal("PublishedByUserId")) ? null : reader.GetGuid(reader.GetOrdinal("PublishedByUserId")),
                reader.IsDBNull(reader.GetOrdinal("PublishedAt")) ? null : new DateTimeOffset(reader.GetDateTime(reader.GetOrdinal("PublishedAt")), TimeSpan.Zero),
                reader.IsDBNull(reader.GetOrdinal("PreviousPublishedReleaseId")) ? null : reader.GetGuid(reader.GetOrdinal("PreviousPublishedReleaseId")));
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
CREATE INDEX IX_tbMappingRelease_DraftId_Hash ON dbo.tbMappingRelease(DraftId, RulesSnapshotHash);

-- Slice 7 (issue #94): governança/publicação — colunas adicionadas de forma idempotente porque
-- dbo.tbMappingRelease já existe em bases criadas pelo Slice 5.
IF COL_LENGTH('dbo.tbMappingRelease', 'Environment') IS NULL
ALTER TABLE dbo.tbMappingRelease ADD Environment NVARCHAR(16) NOT NULL CONSTRAINT DF_tbMappingRelease_Environment DEFAULT 'development';

IF COL_LENGTH('dbo.tbMappingRelease', 'ApprovedByUserId') IS NULL
ALTER TABLE dbo.tbMappingRelease ADD ApprovedByUserId UNIQUEIDENTIFIER NULL;

IF COL_LENGTH('dbo.tbMappingRelease', 'ApprovedAt') IS NULL
ALTER TABLE dbo.tbMappingRelease ADD ApprovedAt DATETIME2 NULL;

IF COL_LENGTH('dbo.tbMappingRelease', 'ApprovalJustification') IS NULL
ALTER TABLE dbo.tbMappingRelease ADD ApprovalJustification NVARCHAR(1024) NULL;

IF COL_LENGTH('dbo.tbMappingRelease', 'PublishedByUserId') IS NULL
ALTER TABLE dbo.tbMappingRelease ADD PublishedByUserId UNIQUEIDENTIFIER NULL;

IF COL_LENGTH('dbo.tbMappingRelease', 'PublishedAt') IS NULL
ALTER TABLE dbo.tbMappingRelease ADD PublishedAt DATETIME2 NULL;

IF COL_LENGTH('dbo.tbMappingRelease', 'PreviousPublishedReleaseId') IS NULL
ALTER TABLE dbo.tbMappingRelease ADD PreviousPublishedReleaseId UNIQUEIDENTIFIER NULL;

IF OBJECT_ID('dbo.tbMappingTransition', 'U') IS NULL
CREATE TABLE dbo.tbMappingTransition (
    TransitionId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    ReleaseId UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.tbMappingRelease(ReleaseId),
    FromStatus NVARCHAR(16) NOT NULL,
    ToStatus NVARCHAR(16) NOT NULL,
    ActorUserId UNIQUEIDENTIFIER NOT NULL,
    OccurredAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    Justification NVARCHAR(1024) NULL,
    ChecksSnapshot NVARCHAR(MAX) NULL
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_tbMappingTransition_ReleaseId' AND object_id = OBJECT_ID('dbo.tbMappingTransition'))
CREATE INDEX IX_tbMappingTransition_ReleaseId ON dbo.tbMappingTransition(ReleaseId);";

                using var command = new SqlCommand(ddl, connection);
                await command.ExecuteNonQueryAsync(cancellationToken);
                _schemaEnsured = true;
            }
            finally
            {
                _schemaLock.Release();
            }
        }

        public async Task<MappingReleaseDetail> ApproveAsync(Guid releaseId, Guid actorUserId, string justification, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            using var tx = connection.BeginTransaction();
            try
            {
                var current = await GetReleaseForUpdateAsync(connection, tx, releaseId, cancellationToken)
                    ?? throw new InvalidOperationException($"Release {releaseId} não encontrada.");

                // Bloqueia entrada em revisão a partir de qualquer status que não seja test_passed —
                // cobre explicitamente test_failed (design §1) e evita reaprovar release já aprovada.
                if (current.Status != MappingReleaseStatus.TestPassed)
                    throw new InvalidOperationException($"Release {releaseId} está em \"{current.Status}\"; aprovação exige \"{MappingReleaseStatus.TestPassed}\".");

                await InsertTransitionAsync(connection, tx, releaseId, MappingReleaseStatus.TestPassed, MappingReleaseStatus.InReview, actorUserId, justification, null, cancellationToken);
                await InsertTransitionAsync(connection, tx, releaseId, MappingReleaseStatus.InReview, MappingReleaseStatus.Approved, actorUserId, justification, null, cancellationToken);

                using (var update = new SqlCommand(
                    @"UPDATE dbo.tbMappingRelease
                      SET Status = @Status, ApprovedByUserId = @ActorUserId, ApprovedAt = SYSUTCDATETIME(), ApprovalJustification = @Justification
                      WHERE ReleaseId = @ReleaseId;",
                    connection, tx))
                {
                    update.Parameters.AddWithValue("@Status", MappingReleaseStatus.Approved);
                    update.Parameters.AddWithValue("@ActorUserId", actorUserId);
                    update.Parameters.AddWithValue("@Justification", (object?)justification ?? DBNull.Value);
                    update.Parameters.AddWithValue("@ReleaseId", releaseId);
                    await update.ExecuteNonQueryAsync(cancellationToken);
                }

                await tx.CommitAsync(cancellationToken);
            }
            catch
            {
                await tx.RollbackAsync(cancellationToken);
                throw;
            }

            return await GetReleaseAsync(connection, releaseId, cancellationToken)
                ?? throw new InvalidOperationException("Falha ao reler a release após aprovação.");
        }

        public async Task<MappingReleaseDetail> PublishAsync(Guid releaseId, Guid actorUserId, string environment, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            using var tx = connection.BeginTransaction();
            try
            {
                var current = await GetReleaseForUpdateAsync(connection, tx, releaseId, cancellationToken)
                    ?? throw new InvalidOperationException($"Release {releaseId} não encontrada.");

                if (current.Status != MappingReleaseStatus.Approved)
                    throw new InvalidOperationException($"Release {releaseId} está em \"{current.Status}\"; publicação exige \"{MappingReleaseStatus.Approved}\".");

                // Release publicada hoje para o mesmo DraftId (se houver) vira Deprecated; a nova
                // release guarda o ponteiro pra ela (design §3, base do rollback).
                Guid? previousPublishedReleaseId = null;
                using (var findPublished = new SqlCommand(
                    @"SELECT TOP 1 ReleaseId FROM dbo.tbMappingRelease
                      WHERE DraftId = @DraftId AND Status = @Published AND ReleaseId <> @ReleaseId;",
                    connection, tx))
                {
                    findPublished.Parameters.AddWithValue("@DraftId", current.DraftId);
                    findPublished.Parameters.AddWithValue("@Published", MappingReleaseStatus.Published);
                    findPublished.Parameters.AddWithValue("@ReleaseId", releaseId);
                    var found = await findPublished.ExecuteScalarAsync(cancellationToken);
                    if (found is Guid previousId)
                        previousPublishedReleaseId = previousId;
                }

                if (previousPublishedReleaseId is Guid deprecateId)
                {
                    await InsertTransitionAsync(connection, tx, deprecateId, MappingReleaseStatus.Published, MappingReleaseStatus.Deprecated, actorUserId, "Substituída pela publicação de outra release.", null, cancellationToken);
                    using var deprecate = new SqlCommand(
                        "UPDATE dbo.tbMappingRelease SET Status = @Status WHERE ReleaseId = @ReleaseId;", connection, tx);
                    deprecate.Parameters.AddWithValue("@Status", MappingReleaseStatus.Deprecated);
                    deprecate.Parameters.AddWithValue("@ReleaseId", deprecateId);
                    await deprecate.ExecuteNonQueryAsync(cancellationToken);
                }

                await InsertTransitionAsync(connection, tx, releaseId, MappingReleaseStatus.Approved, MappingReleaseStatus.Published, actorUserId, $"Publicado em \"{environment}\".", null, cancellationToken);

                using (var update = new SqlCommand(
                    @"UPDATE dbo.tbMappingRelease
                      SET Status = @Status, Environment = @Environment, PublishedByUserId = @ActorUserId,
                          PublishedAt = SYSUTCDATETIME(), PreviousPublishedReleaseId = @PreviousPublishedReleaseId
                      WHERE ReleaseId = @ReleaseId;",
                    connection, tx))
                {
                    update.Parameters.AddWithValue("@Status", MappingReleaseStatus.Published);
                    update.Parameters.AddWithValue("@Environment", environment);
                    update.Parameters.AddWithValue("@ActorUserId", actorUserId);
                    update.Parameters.AddWithValue("@PreviousPublishedReleaseId", (object?)previousPublishedReleaseId ?? DBNull.Value);
                    update.Parameters.AddWithValue("@ReleaseId", releaseId);
                    await update.ExecuteNonQueryAsync(cancellationToken);
                }

                await tx.CommitAsync(cancellationToken);
            }
            catch
            {
                await tx.RollbackAsync(cancellationToken);
                throw;
            }

            return await GetReleaseAsync(connection, releaseId, cancellationToken)
                ?? throw new InvalidOperationException("Falha ao reler a release após publicação.");
        }

        public async Task<MappingReleaseDetail> RollbackAsync(Guid releaseId, Guid actorUserId, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            using var tx = connection.BeginTransaction();
            try
            {
                var current = await GetReleaseForUpdateAsync(connection, tx, releaseId, cancellationToken)
                    ?? throw new InvalidOperationException($"Release {releaseId} não encontrada.");

                // Idempotente (design §3): já não está published (rollback anterior já rodou, ou
                // nunca chegou a publicar) — no-op, não gera transição nova nem erro.
                if (current.Status != MappingReleaseStatus.Published)
                {
                    await tx.CommitAsync(cancellationToken);
                    return await GetReleaseAsync(connection, releaseId, cancellationToken)
                        ?? throw new InvalidOperationException("Falha ao reler a release no rollback idempotente.");
                }

                if (current.PreviousPublishedReleaseId is not Guid previousId)
                    throw new InvalidOperationException($"Release {releaseId} não tem release publicada anterior para reverter.");

                await InsertTransitionAsync(connection, tx, releaseId, MappingReleaseStatus.Published, MappingReleaseStatus.Deprecated, actorUserId, "Rollback: revertida em favor da release publicada anterior.", null, cancellationToken);
                using (var deprecate = new SqlCommand(
                    "UPDATE dbo.tbMappingRelease SET Status = @Status WHERE ReleaseId = @ReleaseId;", connection, tx))
                {
                    deprecate.Parameters.AddWithValue("@Status", MappingReleaseStatus.Deprecated);
                    deprecate.Parameters.AddWithValue("@ReleaseId", releaseId);
                    await deprecate.ExecuteNonQueryAsync(cancellationToken);
                }

                await InsertTransitionAsync(connection, tx, previousId, MappingReleaseStatus.Deprecated, MappingReleaseStatus.Published, actorUserId, "Rollback: promovida de volta a published.", null, cancellationToken);
                using (var restore = new SqlCommand(
                    @"UPDATE dbo.tbMappingRelease SET Status = @Status, PublishedByUserId = @ActorUserId, PublishedAt = SYSUTCDATETIME()
                      WHERE ReleaseId = @ReleaseId;", connection, tx))
                {
                    restore.Parameters.AddWithValue("@Status", MappingReleaseStatus.Published);
                    restore.Parameters.AddWithValue("@ActorUserId", actorUserId);
                    restore.Parameters.AddWithValue("@ReleaseId", previousId);
                    await restore.ExecuteNonQueryAsync(cancellationToken);
                }

                await tx.CommitAsync(cancellationToken);
            }
            catch
            {
                await tx.RollbackAsync(cancellationToken);
                throw;
            }

            return await GetReleaseAsync(connection, releaseId, cancellationToken)
                ?? throw new InvalidOperationException("Falha ao reler a release após rollback.");
        }

        private static async Task InsertTransitionAsync(
            SqlConnection connection, SqlTransaction tx, Guid releaseId, string fromStatus, string toStatus,
            Guid actorUserId, string? justification, string? checksSnapshot, CancellationToken cancellationToken)
        {
            using var insert = new SqlCommand(
                @"INSERT INTO dbo.tbMappingTransition (TransitionId, ReleaseId, FromStatus, ToStatus, ActorUserId, OccurredAt, Justification, ChecksSnapshot)
                  VALUES (@TransitionId, @ReleaseId, @FromStatus, @ToStatus, @ActorUserId, SYSUTCDATETIME(), @Justification, @ChecksSnapshot);",
                connection, tx);
            insert.Parameters.AddWithValue("@TransitionId", Guid.NewGuid());
            insert.Parameters.AddWithValue("@ReleaseId", releaseId);
            insert.Parameters.AddWithValue("@FromStatus", fromStatus);
            insert.Parameters.AddWithValue("@ToStatus", toStatus);
            insert.Parameters.AddWithValue("@ActorUserId", actorUserId);
            insert.Parameters.AddWithValue("@Justification", (object?)justification ?? DBNull.Value);
            insert.Parameters.AddWithValue("@ChecksSnapshot", (object?)checksSnapshot ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        /// <summary>Lê status+DraftId+PreviousPublishedReleaseId dentro da MESMA transação (lock implícito de leitura), sem filtro de membership — a checagem de acesso já aconteceu na camada de RBAC do controller.</summary>
        private static async Task<(string Status, Guid DraftId, Guid? PreviousPublishedReleaseId)?> GetReleaseForUpdateAsync(
            SqlConnection connection, SqlTransaction tx, Guid releaseId, CancellationToken cancellationToken)
        {
            using var command = new SqlCommand(
                "SELECT Status, DraftId, PreviousPublishedReleaseId FROM dbo.tbMappingRelease WITH (UPDLOCK, ROWLOCK) WHERE ReleaseId = @ReleaseId;",
                connection, tx);
            command.Parameters.AddWithValue("@ReleaseId", releaseId);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            var status = reader.GetString(reader.GetOrdinal("Status"));
            var draftId = reader.GetGuid(reader.GetOrdinal("DraftId"));
            var previousOrdinal = reader.GetOrdinal("PreviousPublishedReleaseId");
            Guid? previous = reader.IsDBNull(previousOrdinal) ? null : reader.GetGuid(previousOrdinal);
            return (status, draftId, previous);
        }
    }
}
