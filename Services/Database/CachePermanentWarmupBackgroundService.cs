using LayoutParserApi.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LayoutParserApi.Services.Database
{
    /// <summary>
    /// Background Service responsável por popular o cache permanente de layouts e mapeadores
    /// a partir do banco de dados, sem bloquear o startup da aplicação.
    ///
    /// Motivação: antes, essa população ocorria de forma síncrona em Program.cs, ANTES de
    /// app.Run(), bloqueando o report de "Running" ao Service Control Manager do Windows.
    /// Em ambientes com SQL Server lento/indisponível isso ultrapassava o timeout padrão do
    /// SCM (~30s) e derrubava o Start-Service no deploy. Movendo para um IHostedService que
    /// roda após o app subir, o startup fica desacoplado da disponibilidade do SQL.
    /// </summary>
    public class CachePermanentWarmupBackgroundService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<CachePermanentWarmupBackgroundService> _logger;

        public CachePermanentWarmupBackgroundService(
            IServiceProvider serviceProvider,
            ILogger<CachePermanentWarmupBackgroundService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Executa uma única vez, em segundo plano, após o app já estar respondendo requisições.
            // Qualquer falha aqui é logada e degradada: o cache fica vazio (ou parcial) até o
            // próximo refresh manual/reconexão, o que já é tolerado pelo smoke test do CI
            // (aceita 404 em catálogo vazio).
            try
            {
                _logger.LogInformation("Iniciando populacao em background do cache permanente de layouts e mapeadores");

                using var scope = _serviceProvider.CreateScope();
                var cachedLayoutService = scope.ServiceProvider.GetRequiredService<ICachedLayoutService>();
                var cachedMapperService = scope.ServiceProvider.GetRequiredService<ICachedMapperService>();

                await RefreshLayoutCacheAsync(cachedLayoutService, stoppingToken);
                await RefreshMapperCacheAsync(cachedMapperService, stoppingToken);

                _logger.LogInformation("Populacao em background do cache permanente concluida");
            }
            catch (OperationCanceledException)
            {
                // Serviço sendo parado durante o warm-up — não é erro, apenas encerra.
                _logger.LogWarning("Populacao do cache permanente cancelada (aplicacao em encerramento)");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro inesperado ao popular cache permanente. A aplicacao continua, mas o cache pode estar vazio ou parcial");
            }
        }

        /// <summary>
        /// Popula o cache de layouts a partir do banco. Falha isolada não impede o cache de mapeadores.
        /// </summary>
        private async Task RefreshLayoutCacheAsync(ICachedLayoutService cachedLayoutService, CancellationToken stoppingToken)
        {
            try
            {
                _logger.LogInformation("Populando cache de layouts em background...");
                await cachedLayoutService.RefreshCacheFromDatabaseAsync();
                _logger.LogInformation("Cache de layouts populado com sucesso em background");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao popular cache de layouts em background (SQL indisponivel/lento?). Cache pode ficar vazio ate proximo refresh");
            }
        }

        /// <summary>
        /// Popula o cache de mapeadores a partir do banco. Falha isolada não impede o cache de layouts.
        /// </summary>
        private async Task RefreshMapperCacheAsync(ICachedMapperService cachedMapperService, CancellationToken stoppingToken)
        {
            try
            {
                _logger.LogInformation("Populando cache de mapeadores em background...");
                await cachedMapperService.RefreshCacheFromDatabaseAsync();

                var allMappers = await cachedMapperService.GetAllMappersAsync();
                _logger.LogInformation("Cache de mapeadores populado com sucesso em background: {Count} mapeadores disponiveis", allMappers?.Count ?? 0);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao popular cache de mapeadores em background (SQL indisponivel/lento?). Cache pode ficar vazio ate proximo refresh");
            }
        }
    }
}
