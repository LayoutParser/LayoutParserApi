using LayoutParserApi.Services.Interfaces;

using Microsoft.Data.SqlClient;

namespace LayoutParserApi.Services.Database
{
    /// <summary>
    /// Implementação SQL de <see cref="IGeneratedMapperArtifactStore"/> — issue #438, ADR
    /// <c>docs/architecture/adr-geracao-automatica-gabarito-sysmiddle.md</c>. Banco DEDICADO do
    /// projeto (<c>IdentityDatabase:*</c>), mesmo padrão ADO.NET cru de <see cref="SqlFieldCorrectionStore"/>.
    /// Tabela autossuficiente, sem FK para <c>tbMapper</c> (esse vive no banco compartilhado
    /// somente-leitura <c>172.31.249.51</c>, ver <c>.claude/rules/security.md</c>).
    /// </summary>
    public sealed class SqlGeneratedMapperArtifactStore : IGeneratedMapperArtifactStore
    {
        private readonly ILogger<SqlGeneratedMapperArtifactStore> _logger;
        private readonly string _connectionString;

        private static bool _schemaEnsured;
        private static readonly SemaphoreSlim _schemaLock = new(1, 1);

        public SqlGeneratedMapperArtifactStore(ILogger<SqlGeneratedMapperArtifactStore> logger, IConfiguration configuration)
        {
            _logger = logger;
            var server = configuration["IdentityDatabase:Server"];
            var database = configuration["IdentityDatabase:Database"];
            var userId = configuration["IdentityDatabase:UserId"];
            var password = configuration["IdentityDatabase:Password"];

            _connectionString = $"Server={server};Database={database};User Id={userId};Password={password};TrustServerCertificate=True;";
        }

        public async Task<GeneratedMapperArtifactRecord?> GetAsync(string mapperGuid, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            using var command = new SqlCommand(
                @"SELECT MapperGuid, Status, Content, CoverageJson, ValidationBasis, MapperVoHash,
                         CorrelationId, GeneratedAtUtc, UpdatedAtUtc
                  FROM dbo.tbGeneratedMapperArtifact WHERE MapperGuid = @MapperGuid;",
                connection);
            command.Parameters.AddWithValue("@MapperGuid", mapperGuid);

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
        }

        public async Task<bool> TryBeginGeneratingAsync(string mapperGuid, string correlationId, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            // Tenta inserir primeiro — se a linha não existe, esta chamada "ganha" a corrida sem
            // precisar de lock explícito (a PK em MapperGuid garante atomicidade no SQL Server).
            try
            {
                using var insert = new SqlCommand(
                    @"INSERT INTO dbo.tbGeneratedMapperArtifact (MapperGuid, Status, CorrelationId, UpdatedAtUtc)
                      VALUES (@MapperGuid, @Generating, @CorrelationId, SYSUTCDATETIME());",
                    connection);
                insert.Parameters.AddWithValue("@MapperGuid", mapperGuid);
                insert.Parameters.AddWithValue("@Generating", GeneratedMapperArtifactStatus.Generating);
                insert.Parameters.AddWithValue("@CorrelationId", correlationId);
                await insert.ExecuteNonQueryAsync(cancellationToken);
                return true;
            }
            catch (SqlException ex) when (ex.Number is 2627 or 2601)
            {
                // Linha já existe (outra chamada chegou primeiro, ou é uma regeração de ready/stale) —
                // só assume se NÃO estiver "generating" agora (WHERE no UPDATE é o que evita a corrida
                // de duas chamadas concorrentes disparando geração duplicada).
                using var update = new SqlCommand(
                    @"UPDATE dbo.tbGeneratedMapperArtifact
                         SET Status = @Generating, CorrelationId = @CorrelationId, UpdatedAtUtc = SYSUTCDATETIME()
                       WHERE MapperGuid = @MapperGuid AND Status <> @Generating;",
                    connection);
                update.Parameters.AddWithValue("@MapperGuid", mapperGuid);
                update.Parameters.AddWithValue("@Generating", GeneratedMapperArtifactStatus.Generating);
                update.Parameters.AddWithValue("@CorrelationId", correlationId);
                var affected = await update.ExecuteNonQueryAsync(cancellationToken);
                return affected == 1;
            }
        }

        public async Task CompleteAsync(
            string mapperGuid, string content, string coverageJson, string validationBasis,
            string mapperVoHash, string correlationId, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            using var command = new SqlCommand(
                @"UPDATE dbo.tbGeneratedMapperArtifact
                     SET Status = @Ready, Content = @Content, CoverageJson = @CoverageJson,
                         ValidationBasis = @ValidationBasis, MapperVoHash = @MapperVoHash,
                         CorrelationId = @CorrelationId, GeneratedAtUtc = SYSUTCDATETIME(),
                         UpdatedAtUtc = SYSUTCDATETIME()
                   WHERE MapperGuid = @MapperGuid;",
                connection);
            command.Parameters.AddWithValue("@Ready", GeneratedMapperArtifactStatus.Ready);
            command.Parameters.AddWithValue("@Content", content);
            command.Parameters.AddWithValue("@CoverageJson", coverageJson);
            command.Parameters.AddWithValue("@ValidationBasis", validationBasis);
            command.Parameters.AddWithValue("@MapperVoHash", mapperVoHash);
            command.Parameters.AddWithValue("@CorrelationId", correlationId);
            command.Parameters.AddWithValue("@MapperGuid", mapperGuid);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task FailAsync(string mapperGuid, CancellationToken cancellationToken)
        {
            try
            {
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync(cancellationToken);
                await EnsureSchemaAsync(connection, cancellationToken);

                using var command = new SqlCommand(
                    "DELETE FROM dbo.tbGeneratedMapperArtifact WHERE MapperGuid = @MapperGuid;", connection);
                command.Parameters.AddWithValue("@MapperGuid", mapperGuid);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                // Best-effort: se nem o rollback conseguir gravar, o mapper fica preso em
                // "generating" até o SQL voltar — próximo GET vê status=generating (não quebra),
                // e uma nova tentativa manual sempre pode ser feita depois.
                _logger.LogWarning(ex, "Falha ao reverter status de geração para o mapper {MapperGuid} — pode ficar preso em 'generating' até o SQL voltar.", mapperGuid);
            }
        }

        private static GeneratedMapperArtifactRecord Map(SqlDataReader reader) => new(
            reader.GetString(reader.GetOrdinal("MapperGuid")),
            reader.GetString(reader.GetOrdinal("Status")),
            reader.IsDBNull(reader.GetOrdinal("Content")) ? null : reader.GetString(reader.GetOrdinal("Content")),
            reader.IsDBNull(reader.GetOrdinal("CoverageJson")) ? null : reader.GetString(reader.GetOrdinal("CoverageJson")),
            reader.IsDBNull(reader.GetOrdinal("ValidationBasis")) ? null : reader.GetString(reader.GetOrdinal("ValidationBasis")),
            reader.IsDBNull(reader.GetOrdinal("MapperVoHash")) ? null : reader.GetString(reader.GetOrdinal("MapperVoHash")),
            reader.IsDBNull(reader.GetOrdinal("CorrelationId")) ? null : reader.GetString(reader.GetOrdinal("CorrelationId")),
            reader.IsDBNull(reader.GetOrdinal("GeneratedAtUtc")) ? null : new DateTimeOffset(reader.GetDateTime(reader.GetOrdinal("GeneratedAtUtc")), TimeSpan.Zero),
            new DateTimeOffset(reader.GetDateTime(reader.GetOrdinal("UpdatedAtUtc")), TimeSpan.Zero));

        public static readonly string SchemaDdl = @"
IF OBJECT_ID('dbo.tbGeneratedMapperArtifact', 'U') IS NULL
CREATE TABLE dbo.tbGeneratedMapperArtifact (
    MapperGuid NVARCHAR(64) NOT NULL PRIMARY KEY,
    Status NVARCHAR(20) NOT NULL,
    Content NVARCHAR(MAX) NULL,
    CoverageJson NVARCHAR(MAX) NULL,
    ValidationBasis NVARCHAR(30) NULL,
    MapperVoHash NVARCHAR(64) NULL,
    CorrelationId NVARCHAR(100) NULL,
    GeneratedAtUtc DATETIME2 NULL,
    UpdatedAtUtc DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);";

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
