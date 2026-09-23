using LayoutParserApi.Services.Logging;

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

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

        /// <summary>
        /// Retenção efetiva do histórico (issue #97), já resolvida com o fallback para o default —
        /// mesma convenção de <c>AiCandidateStore.Ttl</c>, exposta para o background service de
        /// limpeza não precisar reler o <see cref="IOptions{TOptions}"/> nem duplicar a lógica de
        /// fallback.
        /// </summary>
        public TimeSpan HistoryRetention { get; }

        private static bool _schemaEnsured;
        private static readonly SemaphoreSlim _schemaLock = new(1, 1);

        public SqlAiUserSessionStore(
            ILogger<SqlAiUserSessionStore> logger,
            IConfiguration configuration,
            IOptions<AiUserSessionHistoryOptions> historyOptions)
        {
            _logger = logger;
            var server = configuration["IdentityDatabase:Server"];
            var database = configuration["IdentityDatabase:Database"];
            var userId = configuration["IdentityDatabase:UserId"];
            var password = configuration["IdentityDatabase:Password"];

            _connectionString = $"Server={server};Database={database};User Id={userId};Password={password};TrustServerCertificate=True;";

            var dias = historyOptions.Value.HistoryRetentionDays;
            HistoryRetention = TimeSpan.FromDays(
                dias > 0 ? dias : AiUserSessionHistoryOptions.DefaultHistoryRetentionDays);
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
        /// Issue #322: upsert das 3 preferências de usuário além do prompt customizado (idioma de
        /// exibição, nível de detalhe da explicação, engine padrão quando ambíguo). Mesmo espírito de
        /// <see cref="EnsureSessionAsync"/> — <c>COALESCE(@Valor, target.Coluna)</c> preserva o que já
        /// estava salvo quando o parâmetro chega <c>null</c> (permite atualizar só uma preferência por
        /// vez sem apagar as outras).
        /// </summary>
        /// <remarks>
        /// Resiliência: mesmo padrão de degradação do resto da store — falha de SQL é Warning, nunca
        /// derruba o pathway/endpoint que chamou.
        /// </remarks>
        public async Task SetPreferencesAsync(
            string userId,
            string? preferredLanguage,
            string? preferredExplanationDetailLevel,
            string? defaultTransformationEngine,
            CancellationToken cancellationToken)
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
                          UPDATE SET PreferredLanguage = COALESCE(@PreferredLanguage, target.PreferredLanguage),
                                     PreferredExplanationDetailLevel = COALESCE(@PreferredExplanationDetailLevel, target.PreferredExplanationDetailLevel),
                                     DefaultTransformationEngine = COALESCE(@DefaultTransformationEngine, target.DefaultTransformationEngine),
                                     UpdatedAt = SYSUTCDATETIME()
                      WHEN NOT MATCHED THEN
                          INSERT (UserId, PreferredLanguage, PreferredExplanationDetailLevel, DefaultTransformationEngine, CreatedAt, UpdatedAt)
                          VALUES (@UserId, @PreferredLanguage, @PreferredExplanationDetailLevel, @DefaultTransformationEngine, SYSUTCDATETIME(), SYSUTCDATETIME());",
                    connection);
                command.Parameters.AddWithValue("@UserId", userId);
                command.Parameters.AddWithValue("@PreferredLanguage", (object?)preferredLanguage ?? DBNull.Value);
                command.Parameters.AddWithValue("@PreferredExplanationDetailLevel", (object?)preferredExplanationDetailLevel ?? DBNull.Value);
                command.Parameters.AddWithValue("@DefaultTransformationEngine", (object?)defaultTransformationEngine ?? DBNull.Value);

                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao gravar preferências de IA do usuário (degradado — não afeta o pathway de IA em si)");
            }
        }

        /// <summary>
        /// Issue #322: leitura das 4 preferências persistidas (prompt customizado + as 3 novas).
        /// Devolve <c>null</c> quando o usuário nunca teve sessão criada (endpoint decide o default
        /// nesse caso) — diferente de "sessão existe mas coluna é NULL", onde a preferência individual
        /// vem <c>null</c> dentro do record e quem chama aplica o default por campo.
        /// </summary>
        public async Task<AiUserPreferences?> GetPreferencesAsync(string userId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(userId))
                return null;

            try
            {
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync(cancellationToken);
                await EnsureSchemaAsync(connection, cancellationToken);

                using var command = new SqlCommand(
                    @"SELECT CustomPromptInstruction, PreferredLanguage, PreferredExplanationDetailLevel, DefaultTransformationEngine
                      FROM dbo.tbLpAiUserSession
                      WHERE UserId = @UserId;",
                    connection);
                command.Parameters.AddWithValue("@UserId", userId);

                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                    return null;

                return new AiUserPreferences(
                    reader.IsDBNull(reader.GetOrdinal("CustomPromptInstruction")) ? null : reader.GetString(reader.GetOrdinal("CustomPromptInstruction")),
                    reader.IsDBNull(reader.GetOrdinal("PreferredLanguage")) ? null : reader.GetString(reader.GetOrdinal("PreferredLanguage")),
                    reader.IsDBNull(reader.GetOrdinal("PreferredExplanationDetailLevel")) ? null : reader.GetString(reader.GetOrdinal("PreferredExplanationDetailLevel")),
                    reader.IsDBNull(reader.GetOrdinal("DefaultTransformationEngine")) ? null : reader.GetString(reader.GetOrdinal("DefaultTransformationEngine")));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao ler preferências de IA do usuário (degradado — devolve null, endpoint aplica defaults)");
                return null;
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

        /// <summary>
        /// Purga entradas de histórico mais antigas que <see cref="HistoryRetention"/> (issue #97 —
        /// gap de TTL/retenção, "no mesmo espírito" do TTL do <c>AiCandidateStore</c>, issue #51).
        /// Chamado periodicamente por <see cref="AiUserSessionHistoryCleanupBackgroundService"/>.
        /// </summary>
        /// <remarks>
        /// Resiliência: mesma degradação graciosa do resto da store — falha de SQL aqui é auditoria
        /// de manutenção, não pode derrubar o host nem interromper o loop de limpeza.
        /// </remarks>
        public async Task<int> PurgeExpiredHistoryAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync(cancellationToken);
                await EnsureSchemaAsync(connection, cancellationToken);

                using var command = new SqlCommand(
                    @"DELETE FROM dbo.tbLpAiUserSessionHistoryEntry
                      WHERE CreatedAt < DATEADD(SECOND, -@RetentionSeconds, SYSUTCDATETIME());",
                    connection);
                command.Parameters.AddWithValue("@RetentionSeconds", (int)HistoryRetention.TotalSeconds);

                var removidos = await command.ExecuteNonQueryAsync(cancellationToken);
                return removidos;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao purgar histórico expirado de sessão de IA (degradado — não afeta o pathway de IA em si)");
                return 0;
            }
        }

        // ✅ Campo público-de-assembly — ver justificativa equivalente em
        // <see cref="SqlIdentityWorkspaceStore.SchemaDdl"/>. Autossuficiente (FK só aponta para tabela
        // criada no mesmo bloco).
        public static readonly string SchemaDdl = @"
IF OBJECT_ID('dbo.tbLpAiUserSession', 'U') IS NULL
CREATE TABLE dbo.tbLpAiUserSession (
    UserId NVARCHAR(256) NOT NULL PRIMARY KEY,
    CustomPromptInstruction NVARCHAR(MAX) NULL,
    PreferredLanguage NVARCHAR(16) NULL,
    PreferredExplanationDetailLevel NVARCHAR(32) NULL,
    DefaultTransformationEngine NVARCHAR(16) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

-- Issue #322: colunas novas em instalação já existente (ALTER idempotente, mesmo espírito do
-- CREATE TABLE IF OBJECT_ID IS NULL acima — não há projeto de migração dedicado nesta API).
IF COL_LENGTH('dbo.tbLpAiUserSession', 'PreferredLanguage') IS NULL
ALTER TABLE dbo.tbLpAiUserSession ADD PreferredLanguage NVARCHAR(16) NULL;

IF COL_LENGTH('dbo.tbLpAiUserSession', 'PreferredExplanationDetailLevel') IS NULL
ALTER TABLE dbo.tbLpAiUserSession ADD PreferredExplanationDetailLevel NVARCHAR(32) NULL;

IF COL_LENGTH('dbo.tbLpAiUserSession', 'DefaultTransformationEngine') IS NULL
ALTER TABLE dbo.tbLpAiUserSession ADD DefaultTransformationEngine NVARCHAR(16) NULL;

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

    /// <summary>Uma entrada do histórico de longo prazo do pathway de IA (issue #102).</summary>
    public record AiUserSessionHistoryEntry(string Ticket, string Status, DateTimeOffset CreatedAt);

    /// <summary>
    /// As 4 preferências persistidas por usuário no pathway de IA (issue #322: prompt customizado —
    /// já existente desde a issue #98 — + idioma/nível de detalhe/engine padrão, novos). Qualquer
    /// campo pode vir <c>null</c> quando a linha existe mas a preferência nunca foi setada — quem
    /// chama (o endpoint) aplica o default nesse caso, não esta store.
    /// </summary>
    public record AiUserPreferences(
        string? CustomPromptInstruction,
        string? PreferredLanguage,
        string? PreferredExplanationDetailLevel,
        string? DefaultTransformationEngine);

    /// <summary>Valores válidos e defaults das preferências novas da issue #322.</summary>
    public static class AiUserPreferenceDefaults
    {
        /// <summary>Bilíngue PT/EN é o padrão do produto (ver <c>.claude/CLAUDE.md</c> §0) — default quando nunca setado.</summary>
        public const string DefaultLanguage = "pt-BR";

        public const string DefaultExplanationDetailLevel = ExplanationDetailLevelConcise;
        public const string ExplanationDetailLevelConcise = "concise";
        public const string ExplanationDetailLevelDetailed = "detailed";

        /// <summary>
        /// Comportamento atual do sistema quando o engine é ambíguo (ver <c>TclExplanationAdapter</c>/
        /// <c>XsltExplanationAdapter</c> — pathway canônico do projeto é o tcl-xsl, TCL primeiro).
        /// </summary>
        public const string DefaultTransformationEngine = TransformationEngineTcl;
        public const string TransformationEngineTcl = "tcl";
        public const string TransformationEngineXslt = "xslt";

        public static readonly IReadOnlySet<string> ValidExplanationDetailLevels =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ExplanationDetailLevelConcise, ExplanationDetailLevelDetailed };

        public static readonly IReadOnlySet<string> ValidTransformationEngines =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { TransformationEngineTcl, TransformationEngineXslt };
    }
}
