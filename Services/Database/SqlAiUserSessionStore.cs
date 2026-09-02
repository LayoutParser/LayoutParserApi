using LayoutParserApi.Services.Logging;

using Microsoft.Data.SqlClient;

namespace LayoutParserApi.Services.Database
{
    /// <summary>
    /// Histórico de longo prazo do pathway de IA por usuário (issue #102), schema recomendado em
    /// <c>docs/architecture/sessao-usuario-e-artefatos-compartilhados-2026-08-14.md</c> §2.1-§2.2.
    /// </summary>
    /// <remarks>
    /// <b>Onde mora:</b> banco <c>IdentityDatabase</c> — o mesmo já criado para identidade/workspace
    /// (<see cref="SqlIdentityWorkspaceStore"/>, Slice 1, issues #225/#228) — não o
    /// <c>ConnectUS_Macgyver</c> do Sysmiddle (credencial compartilhada org-wide, ver
    /// <c>.claude/rules/security.md</c>) nem um terceiro banco novo. É o único banco SQL dedicado
    /// que este projeto já provisionou fora do Sysmiddle, e a issue de origem só recomenda "SQL como
    /// fonte de verdade" sem exigir isolamento de outro domínio.
    /// <para>
    /// <b>Por que <c>UserId</c> é <c>NVARCHAR</c>, não o <c>UNIQUEIDENTIFIER</c> de
    /// <c>tbLpUser</c>:</b> o particionamento por usuário do <see cref="Transformation.Ai.AiCandidateStore"/>
    /// (issue #92) já usa <c>ICurrentUser.Name</c> (string — nome/e-mail vindo do BFF via
    /// <c>TrustedIdentityMiddleware</c>) como chave, não o <c>Guid</c> interno de identidade. Esta
    /// tabela reaproveita a MESMA chave para não duplicar a resolução de identidade nem exigir que o
    /// pathway de IA (que não conhece o agregado de identidade) faça um lookup extra por requisição —
    /// a issue nasce do particionamento do #92, então herda sua chave.
    /// </para>
    /// <para>
    /// <b>Conteúdo pesado não duplicado (critério de aceite da issue):</b> esta tabela guarda só
    /// referência (<c>Ticket</c>) e <c>Status</c> — o XSLT/TCL gerado continua vivendo no
    /// <see cref="Transformation.Ai.AiCandidateStore"/> (cache quente, TTL curto) enquanto o ticket
    /// está em progresso, e no catálogo de mapeadores quando promovido.
    /// </para>
    /// <para>
    /// Schema criado de forma idempotente na primeira chamada de cada instância de processo, mesmo
    /// padrão do <see cref="SqlIdentityWorkspaceStore"/> — não há projeto de migração dedicado nesta
    /// API ainda.
    /// </para>
    /// </remarks>
    public sealed class SqlAiUserSessionStore
    {
        private readonly ILogger<SqlAiUserSessionStore> _logger;
        private readonly string _connectionString;

        private static bool _schemaEnsured;
        private static readonly SemaphoreSlim _schemaLock = new(1, 1);

        public SqlAiUserSessionStore(ILogger<SqlAiUserSessionStore> logger, IConfiguration configuration)
        {
            _logger = logger;
            var server = configuration["IdentityDatabase:Server"];
            var database = configuration["IdentityDatabase:Database"];
            var userId = configuration["IdentityDatabase:UserId"];
            var password = configuration["IdentityDatabase:Password"];

            _connectionString = $"Server={server};Database={database};User Id={userId};Password={password};TrustServerCertificate=True;";
        }

        /// <summary>
        /// Garante a linha de sessão do usuário (upsert simples) e opcionalmente atualiza o prompt
        /// customizado ativo — mesmo espírito de "sessão persistente" da issue #97, item que esta
        /// issue destrava do lado do schema.
        /// </summary>
        /// <remarks>
        /// Resiliência: qualquer falha de SQL aqui é capturada e logada como Warning — gravar
        /// histórico é auditoria, não pode derrubar o pathway de IA em si (mesmo princípio do
        /// <c>AiCandidateStore</c> em disco, que também degrada para "memória apenas" se o I/O falhar).
        /// </remarks>
        public async Task EnsureSessionAsync(string userId, string? customPromptInstruction, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(userId))
                return;

            try
            {
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync(cancellationToken);
                await EnsureSchemaAsync(connection, cancellationToken);

                using var command = new SqlCommand(
                    @"MERGE dbo.tbLpAiUserSession AS target
                      USING (SELECT @UserId AS UserId) AS source
                      ON target.UserId = source.UserId
                      WHEN MATCHED THEN
                          UPDATE SET CustomPromptInstruction = COALESCE(@CustomPromptInstruction, target.CustomPromptInstruction),
                                     UpdatedAt = SYSUTCDATETIME()
                      WHEN NOT MATCHED THEN
                          INSERT (UserId, CustomPromptInstruction, CreatedAt, UpdatedAt)
                          VALUES (@UserId, @CustomPromptInstruction, SYSUTCDATETIME(), SYSUTCDATETIME());",
                    connection);
                command.Parameters.AddWithValue("@UserId", userId);
                command.Parameters.AddWithValue("@CustomPromptInstruction", (object?)customPromptInstruction ?? DBNull.Value);

                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao gravar sessão de IA do usuário (degradado — não afeta o pathway de IA em si)");
            }
        }

        /// <summary>
        /// Registra uma entrada de histórico (ticket + status) para o usuário — chamado quando um job
        /// do pathway de IA chega a um status terminal (<c>converged</c>/<c>failed</c>).
        /// </summary>
        public async Task AddHistoryEntryAsync(string userId, string ticket, string status, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(ticket))
                return;

            try
            {
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync(cancellationToken);
                await EnsureSchemaAsync(connection, cancellationToken);

                // Sessão precisa existir antes da FK — upsert idempotente sem sobrescrever o prompt
                // já salvo (COALESCE(NULL, ...) preserva o valor atual, ver EnsureSessionAsync acima).
                using (var ensureSession = new SqlCommand(
                    @"IF NOT EXISTS (SELECT 1 FROM dbo.tbLpAiUserSession WHERE UserId = @UserId)
                      INSERT INTO dbo.tbLpAiUserSession (UserId, CreatedAt, UpdatedAt) VALUES (@UserId, SYSUTCDATETIME(), SYSUTCDATETIME());",
                    connection))
                {
                    ensureSession.Parameters.AddWithValue("@UserId", userId);
                    await ensureSession.ExecuteNonQueryAsync(cancellationToken);
                }

                using var insertHistory = new SqlCommand(
                    @"INSERT INTO dbo.tbLpAiUserSessionHistoryEntry (HistoryEntryId, UserId, Ticket, Status, CreatedAt)
                      VALUES (@HistoryEntryId, @UserId, @Ticket, @Status, SYSUTCDATETIME());",
                    connection);
                insertHistory.Parameters.AddWithValue("@HistoryEntryId", Guid.NewGuid());
                insertHistory.Parameters.AddWithValue("@UserId", userId);
                insertHistory.Parameters.AddWithValue("@Ticket", ticket);
                insertHistory.Parameters.AddWithValue("@Status", status);

                await insertHistory.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                // Degrade: histórico é auditoria, não pode quebrar o job de IA que acabou de terminar.
                var safeTicket = Services.Logging.LogMessageSanitizer.Sanitize(ticket);
                _logger.LogWarning(ex, "Falha ao gravar histórico de sessão de IA (ticket={Ticket}) — degradado, não afeta o pathway de IA em si", safeTicket);
            }
        }

        /// <summary>Lista o histórico mais recente do usuário (mais novo primeiro) — consulta simples de suporte/auditoria.</summary>
        public async Task<IReadOnlyList<AiUserSessionHistoryEntry>> GetHistoryAsync(string userId, int maxEntries, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(userId))
                return Array.Empty<AiUserSessionHistoryEntry>();

            try
            {
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync(cancellationToken);
                await EnsureSchemaAsync(connection, cancellationToken);

                using var command = new SqlCommand(
                    @"SELECT TOP (@Max) Ticket, Status, CreatedAt
                      FROM dbo.tbLpAiUserSessionHistoryEntry
                      WHERE UserId = @UserId
                      ORDER BY CreatedAt DESC;",
                    connection);
                command.Parameters.AddWithValue("@UserId", userId);
                command.Parameters.AddWithValue("@Max", maxEntries > 0 ? maxEntries : 50);

                var result = new List<AiUserSessionHistoryEntry>();
                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    result.Add(new AiUserSessionHistoryEntry(
                        reader.GetString(reader.GetOrdinal("Ticket")),
                        reader.GetString(reader.GetOrdinal("Status")),
                        new DateTimeOffset(reader.GetDateTime(reader.GetOrdinal("CreatedAt")), TimeSpan.Zero)));
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao ler histórico de sessão de IA — degradado (lista vazia)");
                return Array.Empty<AiUserSessionHistoryEntry>();
            }
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
IF OBJECT_ID('dbo.tbLpAiUserSession', 'U') IS NULL
CREATE TABLE dbo.tbLpAiUserSession (
    UserId NVARCHAR(256) NOT NULL PRIMARY KEY,
    CustomPromptInstruction NVARCHAR(MAX) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

IF OBJECT_ID('dbo.tbLpAiUserSessionHistoryEntry', 'U') IS NULL
CREATE TABLE dbo.tbLpAiUserSessionHistoryEntry (
    HistoryEntryId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    UserId NVARCHAR(256) NOT NULL REFERENCES dbo.tbLpAiUserSession(UserId),
    Ticket NVARCHAR(256) NOT NULL,
    Status NVARCHAR(32) NOT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_tbLpAiUserSessionHistoryEntry_UserId_CreatedAt' AND object_id = OBJECT_ID('dbo.tbLpAiUserSessionHistoryEntry'))
CREATE INDEX IX_tbLpAiUserSessionHistoryEntry_UserId_CreatedAt ON dbo.tbLpAiUserSessionHistoryEntry(UserId, CreatedAt DESC);";

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

    /// <summary>Uma entrada do histórico de longo prazo do pathway de IA (issue #102).</summary>
    public record AiUserSessionHistoryEntry(string Ticket, string Status, DateTimeOffset CreatedAt);
}
