using Microsoft.Extensions.Options;

using LayoutParserApi.Services.Interfaces;

namespace LayoutParserApi.Services.Fiscal
{
    /// <summary>
    /// Purga periódica do histórico de análises fiscais (issue #366): apaga arquivos + linha após o TTL
    /// (default 90 dias) e varre diretórios órfãos. Mesmo padrão de
    /// <see cref="Database.AiUserSessionHistoryCleanupBackgroundService"/>: o serviço de purga é Scoped,
    /// resolvido por ciclo via <see cref="IServiceScopeFactory"/>; falha nunca derruba o host.
    /// </summary>
    public sealed class FiscalAnalysisPurgeBackgroundService : BackgroundService
    {
        private readonly ILogger<FiscalAnalysisPurgeBackgroundService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly TimeSpan _intervalo;

        public FiscalAnalysisPurgeBackgroundService(
            ILogger<FiscalAnalysisPurgeBackgroundService> logger,
            IServiceScopeFactory scopeFactory,
            IOptions<FiscalAnalysisHistoryOptions> options)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            var minutos = options.Value.CleanupIntervalMinutes;
            _intervalo = TimeSpan.FromMinutes(minutos > 0 ? minutos : FiscalAnalysisHistoryOptions.DefaultCleanupIntervalMinutes);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Cede o startup: não concorre com warm-up de cache/conexões.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _logger.LogInformation("Purga do histórico de análises fiscais ativa (varredura a cada {IntervaloMinutos}min)", _intervalo.TotalMinutes);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var service = scope.ServiceProvider.GetRequiredService<IFiscalAnalysisService>();
                    var purgadas = await service.PurgeExpiredAsync(stoppingToken);
                    if (purgadas > 0)
                        _logger.LogInformation("Purga do histórico de análises removeu {AnalisesPurgadas} análise(s) expirada(s)", purgadas);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Manutenção: nunca derruba o host nem interrompe o loop.
                    _logger.LogWarning(ex, "Falha na varredura de purga do histórico de análises fiscais");
                }

                try
                {
                    await Task.Delay(_intervalo, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
