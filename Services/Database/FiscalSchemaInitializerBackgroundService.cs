using LayoutParserApi.Services.Interfaces;

namespace LayoutParserApi.Services.Database
{
    /// <summary>
    /// Dispara <see cref="IFiscalSchemaInitializer"/> uma vez, em segundo plano, logo após o app subir
    /// — mesmo padrão de <see cref="CachePermanentWarmupBackgroundService"/> (não bloquear o report de
    /// "Running" ao Service Control Manager do Windows enquanto aguarda o SQL Server responder).
    /// </summary>
    /// <remarks>
    /// Diferente do warm-up de cache, esta inicialização não tem retry em loop: o schema DDL é
    /// idempotente e cada store mantém seu próprio <c>EnsureSchemaAsync</c> lazy como safety net por
    /// requisição — se esta tentativa única falhar (SQL fora do ar no exato momento do startup), o
    /// primeiro request real a cada store ainda cria a tabela sob demanda, só sem a garantia de ordem
    /// entre stores até o SQL voltar (mesmo comportamento pré-existente, apenas sem regressão).
    /// </remarks>
    public sealed class FiscalSchemaInitializerBackgroundService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<FiscalSchemaInitializerBackgroundService> _logger;

        public FiscalSchemaInitializerBackgroundService(
            IServiceProvider serviceProvider,
            ILogger<FiscalSchemaInitializerBackgroundService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var initializer = scope.ServiceProvider.GetRequiredService<IFiscalSchemaInitializer>();
                await initializer.InitializeAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha inesperada ao inicializar o schema fiscal/identidade em background — stores individuais seguem com EnsureSchemaAsync lazy como safety net.");
            }
        }
    }
}
