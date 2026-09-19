using System.Text.Json;

using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Services.Interfaces;

using Microsoft.Data.SqlClient;

namespace LayoutParserApi.Services.Database
{
    /// <summary>
    /// Implementação SQL de <see cref="ITestSuiteStore"/> — issue #423. Banco DEDICADO do projeto
    /// (<c>IdentityDatabase:*</c>), mesmo padrão ADO.NET cru de <see cref="SqlFieldCorrectionStore"/>.
    /// As 3 tabelas são autossuficientes quanto a FK entre si (tbTestSuiteFixture/tbTestSuiteRun
    /// referenciam tbTestSuite via FK própria), mas SEM FK para tbMappingDraft/tbMappingRelease —
    /// mesma decisão de <see cref="SqlGeneratedMapperArtifactStore"/>: draft/release já são validados
    /// pela camada de aplicação (membership do workspace) antes de chegar aqui, e uma FK cruzando para
    /// tabelas de outro store adicionaria acoplamento de ordem de schema sem ganho real de integridade.
    /// </summary>
    public sealed class SqlTestSuiteStore : ITestSuiteStore
    {
        private readonly ILogger<SqlTestSuiteStore> _logger;
        private readonly string _connectionString;

        private static bool _schemaEnsured;
        private static readonly SemaphoreSlim _schemaLock = new(1, 1);
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public SqlTestSuiteStore(ILogger<SqlTestSuiteStore> logger, IConfiguration configuration)
        {
            _logger = logger;
            var server = configuration["IdentityDatabase:Server"];
            var database = configuration["IdentityDatabase:Database"];
            var userId = configuration["IdentityDatabase:UserId"];
            var password = configuration["IdentityDatabase:Password"];

            _connectionString = $"Server={server};Database={database};User Id={userId};Password={password};TrustServerCertificate=True;";
        }

        public async Task<TestSuiteDetail> CreateSuiteAsync(
            Guid workspaceId, Guid draftId, string name, string? description, Guid createdByUserId, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            var suiteId = Guid.NewGuid();
            using var command = new SqlCommand(
                @"INSERT INTO dbo.tbTestSuite (SuiteId, WorkspaceId, DraftId, Name, Description, CreatedByUserId, CreatedAtUtc)
                  OUTPUT INSERTED.RowVersion
                  VALUES (@SuiteId, @WorkspaceId, @DraftId, @Name, @Description, @CreatedByUserId, SYSUTCDATETIME());",
                connection);
            command.Parameters.AddWithValue("@SuiteId", suiteId);
            command.Parameters.AddWithValue("@WorkspaceId", workspaceId);
            command.Parameters.AddWithValue("@DraftId", draftId);
            command.Parameters.AddWithValue("@Name", name);
            command.Parameters.AddWithValue("@Description", (object?)description ?? DBNull.Value);
            command.Parameters.AddWithValue("@CreatedByUserId", createdByUserId);

            var rowVersion = (byte[])(await command.ExecuteScalarAsync(cancellationToken))!;
            var createdAt = DateTimeOffset.UtcNow;

            return new TestSuiteDetail(suiteId, workspaceId, draftId, name, description, createdByUserId, createdAt, Convert.ToBase64String(rowVersion));
        }

        public async Task<TestSuiteDetail?> GetSuiteIfMemberAsync(Guid suiteId, Guid userId, CancellationToken cancellationToken)
        {
            // Mesmo padrão fail-closed de SqlMappingReleaseStore.GetReleaseIfMemberAsync: o JOIN com
            // tbLpWorkspaceMembership é o próprio filtro de "é membro do workspace da suíte" — não
            // basta o controller checar workspaceId da rota, a query já nega no nível do SQL.
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            using var command = new SqlCommand(
                @"SELECT s.SuiteId, s.WorkspaceId, s.DraftId, s.Name, s.Description, s.CreatedByUserId, s.CreatedAtUtc, s.RowVersion
                  FROM dbo.tbTestSuite s
                  JOIN dbo.tbLpWorkspaceMembership m ON m.WorkspaceId = s.WorkspaceId AND m.UserId = @UserId
                  WHERE s.SuiteId = @SuiteId;",
                connection);
            command.Parameters.AddWithValue("@SuiteId", suiteId);
            command.Parameters.AddWithValue("@UserId", userId);

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? MapSuite(reader) : null;
        }

        public async Task<IReadOnlyList<TestSuiteDetail>> ListByDraftAsync(Guid draftId, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            using var command = new SqlCommand(
                @"SELECT SuiteId, WorkspaceId, DraftId, Name, Description, CreatedByUserId, CreatedAtUtc, RowVersion
                  FROM dbo.tbTestSuite WHERE DraftId = @DraftId ORDER BY CreatedAtUtc DESC;",
                connection);
            command.Parameters.AddWithValue("@DraftId", draftId);

            var result = new List<TestSuiteDetail>();
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(MapSuite(reader));

            return result;
        }

        public async Task<TestSuiteFixtureDetail> AddFixtureAsync(
            Guid suiteId, string name, string inputXml, string expectedXml, string? xsdVersion, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            using var nextOrder = new SqlCommand(
                @"SELECT ISNULL(MAX(SortOrder), -1) + 1 FROM dbo.tbTestSuiteFixture WHERE SuiteId = @SuiteId;", connection);
            nextOrder.Parameters.AddWithValue("@SuiteId", suiteId);
            var sortOrder = (int)(await nextOrder.ExecuteScalarAsync(cancellationToken))!;

            var fixtureId = Guid.NewGuid();
            using var command = new SqlCommand(
                @"INSERT INTO dbo.tbTestSuiteFixture (FixtureId, SuiteId, Name, InputXml, ExpectedXml, XsdVersion, SortOrder, CreatedAtUtc)
                  VALUES (@FixtureId, @SuiteId, @Name, @InputXml, @ExpectedXml, @XsdVersion, @SortOrder, SYSUTCDATETIME());",
                connection);
            command.Parameters.AddWithValue("@FixtureId", fixtureId);
            command.Parameters.AddWithValue("@SuiteId", suiteId);
            command.Parameters.AddWithValue("@Name", name);
            command.Parameters.AddWithValue("@InputXml", inputXml);
            command.Parameters.AddWithValue("@ExpectedXml", expectedXml);
            command.Parameters.AddWithValue("@XsdVersion", (object?)xsdVersion ?? DBNull.Value);
            command.Parameters.AddWithValue("@SortOrder", sortOrder);
            await command.ExecuteNonQueryAsync(cancellationToken);

            return new TestSuiteFixtureDetail(fixtureId, suiteId, name, inputXml, expectedXml, xsdVersion, sortOrder, DateTimeOffset.UtcNow);
        }

        public async Task<IReadOnlyList<TestSuiteFixtureDetail>> ListFixturesAsync(Guid suiteId, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            using var command = new SqlCommand(
                @"SELECT FixtureId, SuiteId, Name, InputXml, ExpectedXml, XsdVersion, SortOrder, CreatedAtUtc
                  FROM dbo.tbTestSuiteFixture WHERE SuiteId = @SuiteId ORDER BY SortOrder ASC;",
                connection);
            command.Parameters.AddWithValue("@SuiteId", suiteId);

            var result = new List<TestSuiteFixtureDetail>();
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(new TestSuiteFixtureDetail(
                    reader.GetGuid(reader.GetOrdinal("FixtureId")),
                    reader.GetGuid(reader.GetOrdinal("SuiteId")),
                    reader.GetString(reader.GetOrdinal("Name")),
                    reader.GetString(reader.GetOrdinal("InputXml")),
                    reader.GetString(reader.GetOrdinal("ExpectedXml")),
                    reader.IsDBNull(reader.GetOrdinal("XsdVersion")) ? null : reader.GetString(reader.GetOrdinal("XsdVersion")),
                    reader.GetInt32(reader.GetOrdinal("SortOrder")),
                    new DateTimeOffset(reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc")), TimeSpan.Zero)));
            }

            return result;
        }

        public async Task<TestSuiteRunDetail> RecordRunAsync(
            Guid suiteId, Guid releaseId, Guid executedByUserId,
            IReadOnlyList<TestSuiteFixtureResult> fixtureResults, double durationMs, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            var runId = Guid.NewGuid();
            var passed = fixtureResults.Count(f => f.Passed);
            var failed = fixtureResults.Count - passed;
            var requiredGatesPassed = fixtureResults.Count > 0 && failed == 0;

            using var command = new SqlCommand(
                @"INSERT INTO dbo.tbTestSuiteRun
                    (RunId, SuiteId, ReleaseId, ExecutedByUserId, ExecutedAtUtc, TotalFixtures, Passed, Failed, RequiredGatesPassed, DurationMs, FixtureResultsJson)
                  VALUES
                    (@RunId, @SuiteId, @ReleaseId, @ExecutedByUserId, SYSUTCDATETIME(), @TotalFixtures, @Passed, @Failed, @RequiredGatesPassed, @DurationMs, @FixtureResultsJson);",
                connection);
            command.Parameters.AddWithValue("@RunId", runId);
            command.Parameters.AddWithValue("@SuiteId", suiteId);
            command.Parameters.AddWithValue("@ReleaseId", releaseId);
            command.Parameters.AddWithValue("@ExecutedByUserId", executedByUserId);
            command.Parameters.AddWithValue("@TotalFixtures", fixtureResults.Count);
            command.Parameters.AddWithValue("@Passed", passed);
            command.Parameters.AddWithValue("@Failed", failed);
            command.Parameters.AddWithValue("@RequiredGatesPassed", requiredGatesPassed);
            command.Parameters.AddWithValue("@DurationMs", durationMs);
            command.Parameters.AddWithValue("@FixtureResultsJson", JsonSerializer.Serialize(fixtureResults, JsonOptions));
            await command.ExecuteNonQueryAsync(cancellationToken);

            return new TestSuiteRunDetail(
                runId, suiteId, releaseId, executedByUserId, DateTimeOffset.UtcNow,
                fixtureResults.Count, passed, failed, requiredGatesPassed, durationMs, fixtureResults);
        }

        public async Task<(IReadOnlyList<TestSuiteRunDetail> Items, int TotalCount)> ListRunsAsync(
            Guid suiteId, int page, int pageSize, CancellationToken cancellationToken)
        {
            if (page < 1) page = 1;
            if (pageSize is < 1 or > 200) pageSize = 20;

            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            using var countCommand = new SqlCommand(
                @"SELECT COUNT(*) FROM dbo.tbTestSuiteRun WHERE SuiteId = @SuiteId;", connection);
            countCommand.Parameters.AddWithValue("@SuiteId", suiteId);
            var totalCount = (int)(await countCommand.ExecuteScalarAsync(cancellationToken))!;

            using var command = new SqlCommand(
                @"SELECT RunId, SuiteId, ReleaseId, ExecutedByUserId, ExecutedAtUtc, TotalFixtures, Passed, Failed, RequiredGatesPassed, DurationMs, FixtureResultsJson
                  FROM dbo.tbTestSuiteRun
                  WHERE SuiteId = @SuiteId
                  ORDER BY ExecutedAtUtc DESC
                  OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;",
                connection);
            command.Parameters.AddWithValue("@SuiteId", suiteId);
            command.Parameters.AddWithValue("@Offset", (page - 1) * pageSize);
            command.Parameters.AddWithValue("@PageSize", pageSize);

            var items = new List<TestSuiteRunDetail>();
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new TestSuiteRunDetail(
                    reader.GetGuid(reader.GetOrdinal("RunId")),
                    reader.GetGuid(reader.GetOrdinal("SuiteId")),
                    reader.GetGuid(reader.GetOrdinal("ReleaseId")),
                    reader.GetGuid(reader.GetOrdinal("ExecutedByUserId")),
                    new DateTimeOffset(reader.GetDateTime(reader.GetOrdinal("ExecutedAtUtc")), TimeSpan.Zero),
                    reader.GetInt32(reader.GetOrdinal("TotalFixtures")),
                    reader.GetInt32(reader.GetOrdinal("Passed")),
                    reader.GetInt32(reader.GetOrdinal("Failed")),
                    reader.GetBoolean(reader.GetOrdinal("RequiredGatesPassed")),
                    reader.GetDouble(reader.GetOrdinal("DurationMs")),
                    JsonSerializer.Deserialize<List<TestSuiteFixtureResult>>(reader.GetString(reader.GetOrdinal("FixtureResultsJson")), JsonOptions) ?? new()));
            }

            return (items, totalCount);
        }

        private static TestSuiteDetail MapSuite(SqlDataReader reader) => new(
            reader.GetGuid(reader.GetOrdinal("SuiteId")),
            reader.GetGuid(reader.GetOrdinal("WorkspaceId")),
            reader.GetGuid(reader.GetOrdinal("DraftId")),
            reader.GetString(reader.GetOrdinal("Name")),
            reader.IsDBNull(reader.GetOrdinal("Description")) ? null : reader.GetString(reader.GetOrdinal("Description")),
            reader.GetGuid(reader.GetOrdinal("CreatedByUserId")),
            new DateTimeOffset(reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc")), TimeSpan.Zero),
            Convert.ToBase64String((byte[])reader["RowVersion"]));

        public static readonly string SchemaDdl = @"
IF OBJECT_ID('dbo.tbTestSuite', 'U') IS NULL
CREATE TABLE dbo.tbTestSuite (
    SuiteId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    WorkspaceId UNIQUEIDENTIFIER NOT NULL,
    DraftId UNIQUEIDENTIFIER NOT NULL,
    Name NVARCHAR(256) NOT NULL,
    Description NVARCHAR(2000) NULL,
    CreatedByUserId UNIQUEIDENTIFIER NOT NULL,
    CreatedAtUtc DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    RowVersion ROWVERSION NOT NULL
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_tbTestSuite_DraftId' AND object_id = OBJECT_ID('dbo.tbTestSuite'))
CREATE INDEX IX_tbTestSuite_DraftId ON dbo.tbTestSuite(DraftId);

IF OBJECT_ID('dbo.tbTestSuiteFixture', 'U') IS NULL
CREATE TABLE dbo.tbTestSuiteFixture (
    FixtureId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    SuiteId UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.tbTestSuite(SuiteId),
    Name NVARCHAR(256) NOT NULL,
    InputXml NVARCHAR(MAX) NOT NULL,
    ExpectedXml NVARCHAR(MAX) NOT NULL,
    XsdVersion NVARCHAR(32) NULL,
    SortOrder INT NOT NULL DEFAULT 0,
    CreatedAtUtc DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_tbTestSuiteFixture_SuiteId' AND object_id = OBJECT_ID('dbo.tbTestSuiteFixture'))
CREATE INDEX IX_tbTestSuiteFixture_SuiteId ON dbo.tbTestSuiteFixture(SuiteId);

IF OBJECT_ID('dbo.tbTestSuiteRun', 'U') IS NULL
CREATE TABLE dbo.tbTestSuiteRun (
    RunId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    SuiteId UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.tbTestSuite(SuiteId),
    ReleaseId UNIQUEIDENTIFIER NOT NULL,
    ExecutedByUserId UNIQUEIDENTIFIER NOT NULL,
    ExecutedAtUtc DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    TotalFixtures INT NOT NULL,
    Passed INT NOT NULL,
    Failed INT NOT NULL,
    RequiredGatesPassed BIT NOT NULL,
    DurationMs FLOAT NOT NULL,
    FixtureResultsJson NVARCHAR(MAX) NOT NULL
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_tbTestSuiteRun_SuiteId_ExecutedAtUtc' AND object_id = OBJECT_ID('dbo.tbTestSuiteRun'))
CREATE INDEX IX_tbTestSuiteRun_SuiteId_ExecutedAtUtc ON dbo.tbTestSuiteRun(SuiteId, ExecutedAtUtc DESC);";

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
