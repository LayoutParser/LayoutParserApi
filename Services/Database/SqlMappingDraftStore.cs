using System.Text.Json;

using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Services.Interfaces;

using Microsoft.Data.SqlClient;

namespace LayoutParserApi.Services.Database
{
    /// <summary>
    /// Implementação SQL de <see cref="IMappingDraftStore"/> — Slice 3 (issue #230). Mesmo banco
    /// <c>ConnectUS_Macgyver</c> e mesmo padrão ADO.NET cru de <see cref="SqlFiscalPackageStore"/>
    /// (DDL idempotente por processo). Só LÊ as tabelas do Slice 2 (revisão/artefato) — nunca escreve
    /// nelas, respeitando "não tocar código dos Slices 1/2 além de reaproveitar".
    /// </summary>
    public sealed class SqlMappingDraftStore : IMappingDraftStore
    {
        private readonly ILogger<SqlMappingDraftStore> _logger;
        private readonly string _connectionString;

        private static bool _schemaEnsured;
        private static readonly SemaphoreSlim _schemaLock = new(1, 1);

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public SqlMappingDraftStore(ILogger<SqlMappingDraftStore> logger, IConfiguration configuration)
        {
            _logger = logger;
            var server = configuration["Database:Server"];
            var database = configuration["Database:Database"];
            var userId = configuration["Database:UserId"];
            var password = configuration["Database:Password"];

            _connectionString = $"Server={server};Database={database};User Id={userId};Password={password};TrustServerCertificate=True;";
        }

        public async Task<bool> RevisionBelongsToPackageAsync(Guid packageId, Guid revisionId, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            using var command = new SqlCommand(
                "SELECT 1 FROM dbo.tbFiscalMappingPackageRevision WHERE RevisionId = @RevisionId AND PackageId = @PackageId;",
                connection);
            command.Parameters.AddWithValue("@RevisionId", revisionId);
            command.Parameters.AddWithValue("@PackageId", packageId);
            return await command.ExecuteScalarAsync(cancellationToken) != null;
        }

        public async Task<IReadOnlyList<ArtifactFileRef>> GetArtifactFilesForRevisionAsync(Guid revisionId, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            var result = new List<ArtifactFileRef>();
            using var command = new SqlCommand(
                "SELECT ArtifactId, Kind, StoragePath, OriginalFileName FROM dbo.tbPackageArtifact WHERE RevisionId = @RevisionId;",
                connection);
            command.Parameters.AddWithValue("@RevisionId", revisionId);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(new ArtifactFileRef(
                    reader.GetGuid(reader.GetOrdinal("ArtifactId")),
                    reader.GetString(reader.GetOrdinal("Kind")),
                    reader.GetString(reader.GetOrdinal("StoragePath")),
                    reader.GetString(reader.GetOrdinal("OriginalFileName"))));
            }
            return result;
        }

        public async Task<MappingDraftDetail> CreateDraftAsync(
            Guid workspaceId, Guid packageId, Guid revisionId, Guid createdByUserId, string engine, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            var draftId = Guid.NewGuid();
            var createdAt = DateTimeOffset.UtcNow;

            using var command = new SqlCommand(
                @"INSERT INTO dbo.tbMappingDraft (DraftId, WorkspaceId, PackageId, RevisionId, Engine, CreatedByUserId, CreatedAt)
                  VALUES (@DraftId, @WorkspaceId, @PackageId, @RevisionId, @Engine, @CreatedByUserId, SYSUTCDATETIME());",
                connection);
            command.Parameters.AddWithValue("@DraftId", draftId);
            command.Parameters.AddWithValue("@WorkspaceId", workspaceId);
            command.Parameters.AddWithValue("@PackageId", packageId);
            command.Parameters.AddWithValue("@RevisionId", revisionId);
            command.Parameters.AddWithValue("@Engine", engine);
            command.Parameters.AddWithValue("@CreatedByUserId", createdByUserId);
            await command.ExecuteNonQueryAsync(cancellationToken);

            return new MappingDraftDetail(draftId, workspaceId, packageId, revisionId, engine, createdAt, Array.Empty<MappingDraftRuleDetail>());
        }

        public async Task<MappingDraftDetail?> GetDraftIfMemberAsync(Guid draftId, Guid userId, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            Guid workspaceId, packageId, revisionId;
            string engine;
            DateTimeOffset createdAt;

            using (var selectDraft = new SqlCommand(
                @"SELECT d.WorkspaceId, d.PackageId, d.RevisionId, d.Engine, d.CreatedAt
                  FROM dbo.tbMappingDraft d
                  JOIN dbo.tbWorkspaceMembership m ON m.WorkspaceId = d.WorkspaceId AND m.UserId = @UserId
                  WHERE d.DraftId = @DraftId;",
                connection))
            {
                selectDraft.Parameters.AddWithValue("@DraftId", draftId);
                selectDraft.Parameters.AddWithValue("@UserId", userId);
                using var reader = await selectDraft.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                    return null; // Não existe OU não é seu — indistinguível, mesmo padrão do Slice 1/2.

                workspaceId = reader.GetGuid(reader.GetOrdinal("WorkspaceId"));
                packageId = reader.GetGuid(reader.GetOrdinal("PackageId"));
                revisionId = reader.GetGuid(reader.GetOrdinal("RevisionId"));
                engine = reader.GetString(reader.GetOrdinal("Engine"));
                createdAt = new DateTimeOffset(reader.GetDateTime(reader.GetOrdinal("CreatedAt")), TimeSpan.Zero);
            }

            var rules = await LoadRulesAsync(connection, draftId, cancellationToken);
            return new MappingDraftDetail(draftId, workspaceId, packageId, revisionId, engine, createdAt, rules);
        }

        public async Task<MappingDraftRuleDetail?> GetRuleIfMemberAsync(Guid draftId, Guid ruleId, Guid userId, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            using var command = new SqlCommand(
                @"SELECT r.RuleId, r.DraftId, r.SourceRefs, r.TargetRefs, r.Operation, r.ConditionsJson, r.TransformationsJson,
                         r.Cardinality, r.EvidenceJson, r.Confidence, r.Status, r.OpenQuestionsJson, r.CreatedAt, r.RowVersion
                  FROM dbo.tbMappingDraftRule r
                  JOIN dbo.tbMappingDraft d ON d.DraftId = r.DraftId
                  JOIN dbo.tbWorkspaceMembership m ON m.WorkspaceId = d.WorkspaceId AND m.UserId = @UserId
                  WHERE r.RuleId = @RuleId AND r.DraftId = @DraftId;",
                connection);
            command.Parameters.AddWithValue("@RuleId", ruleId);
            command.Parameters.AddWithValue("@DraftId", draftId);
            command.Parameters.AddWithValue("@UserId", userId);

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            return ReadRuleDetail(reader);
        }

        public async Task InsertProposedRulesAsync(Guid draftId, Guid jobId, IReadOnlyList<MappingDraftRuleProposal> proposals, CancellationToken cancellationToken)
        {
            if (proposals.Count == 0)
                return;

            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            using var tx = connection.BeginTransaction();
            try
            {
                foreach (var proposal in proposals)
                {
                    var targetRefsJson = JsonSerializer.Serialize(proposal.TargetRefs, JsonOptions);

                    // Nova sugestão cobrindo o mesmo TargetRefs de uma regra já decidida vira superseded
                    // — nunca apagada (spec §8/design §2).
                    using (var supersede = new SqlCommand(
                        @"UPDATE dbo.tbMappingDraftRule
                          SET Status = @Superseded
                          WHERE DraftId = @DraftId AND TargetRefs = @TargetRefsJson
                                AND Status IN (@Accepted, @Edited, @Rejected);",
                        connection, tx))
                    {
                        supersede.Parameters.AddWithValue("@Superseded", MappingDraftRuleStatus.Superseded);
                        supersede.Parameters.AddWithValue("@DraftId", draftId);
                        supersede.Parameters.AddWithValue("@TargetRefsJson", targetRefsJson);
                        supersede.Parameters.AddWithValue("@Accepted", MappingDraftRuleStatus.Accepted);
                        supersede.Parameters.AddWithValue("@Edited", MappingDraftRuleStatus.Edited);
                        supersede.Parameters.AddWithValue("@Rejected", MappingDraftRuleStatus.Rejected);
                        await supersede.ExecuteNonQueryAsync(cancellationToken);
                    }

                    using var insert = new SqlCommand(
                        @"INSERT INTO dbo.tbMappingDraftRule
                            (RuleId, DraftId, SourceRefs, TargetRefs, Operation, ConditionsJson, TransformationsJson,
                             Cardinality, EvidenceJson, Confidence, Status, OpenQuestionsJson, CreatedByJobId, CreatedAt)
                          VALUES
                            (@RuleId, @DraftId, @SourceRefs, @TargetRefs, @Operation, @ConditionsJson, @TransformationsJson,
                             @Cardinality, @EvidenceJson, @Confidence, @Status, @OpenQuestionsJson, @CreatedByJobId, SYSUTCDATETIME());",
                        connection, tx);

                    insert.Parameters.AddWithValue("@RuleId", Guid.NewGuid());
                    insert.Parameters.AddWithValue("@DraftId", draftId);
                    insert.Parameters.AddWithValue("@SourceRefs", JsonSerializer.Serialize(proposal.SourceRefs, JsonOptions));
                    insert.Parameters.AddWithValue("@TargetRefs", targetRefsJson);
                    insert.Parameters.AddWithValue("@Operation", proposal.Operation);
                    insert.Parameters.AddWithValue("@ConditionsJson", proposal.ConditionsJson);
                    insert.Parameters.AddWithValue("@TransformationsJson", proposal.TransformationsJson);
                    insert.Parameters.AddWithValue("@Cardinality", proposal.Cardinality);
                    insert.Parameters.AddWithValue("@EvidenceJson", JsonSerializer.Serialize(proposal.Evidence, JsonOptions));
                    insert.Parameters.AddWithValue("@Confidence", proposal.Confidence);
                    insert.Parameters.AddWithValue("@Status", proposal.Status);
                    insert.Parameters.AddWithValue("@OpenQuestionsJson", JsonSerializer.Serialize(proposal.OpenQuestions, JsonOptions));
                    insert.Parameters.AddWithValue("@CreatedByJobId", jobId);
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                }

                await tx.CommitAsync(cancellationToken);
            }
            catch
            {
                await tx.RollbackAsync(cancellationToken);
                throw;
            }
        }

        public async Task<UpdateRuleOutcome> UpdateRuleStatusAsync(
            Guid draftId,
            Guid ruleId,
            Guid userId,
            byte[] expectedRowVersion,
            string newStatus,
            string? justification,
            IReadOnlyList<string>? editedSourceRefs,
            IReadOnlyList<string>? editedTargetRefs,
            string? editedOperation,
            CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            using var tx = connection.BeginTransaction();
            try
            {
                // Isolamento: só atualiza se a regra pertencer a um draft de um workspace do qual o
                // usuário é membro — mesma cláusula de join do Slice 1/2.
                using (var update = new SqlCommand(
                    @"UPDATE r
                      SET r.Status = @NewStatus,
                          r.SourceRefs = COALESCE(@EditedSourceRefs, r.SourceRefs),
                          r.TargetRefs = COALESCE(@EditedTargetRefs, r.TargetRefs),
                          r.Operation = COALESCE(@EditedOperation, r.Operation)
                      FROM dbo.tbMappingDraftRule r
                      JOIN dbo.tbMappingDraft d ON d.DraftId = r.DraftId
                      JOIN dbo.tbWorkspaceMembership m ON m.WorkspaceId = d.WorkspaceId AND m.UserId = @UserId
                      WHERE r.RuleId = @RuleId AND r.DraftId = @DraftId AND r.RowVersion = @ExpectedRowVersion;",
                    connection, tx))
                {
                    update.Parameters.AddWithValue("@NewStatus", newStatus);
                    update.Parameters.AddWithValue("@EditedSourceRefs", (object?)(editedSourceRefs != null ? JsonSerializer.Serialize(editedSourceRefs, JsonOptions) : null) ?? DBNull.Value);
                    update.Parameters.AddWithValue("@EditedTargetRefs", (object?)(editedTargetRefs != null ? JsonSerializer.Serialize(editedTargetRefs, JsonOptions) : null) ?? DBNull.Value);
                    update.Parameters.AddWithValue("@EditedOperation", (object?)editedOperation ?? DBNull.Value);
                    update.Parameters.AddWithValue("@RuleId", ruleId);
                    update.Parameters.AddWithValue("@DraftId", draftId);
                    update.Parameters.AddWithValue("@UserId", userId);
                    update.Parameters.AddWithValue("@ExpectedRowVersion", expectedRowVersion);

                    var rowsAffected = await update.ExecuteNonQueryAsync(cancellationToken);
                    if (rowsAffected == 0)
                    {
                        // RowCount=0: distingue "não existe/não é seu"(NotFound) de "conflito"(Conflict)
                        // por uma consulta de existência separada — só aqui, no caminho de falha.
                        using var existsCheck = new SqlCommand(
                            @"SELECT 1 FROM dbo.tbMappingDraftRule r
                              JOIN dbo.tbMappingDraft d ON d.DraftId = r.DraftId
                              JOIN dbo.tbWorkspaceMembership m ON m.WorkspaceId = d.WorkspaceId AND m.UserId = @UserId
                              WHERE r.RuleId = @RuleId AND r.DraftId = @DraftId;",
                            connection, tx);
                        existsCheck.Parameters.AddWithValue("@RuleId", ruleId);
                        existsCheck.Parameters.AddWithValue("@DraftId", draftId);
                        existsCheck.Parameters.AddWithValue("@UserId", userId);
                        var exists = await existsCheck.ExecuteScalarAsync(cancellationToken) != null;

                        await tx.RollbackAsync(cancellationToken);
                        return new UpdateRuleOutcome(exists ? UpdateRuleResult.Conflict : UpdateRuleResult.NotFound, null);
                    }
                }

                using (var insertDecision = new SqlCommand(
                    @"INSERT INTO dbo.tbMappingDraftRuleDecision (DecisionId, RuleId, ActorUserId, At, RevisionId, NewStatus, Justification)
                      SELECT @DecisionId, @RuleId, @ActorUserId, SYSUTCDATETIME(), d.RevisionId, @NewStatus, @Justification
                      FROM dbo.tbMappingDraftRule r JOIN dbo.tbMappingDraft d ON d.DraftId = r.DraftId
                      WHERE r.RuleId = @RuleId;",
                    connection, tx))
                {
                    insertDecision.Parameters.AddWithValue("@DecisionId", Guid.NewGuid());
                    insertDecision.Parameters.AddWithValue("@RuleId", ruleId);
                    insertDecision.Parameters.AddWithValue("@ActorUserId", userId);
                    insertDecision.Parameters.AddWithValue("@NewStatus", newStatus);
                    insertDecision.Parameters.AddWithValue("@Justification", (object?)justification ?? DBNull.Value);
                    await insertDecision.ExecuteNonQueryAsync(cancellationToken);
                }

                await tx.CommitAsync(cancellationToken);
            }
            catch
            {
                await tx.RollbackAsync(cancellationToken);
                throw;
            }

            using var selectUpdated = new SqlCommand(
                @"SELECT RuleId, DraftId, SourceRefs, TargetRefs, Operation, ConditionsJson, TransformationsJson,
                         Cardinality, EvidenceJson, Confidence, Status, OpenQuestionsJson, CreatedAt, RowVersion
                  FROM dbo.tbMappingDraftRule WHERE RuleId = @RuleId;",
                connection);
            selectUpdated.Parameters.AddWithValue("@RuleId", ruleId);
            using var readerFinal = await selectUpdated.ExecuteReaderAsync(cancellationToken);
            if (!await readerFinal.ReadAsync(cancellationToken))
                return new UpdateRuleOutcome(UpdateRuleResult.NotFound, null);

            return new UpdateRuleOutcome(UpdateRuleResult.Success, ReadRuleDetail(readerFinal));
        }

        private static async Task<IReadOnlyList<MappingDraftRuleDetail>> LoadRulesAsync(SqlConnection connection, Guid draftId, CancellationToken cancellationToken)
        {
            var result = new List<MappingDraftRuleDetail>();
            using var command = new SqlCommand(
                @"SELECT RuleId, DraftId, SourceRefs, TargetRefs, Operation, ConditionsJson, TransformationsJson,
                         Cardinality, EvidenceJson, Confidence, Status, OpenQuestionsJson, CreatedAt, RowVersion
                  FROM dbo.tbMappingDraftRule WHERE DraftId = @DraftId ORDER BY CreatedAt ASC;",
                connection);
            command.Parameters.AddWithValue("@DraftId", draftId);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(ReadRuleDetail(reader));
            return result;
        }

        private static MappingDraftRuleDetail ReadRuleDetail(SqlDataReader reader)
        {
            var rowVersion = (byte[])reader["RowVersion"];
            return new MappingDraftRuleDetail(
                reader.GetGuid(reader.GetOrdinal("RuleId")),
                reader.GetGuid(reader.GetOrdinal("DraftId")),
                JsonSerializer.Deserialize<List<string>>(reader.GetString(reader.GetOrdinal("SourceRefs")), JsonOptions) ?? new(),
                JsonSerializer.Deserialize<List<string>>(reader.GetString(reader.GetOrdinal("TargetRefs")), JsonOptions) ?? new(),
                reader.GetString(reader.GetOrdinal("Operation")),
                reader.GetString(reader.GetOrdinal("ConditionsJson")),
                reader.GetString(reader.GetOrdinal("TransformationsJson")),
                reader.GetString(reader.GetOrdinal("Cardinality")),
                JsonSerializer.Deserialize<List<MappingDraftRuleEvidence>>(reader.GetString(reader.GetOrdinal("EvidenceJson")), JsonOptions) ?? new(),
                reader.GetString(reader.GetOrdinal("Confidence")),
                reader.GetString(reader.GetOrdinal("Status")),
                JsonSerializer.Deserialize<List<string>>(reader.GetString(reader.GetOrdinal("OpenQuestionsJson")), JsonOptions) ?? new(),
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
IF OBJECT_ID('dbo.tbMappingDraft', 'U') IS NULL
CREATE TABLE dbo.tbMappingDraft (
    DraftId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    WorkspaceId UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.tbFiscalWorkspace(WorkspaceId),
    PackageId UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.tbFiscalMappingPackage(PackageId),
    RevisionId UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.tbFiscalMappingPackageRevision(RevisionId),
    Engine NVARCHAR(16) NOT NULL,
    CreatedByUserId UNIQUEIDENTIFIER NOT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

IF OBJECT_ID('dbo.tbMappingDraftRule', 'U') IS NULL
CREATE TABLE dbo.tbMappingDraftRule (
    RuleId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    DraftId UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.tbMappingDraft(DraftId),
    SourceRefs NVARCHAR(MAX) NOT NULL,
    TargetRefs NVARCHAR(MAX) NOT NULL,
    Operation NVARCHAR(32) NOT NULL,
    ConditionsJson NVARCHAR(MAX) NOT NULL,
    TransformationsJson NVARCHAR(MAX) NOT NULL,
    Cardinality NVARCHAR(8) NOT NULL,
    EvidenceJson NVARCHAR(MAX) NOT NULL,
    Confidence NVARCHAR(16) NOT NULL,
    Status NVARCHAR(16) NOT NULL,
    OpenQuestionsJson NVARCHAR(MAX) NOT NULL,
    CreatedByJobId UNIQUEIDENTIFIER NOT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    RowVersion ROWVERSION NOT NULL
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_tbMappingDraftRule_DraftId' AND object_id = OBJECT_ID('dbo.tbMappingDraftRule'))
CREATE INDEX IX_tbMappingDraftRule_DraftId ON dbo.tbMappingDraftRule(DraftId);

IF OBJECT_ID('dbo.tbMappingDraftRuleDecision', 'U') IS NULL
CREATE TABLE dbo.tbMappingDraftRuleDecision (
    DecisionId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    RuleId UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.tbMappingDraftRule(RuleId),
    ActorUserId UNIQUEIDENTIFIER NOT NULL,
    At DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    RevisionId UNIQUEIDENTIFIER NOT NULL,
    NewStatus NVARCHAR(16) NOT NULL,
    Justification NVARCHAR(2000) NULL
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_tbMappingDraftRuleDecision_RuleId' AND object_id = OBJECT_ID('dbo.tbMappingDraftRuleDecision'))
CREATE INDEX IX_tbMappingDraftRuleDecision_RuleId ON dbo.tbMappingDraftRuleDecision(RuleId);";

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
