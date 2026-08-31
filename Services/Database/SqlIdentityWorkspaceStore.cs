using LayoutParserApi.Models.Entities.Identity;
using LayoutParserApi.Services.Interfaces;

using Microsoft.Data.SqlClient;

namespace LayoutParserApi.Services.Database
{
    /// <summary>
    /// Implementação SQL de <see cref="IIdentityWorkspaceStore"/> — Slice 1 (issue #225/#228). Segue o
    /// mesmo padrão de acesso a dado de <see cref="MapperDatabaseService"/> (ADO.NET cru, connection
    /// string montada de <c>Database:*</c>, mesmo banco <c>ConnectUS_Macgyver</c> — não há banco
    /// dedicado para este projeto).
    /// </summary>
    /// <remarks>
    /// As tabelas (<c>tbUser</c>, <c>tbExternalIdentity</c>, <c>tbFiscalWorkspace</c>,
    /// <c>tbWorkspaceMembership</c>) são criadas de forma idempotente (<c>IF OBJECT_ID(...) IS NULL</c>)
    /// na primeira chamada de cada instância de processo — não há projeto de migração dedicado nesta
    /// API ainda. A garantia de "não duplicar sob concorrência" É o UNIQUE constraint
    /// (<c>UQ_tbExternalIdentity</c>, índice filtrado por workspace pessoal, <c>UQ_tbWorkspaceMembership</c>):
    /// o INSERT tenta direto e, se colidir (erro 2601/2627), relê a linha existente — não usa
    /// <c>SELECT</c> prévio como garantia (só como fast-path), porque um <c>SELECT</c> antes do
    /// <c>INSERT</c> tem janela de corrida entre processos.
    /// </remarks>
    public sealed class SqlIdentityWorkspaceStore : IIdentityWorkspaceStore
    {
        private readonly ILogger<SqlIdentityWorkspaceStore> _logger;
        private readonly string _connectionString;

        // Erros de violação de UNIQUE/PRIMARY KEY do SQL Server.
        private static readonly HashSet<int> UniqueViolationErrorNumbers = new() { 2601, 2627 };

        // DDL roda uma vez por processo (não por chamada) — flag estática protegida por lock.
        private static bool _schemaEnsured;
        private static readonly SemaphoreSlim _schemaLock = new(1, 1);

        public SqlIdentityWorkspaceStore(ILogger<SqlIdentityWorkspaceStore> logger, IConfiguration configuration)
        {
            _logger = logger;
            var server = configuration["Database:Server"];
            var database = configuration["Database:Database"];
            var userId = configuration["Database:UserId"];
            var password = configuration["Database:Password"];

            _connectionString = $"Server={server};Database={database};User Id={userId};Password={password};TrustServerCertificate=True;";
        }

        public async Task<Guid> ResolveOrCreateUserAsync(string provider, string tenantOrIssuer, string subject, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            // Fast-path: já existe.
            var existing = await SelectUserIdByExternalIdentityAsync(connection, provider, tenantOrIssuer, subject, cancellationToken);
            if (existing != null)
                return existing.Value;

            var newUserId = Guid.NewGuid();
            using (var tx = connection.BeginTransaction())
            {
                try
                {
                    using (var insertUser = new SqlCommand(
                        "INSERT INTO dbo.tbUser (UserId, CreatedAt) VALUES (@UserId, SYSUTCDATETIME());",
                        connection, tx))
                    {
                        insertUser.Parameters.AddWithValue("@UserId", newUserId);
                        await insertUser.ExecuteNonQueryAsync(cancellationToken);
                    }

                    using (var insertIdentity = new SqlCommand(
                        @"INSERT INTO dbo.tbExternalIdentity (ExternalIdentityId, UserId, Provider, TenantOrIssuer, Subject, CreatedAt)
                          VALUES (@ExternalIdentityId, @UserId, @Provider, @TenantOrIssuer, @Subject, SYSUTCDATETIME());",
                        connection, tx))
                    {
                        insertIdentity.Parameters.AddWithValue("@ExternalIdentityId", Guid.NewGuid());
                        insertIdentity.Parameters.AddWithValue("@UserId", newUserId);
                        insertIdentity.Parameters.AddWithValue("@Provider", provider);
                        insertIdentity.Parameters.AddWithValue("@TenantOrIssuer", tenantOrIssuer);
                        insertIdentity.Parameters.AddWithValue("@Subject", subject);
                        await insertIdentity.ExecuteNonQueryAsync(cancellationToken);
                    }

                    await tx.CommitAsync(cancellationToken);
                    return newUserId;
                }
                catch (SqlException ex) when (UniqueViolationErrorNumbers.Contains(ex.Number))
                {
                    // Corrida perdida: outra requisição criou a ExternalIdentity entre o SELECT e o
                    // INSERT. Não é erro — relê o UserId que já existe (idempotência sob concorrência,
                    // critério de aceite #3 do contrato cross-repo).
                    await tx.RollbackAsync(cancellationToken);
                    _logger.LogInformation("Corrida de criação de identidade externa detectada (provider={Provider}); relendo UserId existente.", provider);
                }
                catch
                {
                    await tx.RollbackAsync(cancellationToken);
                    throw;
                }
            }

            var afterRace = await SelectUserIdByExternalIdentityAsync(connection, provider, tenantOrIssuer, subject, cancellationToken);
            return afterRace
                ?? throw new InvalidOperationException("Colisão de UNIQUE em tbExternalIdentity, mas a releitura não encontrou a linha — estado inconsistente.");
        }

        public async Task<WorkspaceSummary> EnsurePersonalWorkspaceAsync(Guid userId, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            var existing = await SelectPersonalWorkspaceAsync(connection, userId, cancellationToken);
            if (existing != null)
                return existing;

            var workspaceId = Guid.NewGuid();
            using (var tx = connection.BeginTransaction())
            {
                try
                {
                    using (var insertWorkspace = new SqlCommand(
                        @"INSERT INTO dbo.tbFiscalWorkspace (WorkspaceId, Name, Kind, OwnerUserId, CreatedAt)
                          VALUES (@WorkspaceId, @Name, @Kind, @OwnerUserId, SYSUTCDATETIME());",
                        connection, tx))
                    {
                        insertWorkspace.Parameters.AddWithValue("@WorkspaceId", workspaceId);
                        insertWorkspace.Parameters.AddWithValue("@Name", "Meu workspace fiscal");
                        insertWorkspace.Parameters.AddWithValue("@Kind", WorkspaceKind.Personal);
                        insertWorkspace.Parameters.AddWithValue("@OwnerUserId", userId);
                        await insertWorkspace.ExecuteNonQueryAsync(cancellationToken);
                    }

                    using (var insertMembership = new SqlCommand(
                        @"INSERT INTO dbo.tbWorkspaceMembership (WorkspaceMembershipId, WorkspaceId, UserId, Role, CreatedAt)
                          VALUES (@Id, @WorkspaceId, @UserId, @Role, SYSUTCDATETIME());",
                        connection, tx))
                    {
                        insertMembership.Parameters.AddWithValue("@Id", Guid.NewGuid());
                        insertMembership.Parameters.AddWithValue("@WorkspaceId", workspaceId);
                        insertMembership.Parameters.AddWithValue("@UserId", userId);
                        insertMembership.Parameters.AddWithValue("@Role", WorkspaceRole.Owner);
                        await insertMembership.ExecuteNonQueryAsync(cancellationToken);
                    }

                    await tx.CommitAsync(cancellationToken);
                }
                catch (SqlException ex) when (UniqueViolationErrorNumbers.Contains(ex.Number))
                {
                    // Corrida perdida: outra requisição já criou o workspace pessoal deste usuário
                    // (índice filtrado UX_tbFiscalWorkspace_PersonalOwner). Relê abaixo.
                    await tx.RollbackAsync(cancellationToken);
                    _logger.LogInformation("Corrida de criação de workspace pessoal detectada (userId={UserId}); relendo workspace existente.", userId);
                }
                catch
                {
                    await tx.RollbackAsync(cancellationToken);
                    throw;
                }
            }

            var afterRace = await SelectPersonalWorkspaceAsync(connection, userId, cancellationToken);
            return afterRace
                ?? throw new InvalidOperationException("Colisão de UNIQUE em tbFiscalWorkspace, mas a releitura não encontrou o workspace pessoal — estado inconsistente.");
        }

        public async Task<IReadOnlyList<WorkspaceSummary>> GetMembershipsAsync(Guid userId, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            var result = new List<WorkspaceSummary>();
            using var command = new SqlCommand(
                @"SELECT w.WorkspaceId, w.Name, w.Kind, m.Role, w.CreatedAt
                  FROM dbo.tbWorkspaceMembership m
                  JOIN dbo.tbFiscalWorkspace w ON w.WorkspaceId = m.WorkspaceId
                  WHERE m.UserId = @UserId
                  ORDER BY w.CreatedAt ASC;",
                connection);
            command.Parameters.AddWithValue("@UserId", userId);

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(ReadWorkspaceSummary(reader));

            return result;
        }

        public async Task<WorkspaceSummary?> GetWorkspaceIfMemberAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            using var command = new SqlCommand(
                @"SELECT w.WorkspaceId, w.Name, w.Kind, m.Role, w.CreatedAt
                  FROM dbo.tbWorkspaceMembership m
                  JOIN dbo.tbFiscalWorkspace w ON w.WorkspaceId = m.WorkspaceId
                  WHERE m.WorkspaceId = @WorkspaceId AND m.UserId = @UserId;",
                connection);
            command.Parameters.AddWithValue("@WorkspaceId", workspaceId);
            command.Parameters.AddWithValue("@UserId", userId);

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null; // Sem membership: "não existe" e "não é seu" ficam indistinguíveis daqui pra cima.

            return ReadWorkspaceSummary(reader);
        }

        private static WorkspaceSummary ReadWorkspaceSummary(SqlDataReader reader) => new(
            reader.GetGuid(reader.GetOrdinal("WorkspaceId")),
            reader.GetString(reader.GetOrdinal("Name")),
            reader.GetString(reader.GetOrdinal("Kind")),
            reader.GetString(reader.GetOrdinal("Role")),
            new DateTimeOffset(reader.GetDateTime(reader.GetOrdinal("CreatedAt")), TimeSpan.Zero));

        private static async Task<Guid?> SelectUserIdByExternalIdentityAsync(SqlConnection connection, string provider, string tenantOrIssuer, string subject, CancellationToken cancellationToken)
        {
            using var command = new SqlCommand(
                "SELECT UserId FROM dbo.tbExternalIdentity WHERE Provider = @Provider AND TenantOrIssuer = @TenantOrIssuer AND Subject = @Subject;",
                connection);
            command.Parameters.AddWithValue("@Provider", provider);
            command.Parameters.AddWithValue("@TenantOrIssuer", tenantOrIssuer);
            command.Parameters.AddWithValue("@Subject", subject);

            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is Guid guid ? guid : null;
        }

        private static async Task<WorkspaceSummary?> SelectPersonalWorkspaceAsync(SqlConnection connection, Guid userId, CancellationToken cancellationToken)
        {
            using var command = new SqlCommand(
                @"SELECT w.WorkspaceId, w.Name, w.Kind, m.Role, w.CreatedAt
                  FROM dbo.tbFiscalWorkspace w
                  JOIN dbo.tbWorkspaceMembership m ON m.WorkspaceId = w.WorkspaceId AND m.UserId = w.OwnerUserId
                  WHERE w.OwnerUserId = @UserId AND w.Kind = @Kind;",
                connection);
            command.Parameters.AddWithValue("@UserId", userId);
            command.Parameters.AddWithValue("@Kind", WorkspaceKind.Personal);

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            return ReadWorkspaceSummary(reader);
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
IF OBJECT_ID('dbo.tbUser', 'U') IS NULL
CREATE TABLE dbo.tbUser (
    UserId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

IF OBJECT_ID('dbo.tbExternalIdentity', 'U') IS NULL
CREATE TABLE dbo.tbExternalIdentity (
    ExternalIdentityId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    UserId UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.tbUser(UserId),
    Provider NVARCHAR(64) NOT NULL,
    TenantOrIssuer NVARCHAR(256) NOT NULL,
    Subject NVARCHAR(256) NOT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_tbExternalIdentity UNIQUE (Provider, TenantOrIssuer, Subject)
);

IF OBJECT_ID('dbo.tbFiscalWorkspace', 'U') IS NULL
CREATE TABLE dbo.tbFiscalWorkspace (
    WorkspaceId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    Name NVARCHAR(256) NOT NULL,
    Kind NVARCHAR(32) NOT NULL,
    OwnerUserId UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.tbUser(UserId),
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_tbFiscalWorkspace_PersonalOwner' AND object_id = OBJECT_ID('dbo.tbFiscalWorkspace'))
CREATE UNIQUE INDEX UX_tbFiscalWorkspace_PersonalOwner ON dbo.tbFiscalWorkspace(OwnerUserId) WHERE Kind = 'personal';

IF OBJECT_ID('dbo.tbWorkspaceMembership', 'U') IS NULL
CREATE TABLE dbo.tbWorkspaceMembership (
    WorkspaceMembershipId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    WorkspaceId UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.tbFiscalWorkspace(WorkspaceId),
    UserId UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.tbUser(UserId),
    Role NVARCHAR(32) NOT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_tbWorkspaceMembership UNIQUE (WorkspaceId, UserId)
);";

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
