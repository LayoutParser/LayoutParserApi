using LayoutParserApi.Services.Interfaces;

using Microsoft.Data.SqlClient;

namespace LayoutParserApi.Services.Database
{
    /// <summary>
    /// Implementação de <see cref="IFiscalSchemaInitializer"/> — chama, em ORDEM, o
    /// <c>EnsureSchemaAsync</c> (agora <c>internal</c>) de cada store fiscal/identidade, uma vez no
    /// startup, em vez de depender de qual store uma requisição real bate primeiro.
    /// </summary>
    /// <remarks>
    /// Grafo de dependência de FK mapeado (issue da instabilidade de deploy, PR #310):
    /// <list type="bullet">
    /// <item>Banco <c>Database:*</c> (ConnectUS_Macgyver/Sysmiddle):
    ///   <see cref="SqlFiscalPackageStore"/> (tbFiscalProject, tbFiscalMappingPackage,
    ///   tbFiscalMappingPackageRevision, tbPackageArtifact) →
    ///   <see cref="SqlMappingDraftStore"/> (tbMappingDraft.PackageId/RevisionId referenciam as
    ///   tabelas acima) →
    ///   <see cref="SqlMappingReleaseStore"/> (tbMappingRelease.DraftId referencia tbMappingDraft).
    /// </item>
    /// <item>Banco <c>IdentityDatabase:*</c> (físicamente outro servidor — sem relação de FK com o
    ///   banco acima, SQL Server não suporta FK entre bancos):
    ///   <see cref="SqlIdentityWorkspaceStore"/> e <see cref="SqlAiUserSessionStore"/>, cada um
    ///   autossuficiente (FKs só apontam para tabelas do próprio bloco) — ordem entre os dois é
    ///   irrelevante.
    /// </item>
    /// </list>
    /// Adicionalmente, as colunas <c>WorkspaceId</c> de <c>tbFiscalProject</c>/
    /// <c>tbFiscalMappingPackage</c>/<c>tbMappingDraft</c>/<c>tbMappingRelease</c> tinham
    /// <c>REFERENCES dbo.tbFiscalWorkspace(WorkspaceId)</c> — uma FK que NUNCA funcionou, em
    /// nenhuma ordem: <c>tbFiscalWorkspace</c> não existe no banco <c>Database:*</c> (o workspace
    /// fiscal real, <c>tbLpFiscalWorkspace</c>, mora no banco <c>IdentityDatabase:*</c>, fisicamente
    /// outro servidor — FK cross-database é impossível no SQL Server). Essa FK foi removida das 4
    /// tabelas (coluna preservada, sem constraint); a validação de que o WorkspaceId existe é
    /// responsabilidade da camada de aplicação via <see cref="IIdentityWorkspaceStore"/>.
    /// </remarks>
    public sealed class FiscalSchemaInitializer : IFiscalSchemaInitializer
    {
        private readonly ILogger<FiscalSchemaInitializer> _logger;
        private readonly string _fiscalConnectionString;
        private readonly string _identityConnectionString;

        public FiscalSchemaInitializer(ILogger<FiscalSchemaInitializer> logger, IConfiguration configuration)
        {
            _logger = logger;

            var fiscalServer = configuration["Database:Server"];
            var fiscalDatabase = configuration["Database:Database"];
            var fiscalUserId = configuration["Database:UserId"];
            var fiscalPassword = configuration["Database:Password"];
            _fiscalConnectionString =
                $"Server={fiscalServer};Database={fiscalDatabase};User Id={fiscalUserId};Password={fiscalPassword};TrustServerCertificate=True;";

            var identityServer = configuration["IdentityDatabase:Server"];
            var identityDatabase = configuration["IdentityDatabase:Database"];
            var identityUserId = configuration["IdentityDatabase:UserId"];
            var identityPassword = configuration["IdentityDatabase:Password"];
            _identityConnectionString =
                $"Server={identityServer};Database={identityDatabase};User Id={identityUserId};Password={identityPassword};TrustServerCertificate=True;";
        }

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            await InitializeFiscalSchemaAsync(cancellationToken);
            await InitializeIdentitySchemaAsync(cancellationToken);
        }

        // Ordem impositiva: FiscalPackage → MappingDraft → MappingRelease (ver grafo no <remarks>).
        private async Task InitializeFiscalSchemaAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var connection = new SqlConnection(_fiscalConnectionString);
                await connection.OpenAsync(cancellationToken);

                await SqlFiscalPackageStore.EnsureSchemaAsync(connection, cancellationToken);
                await SqlMappingDraftStore.EnsureSchemaAsync(connection, cancellationToken);
                await SqlMappingReleaseStore.EnsureSchemaAsync(connection, cancellationToken);

                _logger.LogInformation("Schema fiscal (Database:*) inicializado com sucesso no startup, em ordem de dependência de FK.");
            }
            catch (Exception ex)
            {
                // Degrada graciosamente: cada store mantém seu próprio EnsureSchemaAsync lazy como
                // safety net por requisição — não derruba o startup da API se o SQL estiver fora do ar.
                _logger.LogWarning(ex, "Falha ao inicializar o schema fiscal (Database:*) no startup — cada store tentará novamente sob demanda.");
            }
        }

        // Autossuficientes, sem dependência de ordem entre si.
        private async Task InitializeIdentitySchemaAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var connection = new SqlConnection(_identityConnectionString);
                await connection.OpenAsync(cancellationToken);

                await SqlIdentityWorkspaceStore.EnsureSchemaAsync(connection, cancellationToken);
                await SqlAiUserSessionStore.EnsureSchemaAsync(connection, cancellationToken);

                _logger.LogInformation("Schema de identidade (IdentityDatabase:*) inicializado com sucesso no startup.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao inicializar o schema de identidade (IdentityDatabase:*) no startup — cada store tentará novamente sob demanda.");
            }
        }
    }
}
