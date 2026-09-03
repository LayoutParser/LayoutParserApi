using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Services.Database
{
    /// <summary>
    /// Poda periódica do histórico de longo prazo do pathway de IA por usuário
    /// (<c>tbLpAiUserSessionHistoryEntry</c>) que crescia indefinidamente — gap apontado na issue
    /// #97, resolvido "no mesmo espírito" do <see cref="Transformation.Ai.AiCandidateStoreCleanupBackgroundService"/>
    /// da issue #51: BackgroundService com timer previsível, independente de tráfego, em vez de
    /// varredura oportunista no caminho de escrita (que pagaria por um DELETE a cada gravação de
    /// histórico).
    /// </summary>
    public class AiUserSessionHistoryCleanupBackgroundService : BackgroundService
    {
        private readonly ILogger<AiUserSessionHistoryCleanupBackgroundService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly TimeSpan _intervalo;
        private readonly TimeSpan _retencaoParaLog;

        // ✅ SqlAiUserSessionStore é Scoped (grupo Database) — o BackgroundService é efetivamente
        // Singleton, então não pode capturá-lo direto no construtor (captive dependency). Resolve
        // via IServiceScopeFactory a cada ciclo, mesmo padrão recomendado pela doc do ASP.NET Core
        // para hosted services que dependem de serviços Scoped.
        public AiUserSessionHistoryCleanupBackgroundService(
            ILogger<AiUserSessionHistoryCleanupBackgroundService> logger,
            IServiceScopeFactory scopeFactory,
            IOptions<AiUserSessionHistoryOptions> options)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;

            var minutos = options.Value.CleanupIntervalMinutes;
            _intervalo = TimeSpan.FromMinutes(
                minutos > 0 ? minutos : AiUserSessionHistoryOptions.DefaultCleanupIntervalMinutes);

            var dias = options.Value.HistoryRetentionDays;
            _retencaoParaLog = TimeSpan.FromDays(
                dias > 0 ? dias : AiUserSessionHistoryOptions.DefaultHistoryRetentionDays);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Cede a thread de startup imediatamente, mesmo padrão do cleanup do AiCandidateStore —
            // evita concorrer com warm-up de cache/conexões nos primeiros segundos de vida da API.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _logger.LogInformation(
                "Limpeza do histórico de sessão de IA ativa (retenção {RetencaoDias}d, varredura a cada {IntervaloMinutos}min)",
                _retencaoParaLog.TotalDays, _intervalo.TotalMinutes);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var store = scope.ServiceProvider.GetRequiredService<SqlAiUserSessionStore>();
                    var removidos = await store.PurgeExpiredHistoryAsync(stoppingToken);
                    if (removidos > 0)
                    {
                        _logger.LogInformation(
                            "Limpeza do histórico de sessão de IA removeu {EntradasRemovidas} entrada(s) expirada(s)",
                            removidos);
                    }
                }
                catch (Exception ex)
                {
                    // Degrade: limpeza é manutenção — nunca pode derrubar o host nem interromper o
                    // loop (dotnet-standards.md §Resiliência / §Background work). PurgeExpiredHistoryAsync
                    // já degrada internamente, mas o try/catch aqui cobre também falhas do próprio Delay/loop.
                    _logger.LogWarning(ex, "Falha na varredura de limpeza do histórico de sessão de IA");
                }

                try
                {
                    await Task.Delay(_intervalo, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break; // Host parando.
                }
            }
        }
    }
}
