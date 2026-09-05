namespace LayoutParserApi.Services.Interfaces
{
    /// <summary>
    /// Garante, uma única vez no startup (fora do request-path), que TODAS as tabelas fiscais/de
    /// identidade sejam criadas na ORDEM correta de dependência de foreign key — elimina a corrida
    /// entre stores que causava o 503 "MappingDraftStore falhou: FK ... references invalid table"
    /// (PR #310): cada store fazia DDL lazy na primeira requisição que o exercitasse, sem garantia
    /// de que a tabela referenciada por FK já existisse.
    /// </summary>
    public interface IFiscalSchemaInitializer
    {
        /// <summary>
        /// Inicializa o schema fiscal (banco <c>Database:*</c>, ConnectUS_Macgyver/Sysmiddle) e o
        /// schema de identidade (banco <c>IdentityDatabase:*</c>) em ordem topológica. Degrada
        /// graciosamente: se o SQL estiver indisponível no startup, loga e retorna — cada store
        /// continua com seu próprio <c>EnsureSchemaAsync</c> lazy como safety net por requisição.
        /// </summary>
        Task InitializeAsync(CancellationToken cancellationToken);
    }
}
