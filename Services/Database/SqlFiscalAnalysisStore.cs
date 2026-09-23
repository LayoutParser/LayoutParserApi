using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Services.Interfaces;

using Microsoft.Data.SqlClient;

namespace LayoutParserApi.Services.Database
{
    /// <summary>
    /// Implementação SQL de <see cref="IFiscalAnalysisStore"/> — issue #366. Banco DEDICADO do
    /// projeto (<c>IdentityDatabase:*</c>); NUNCA <c>Database:*</c>/172.31.249.51 (somente leitura).
    /// Mesmo padrão ADO.NET cru + DDL lazy de <see cref="SqlTestSuiteStore"/>. Só metadado e hash:
    /// o conteúdo dos arquivos fica em disco.
    /// </summary>
    public sealed class SqlFiscalAnalysisStore : IFiscalAnalysisStore
    {
        private readonly ILogger<SqlFiscalAnalysisStore> _logger;
        private readonly string _connectionString;

        private static bool _schemaEnsured;
        private static readonly SemaphoreSlim _schemaLock = new(1, 1);

        public SqlFiscalAnalysisStore(ILogger<SqlFiscalAnalysisStore> logger, IConfiguration configuration)
        {
            _logger = logger;
            var server = configuration["IdentityDatabase:Server"];
            var database = configuration["IdentityDatabase:Database"];
            var userId = configuration["IdentityDatabase:UserId"];
            var password = configuration["IdentityDatabase:Password"];

            _connectionString = $"Server={server};Database={database};User Id={userId};Password={password};TrustServerCertificate=True;";
        }

        public async Task CreateAsync(FiscalAnalysisRecord analysis, IReadOnlyList<FiscalAnalysisFileRecord> files, CancellationToken cancellationToken)
        {
            using var connection = await OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();
            try
            {
                using (var command = new SqlCommand(
                    @"INSERT INTO dbo.tbLpFiscalAnalysis
                        (AnalysisId, WorkspaceId, OwnerUserId, CreatedAtUtc, ExpiresAtUtc, Source, LayoutMode, LayoutGuid, LayoutName, DetectedType)
                      VALUES (@AnalysisId, @WorkspaceId, @OwnerUserId, @CreatedAtUtc, @ExpiresAtUtc, @Source, @LayoutMode, @LayoutGuid, @LayoutName, @DetectedType);",
                    connection, transaction))
                {
                    command.Parameters.AddWithValue("@AnalysisId", analysis.AnalysisId);
                    command.Parameters.AddWithValue("@WorkspaceId", analysis.WorkspaceId);
                    command.Parameters.AddWithValue("@OwnerUserId", analysis.OwnerUserId);
                    command.Parameters.AddWithValue("@CreatedAtUtc", analysis.CreatedAtUtc);
                    command.Parameters.AddWithValue("@ExpiresAtUtc", analysis.ExpiresAtUtc);
                    command.Parameters.AddWithValue("@Source", analysis.Source);
                    command.Parameters.AddWithValue("@LayoutMode", analysis.LayoutMode);
                    command.Parameters.AddWithValue("@LayoutGuid", (object?)analysis.LayoutGuid ?? DBNull.Value);
                    command.Parameters.AddWithValue("@LayoutName", (object?)analysis.LayoutName ?? DBNull.Value);
                    command.Parameters.AddWithValue("@DetectedType", (object?)analysis.DetectedType ?? DBNull.Value);
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }

                foreach (var file in files)
                {
                    using var fileCommand = new SqlCommand(
                        @"INSERT INTO dbo.tbLpFiscalAnalysisFile
                            (AnalysisFileId, AnalysisId, Role, OriginalFileName, SizeBytes, Sha256, MimeSniffed, StoragePath)
                          VALUES (@AnalysisFileId, @AnalysisId, @Role, @OriginalFileName, @SizeBytes, @Sha256, @MimeSniffed, @StoragePath);",
                        connection, transaction);
                    fileCommand.Parameters.AddWithValue("@AnalysisFileId", file.AnalysisFileId);
                    fileCommand.Parameters.AddWithValue("@AnalysisId", file.AnalysisId);
                    fileCommand.Parameters.AddWithValue("@Role", file.Role);
                    fileCommand.Parameters.AddWithValue("@OriginalFileName", file.OriginalFileName);
                    fileCommand.Parameters.AddWithValue("@SizeBytes", file.SizeBytes);
                    fileCommand.Parameters.AddWithValue("@Sha256", file.Sha256);
                    fileCommand.Parameters.AddWithValue("@MimeSniffed", (object?)file.MimeSniffed ?? DBNull.Value);
                    fileCommand.Parameters.AddWithValue("@StoragePath", file.StoragePath);
                    await fileCommand.ExecuteNonQueryAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }

        public async Task<FiscalAnalysisPage> ListAsync(
            Guid workspaceId, Guid ownerUserId, int page, int pageSize, DateTime nowUtc, CancellationToken cancellationToken)
        {
            using var connection = await OpenAsync(cancellationToken);

            int total;
            using (var count = new SqlCommand(
                @"SELECT COUNT(*) FROM dbo.tbLpFiscalAnalysis
                  WHERE WorkspaceId = @WorkspaceId AND OwnerUserId = @OwnerUserId AND ExpiresAtUtc > @Now;", connection))
            {
                count.Parameters.AddWithValue("@WorkspaceId", workspaceId);
                count.Parameters.AddWithValue("@OwnerUserId", ownerUserId);
                count.Parameters.AddWithValue("@Now", nowUtc);
                total = (int)(await count.ExecuteScalarAsync(cancellationToken))!;
            }

            var items = new List<FiscalAnalysisSummary>();
            using (var command = new SqlCommand(
                @"SELECT a.AnalysisId, a.CreatedAtUtc, a.ExpiresAtUtc, a.Source, a.LayoutName, a.LayoutGuid, a.DetectedType,
                         (SELECT COUNT(*) FROM dbo.tbLpFiscalAnalysisFile f WHERE f.AnalysisId = a.AnalysisId) AS FileCount,
                         (SELECT ISNULL(SUM(f.SizeBytes), 0) FROM dbo.tbLpFiscalAnalysisFile f WHERE f.AnalysisId = a.AnalysisId) AS TotalSize
                  FROM dbo.tbLpFiscalAnalysis a
                  WHERE a.WorkspaceId = @WorkspaceId AND a.OwnerUserId = @OwnerUserId AND a.ExpiresAtUtc > @Now
                  ORDER BY a.CreatedAtUtc DESC, a.AnalysisId
                  OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;", connection))
            {
                command.Parameters.AddWithValue("@WorkspaceId", workspaceId);
                command.Parameters.AddWithValue("@OwnerUserId", ownerUserId);
                command.Parameters.AddWithValue("@Now", nowUtc);
                command.Parameters.AddWithValue("@Offset", (page - 1) * pageSize);
                command.Parameters.AddWithValue("@PageSize", pageSize);

                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    items.Add(new FiscalAnalysisSummary(
                        reader.GetGuid(0),
                        DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc),
                        DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc),
                        reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.IsDBNull(6) ? null : reader.GetString(6),
                        reader.GetInt32(7),
                        reader.GetInt64(8)));
                }
            }

            return new FiscalAnalysisPage(page, pageSize, total, items);
        }

        public async Task<FiscalAnalysisDetail?> GetAsync(
            Guid workspaceId, Guid ownerUserId, Guid analysisId, DateTime nowUtc, CancellationToken cancellationToken)
        {
            using var connection = await OpenAsync(cancellationToken);

            FiscalAnalysisRecord? analysis = null;
            using (var command = new SqlCommand(
                @"SELECT AnalysisId, WorkspaceId, OwnerUserId, CreatedAtUtc, ExpiresAtUtc, Source, LayoutMode, LayoutGuid, LayoutName, DetectedType
                  FROM dbo.tbLpFiscalAnalysis
                  WHERE AnalysisId = @AnalysisId AND WorkspaceId = @WorkspaceId AND OwnerUserId = @OwnerUserId AND ExpiresAtUtc > @Now;", connection))
            {
                command.Parameters.AddWithValue("@AnalysisId", analysisId);
                command.Parameters.AddWithValue("@WorkspaceId", workspaceId);
                command.Parameters.AddWithValue("@OwnerUserId", ownerUserId);
                command.Parameters.AddWithValue("@Now", nowUtc);

                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    analysis = new FiscalAnalysisRecord(
                        reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2),
                        DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc),
                        DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc),
                        reader.GetString(5), reader.GetString(6),
                        reader.IsDBNull(7) ? null : reader.GetString(7),
                        reader.IsDBNull(8) ? null : reader.GetString(8),
                        reader.IsDBNull(9) ? null : reader.GetString(9));
                }
            }

            if (analysis == null)
                return null;

            var files = new List<FiscalAnalysisFileRecord>();
            using (var command = new SqlCommand(
                @"SELECT AnalysisFileId, AnalysisId, Role, OriginalFileName, SizeBytes, Sha256, MimeSniffed, StoragePath
                  FROM dbo.tbLpFiscalAnalysisFile WHERE AnalysisId = @AnalysisId ORDER BY Role DESC, OriginalFileName;", connection))
            {
                command.Parameters.AddWithValue("@AnalysisId", analysisId);
                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    files.Add(new FiscalAnalysisFileRecord(
                        reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
                        reader.GetInt64(4), reader.GetString(5),
                        reader.IsDBNull(6) ? null : reader.GetString(6),
                        reader.GetString(7)));
                }
            }

            return new FiscalAnalysisDetail(analysis, files);
        }

        public async Task<bool> DeleteAsync(Guid workspaceId, Guid ownerUserId, Guid analysisId, CancellationToken cancellationToken)
        {
            using var connection = await OpenAsync(cancellationToken);
            using var command = new SqlCommand(
                @"DELETE FROM dbo.tbLpFiscalAnalysis
                  WHERE AnalysisId = @AnalysisId AND WorkspaceId = @WorkspaceId AND OwnerUserId = @OwnerUserId;", connection);
            command.Parameters.AddWithValue("@AnalysisId", analysisId);
            command.Parameters.AddWithValue("@WorkspaceId", workspaceId);
            command.Parameters.AddWithValue("@OwnerUserId", ownerUserId);
            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }

        public async Task<IReadOnlyList<FiscalAnalysisExpiredRef>> ListExpiredAsync(DateTime nowUtc, int batchSize, CancellationToken cancellationToken)
        {
            using var connection = await OpenAsync(cancellationToken);
            using var command = new SqlCommand(
                @"SELECT TOP (@Batch) AnalysisId, WorkspaceId FROM dbo.tbLpFiscalAnalysis
                  WHERE ExpiresAtUtc <= @Now ORDER BY ExpiresAtUtc;", connection);
            command.Parameters.AddWithValue("@Batch", batchSize);
            command.Parameters.AddWithValue("@Now", nowUtc);

            var result = new List<FiscalAnalysisExpiredRef>();
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(new FiscalAnalysisExpiredRef(reader.GetGuid(0), reader.GetGuid(1)));
            return result;
        }

        public async Task DeleteByIdAsync(Guid analysisId, CancellationToken cancellationToken)
        {
            using var connection = await OpenAsync(cancellationToken);
            using var command = new SqlCommand("DELETE FROM dbo.tbLpFiscalAnalysis WHERE AnalysisId = @AnalysisId;", connection);
            command.Parameters.AddWithValue("@AnalysisId", analysisId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task<IReadOnlySet<Guid>> GetExistingIdsAsync(IReadOnlyCollection<Guid> analysisIds, CancellationToken cancellationToken)
        {
            var existing = new HashSet<Guid>();
            if (analysisIds.Count == 0)
                return existing;

            using var connection = await OpenAsync(cancellationToken);
            // Em lotes: o limite de parâmetros do SQL Server é 2100.
            foreach (var chunk in analysisIds.Chunk(500))
            {
                using var command = new SqlCommand { Connection = connection };
                var names = new List<string>();
                for (var i = 0; i < chunk.Length; i++)
                {
                    names.Add($"@p{i}");
                    command.Parameters.AddWithValue($"@p{i}", chunk[i]);
                }
                command.CommandText = $"SELECT AnalysisId FROM dbo.tbLpFiscalAnalysis WHERE AnalysisId IN ({string.Join(",", names)});";

                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    existing.Add(reader.GetGuid(0));
            }

            return existing;
        }

        private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
        {
            var connection = new SqlConnection(_connectionString);
            try
            {
                await connection.OpenAsync(cancellationToken);
                await EnsureSchemaAsync(connection, cancellationToken);
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        // FK lógica para tbLpFiscalWorkspace (sem constraint — mesma decisão dos demais stores fiscais,
        // ver SqlFiscalPackageStore.SchemaDdl). Só FK entre as duas tabelas, com ON DELETE CASCADE.
        public static readonly string SchemaDdl = @"
IF OBJECT_ID('dbo.tbLpFiscalAnalysis', 'U') IS NULL
CREATE TABLE dbo.tbLpFiscalAnalysis (
    AnalysisId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    WorkspaceId UNIQUEIDENTIFIER NOT NULL,
    OwnerUserId UNIQUEIDENTIFIER NOT NULL,
    CreatedAtUtc DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    ExpiresAtUtc DATETIME2 NOT NULL,
    Source NVARCHAR(16) NOT NULL,
    LayoutMode NVARCHAR(16) NOT NULL,
    LayoutGuid NVARCHAR(64) NULL,
    LayoutName NVARCHAR(256) NULL,
    DetectedType NVARCHAR(32) NULL
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_tbLpFiscalAnalysis_Workspace_Owner_Created' AND object_id = OBJECT_ID('dbo.tbLpFiscalAnalysis'))
CREATE INDEX IX_tbLpFiscalAnalysis_Workspace_Owner_Created ON dbo.tbLpFiscalAnalysis(WorkspaceId, OwnerUserId, CreatedAtUtc DESC);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_tbLpFiscalAnalysis_ExpiresAtUtc' AND object_id = OBJECT_ID('dbo.tbLpFiscalAnalysis'))
CREATE INDEX IX_tbLpFiscalAnalysis_ExpiresAtUtc ON dbo.tbLpFiscalAnalysis(ExpiresAtUtc);

IF OBJECT_ID('dbo.tbLpFiscalAnalysisFile', 'U') IS NULL
CREATE TABLE dbo.tbLpFiscalAnalysisFile (
    AnalysisFileId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    AnalysisId UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.tbLpFiscalAnalysis(AnalysisId) ON DELETE CASCADE,
    Role NVARCHAR(16) NOT NULL,
    OriginalFileName NVARCHAR(260) NOT NULL,
    SizeBytes BIGINT NOT NULL,
    Sha256 CHAR(64) NOT NULL,
    MimeSniffed NVARCHAR(128) NULL,
    StoragePath NVARCHAR(512) NOT NULL
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_tbLpFiscalAnalysisFile_AnalysisId' AND object_id = OBJECT_ID('dbo.tbLpFiscalAnalysisFile'))
CREATE INDEX IX_tbLpFiscalAnalysisFile_AnalysisId ON dbo.tbLpFiscalAnalysisFile(AnalysisId);
";

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
