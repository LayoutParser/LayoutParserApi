using LayoutParserApi.Services.Interfaces;

using Microsoft.Data.SqlClient;

namespace LayoutParserApi.Services.Database
{
    /// <summary>
    /// Implementação SQL de <see cref="IMappingRuleAnswerStore"/> — issue #422. Banco DEDICADO do projeto
    /// (<c>IdentityDatabase:*</c>; nunca o compartilhado 172.31.249.51). Mesmo padrão ADO.NET cru e
    /// <c>EnsureSchemaAsync</c> idempotente de <see cref="SqlFieldCorrectionStore"/>. Sem FK para
    /// tbMappingDraft/tbMappingDraftRule (autossuficiente; o vínculo é validado no controller).
    /// </summary>
    public sealed class SqlMappingRuleAnswerStore : IMappingRuleAnswerStore
    {
        private readonly ILogger<SqlMappingRuleAnswerStore> _logger;
        private readonly string _connectionString;

        private static bool _schemaEnsured;
        private static readonly SemaphoreSlim _schemaLock = new(1, 1);

        private const string Columns =
            "a.AnswerId, a.WorkspaceId, a.DraftId, a.RuleId, a.QuestionIndex, a.QuestionText, a.AnswerText, a.AnsweredByUserId, a.AnsweredByName, a.AnsweredAtUtc, a.Version";

        public SqlMappingRuleAnswerStore(ILogger<SqlMappingRuleAnswerStore> logger, IConfiguration configuration)
        {
            _logger = logger;
            var server = configuration["IdentityDatabase:Server"];
            var database = configuration["IdentityDatabase:Database"];
            var userId = configuration["IdentityDatabase:UserId"];
            var password = configuration["IdentityDatabase:Password"];

            _connectionString = $"Server={server};Database={database};User Id={userId};Password={password};TrustServerCertificate=True;";
        }

        public async Task<(MappingRuleAnswer Answer, bool Created)> SaveAnswerAsync(
            Guid workspaceId, Guid draftId, Guid ruleId, int questionIndex, string questionText,
            string answerText, Guid userId, string? userName, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            using var tx = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                // UPDLOCK/HOLDLOCK serializa respostas concorrentes à mesma pergunta; o índice único
                // (RuleId, QuestionIndex, Version) é a rede de segurança.
                MappingRuleAnswer? latest = null;
                using (var select = new SqlCommand(
                    $@"SELECT TOP 1 {Columns} FROM dbo.tbMappingRuleAnswer a WITH (UPDLOCK, HOLDLOCK)
                       WHERE a.RuleId = @RuleId AND a.QuestionIndex = @QuestionIndex
                       ORDER BY a.Version DESC;", connection, tx))
                {
                    select.Parameters.AddWithValue("@RuleId", ruleId);
                    select.Parameters.AddWithValue("@QuestionIndex", questionIndex);
                    using var reader = await select.ExecuteReaderAsync(cancellationToken);
                    if (await reader.ReadAsync(cancellationToken))
                        latest = Read(reader);
                }

                if (latest != null && string.Equals(latest.AnswerText, answerText, StringComparison.Ordinal))
                {
                    await tx.CommitAsync(cancellationToken);
                    return (latest, false);
                }

                var answer = new MappingRuleAnswer(
                    Guid.NewGuid(), workspaceId, draftId, ruleId, questionIndex, questionText, answerText,
                    userId, userName, DateTimeOffset.UtcNow, (latest?.Version ?? 0) + 1);

                using (var insert = new SqlCommand(
                    @"INSERT INTO dbo.tbMappingRuleAnswer
                          (AnswerId, WorkspaceId, DraftId, RuleId, QuestionIndex, QuestionText, AnswerText,
                           AnsweredByUserId, AnsweredByName, AnsweredAtUtc, Version)
                      VALUES
                          (@AnswerId, @WorkspaceId, @DraftId, @RuleId, @QuestionIndex, @QuestionText, @AnswerText,
                           @UserId, @UserName, @At, @Version);", connection, tx))
                {
                    insert.Parameters.AddWithValue("@AnswerId", answer.AnswerId);
                    insert.Parameters.AddWithValue("@WorkspaceId", workspaceId);
                    insert.Parameters.AddWithValue("@DraftId", draftId);
                    insert.Parameters.AddWithValue("@RuleId", ruleId);
                    insert.Parameters.AddWithValue("@QuestionIndex", questionIndex);
                    insert.Parameters.AddWithValue("@QuestionText", questionText);
                    insert.Parameters.AddWithValue("@AnswerText", answerText);
                    insert.Parameters.AddWithValue("@UserId", userId);
                    insert.Parameters.AddWithValue("@UserName", (object?)userName ?? DBNull.Value);
                    insert.Parameters.AddWithValue("@At", answer.AnsweredAt.UtcDateTime);
                    insert.Parameters.AddWithValue("@Version", answer.Version);
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                }

                await tx.CommitAsync(cancellationToken);
                return (answer, true);
            }
            catch
            {
                await tx.RollbackAsync(cancellationToken);
                throw;
            }
        }

        public async Task<IReadOnlyList<MappingRuleAnswer>> ListAsync(
            Guid draftId, Guid? ruleId, bool includeHistory, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            // Fragmentos constantes; valores do cliente só via SqlParameter.
            var conditions = new List<string> { "a.DraftId = @DraftId" };
            if (ruleId.HasValue)
                conditions.Add("a.RuleId = @RuleId");
            if (!includeHistory)
                conditions.Add("a.Version = (SELECT MAX(x.Version) FROM dbo.tbMappingRuleAnswer x WHERE x.RuleId = a.RuleId AND x.QuestionIndex = a.QuestionIndex)");

            using var command = new SqlCommand(
                $"SELECT {Columns} FROM dbo.tbMappingRuleAnswer a WHERE {string.Join(" AND ", conditions)} ORDER BY a.RuleId, a.QuestionIndex, a.Version;",
                connection);
            command.Parameters.AddWithValue("@DraftId", draftId);
            if (ruleId.HasValue)
                command.Parameters.AddWithValue("@RuleId", ruleId.Value);

            var result = new List<MappingRuleAnswer>();
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(Read(reader));
            return result;
        }

        private static MappingRuleAnswer Read(SqlDataReader reader) => new(
            reader.GetGuid(reader.GetOrdinal("AnswerId")),
            reader.GetGuid(reader.GetOrdinal("WorkspaceId")),
            reader.GetGuid(reader.GetOrdinal("DraftId")),
            reader.GetGuid(reader.GetOrdinal("RuleId")),
            reader.GetInt32(reader.GetOrdinal("QuestionIndex")),
            reader.GetString(reader.GetOrdinal("QuestionText")),
            reader.GetString(reader.GetOrdinal("AnswerText")),
            reader.GetGuid(reader.GetOrdinal("AnsweredByUserId")),
            reader.IsDBNull(reader.GetOrdinal("AnsweredByName")) ? null : reader.GetString(reader.GetOrdinal("AnsweredByName")),
            new DateTimeOffset(reader.GetDateTime(reader.GetOrdinal("AnsweredAtUtc")), TimeSpan.Zero),
            reader.GetInt32(reader.GetOrdinal("Version")));

        public static readonly string SchemaDdl = @"
IF OBJECT_ID('dbo.tbMappingRuleAnswer', 'U') IS NULL
CREATE TABLE dbo.tbMappingRuleAnswer (
    AnswerId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    WorkspaceId UNIQUEIDENTIFIER NOT NULL,
    DraftId UNIQUEIDENTIFIER NOT NULL,
    RuleId UNIQUEIDENTIFIER NOT NULL,
    QuestionIndex INT NOT NULL,
    QuestionText NVARCHAR(MAX) NOT NULL,
    AnswerText NVARCHAR(4000) NOT NULL,
    AnsweredByUserId UNIQUEIDENTIFIER NOT NULL,
    AnsweredByName NVARCHAR(256) NULL,
    AnsweredAtUtc DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    Version INT NOT NULL
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_tbMappingRuleAnswer_Key' AND object_id = OBJECT_ID('dbo.tbMappingRuleAnswer'))
CREATE UNIQUE INDEX UX_tbMappingRuleAnswer_Key ON dbo.tbMappingRuleAnswer(RuleId, QuestionIndex, Version);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_tbMappingRuleAnswer_DraftId' AND object_id = OBJECT_ID('dbo.tbMappingRuleAnswer'))
CREATE INDEX IX_tbMappingRuleAnswer_DraftId ON dbo.tbMappingRuleAnswer(DraftId);";

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
