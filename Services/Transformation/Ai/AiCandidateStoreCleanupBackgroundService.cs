using Microsoft.Extensions.Options;

namespace LayoutParserApi.Services.Transformation.Ai
{
    /// <summary>
    /// Poda periódica dos tickets vencidos do <see cref="AiCandidateStore"/> — memória e disco
    /// (issue #51: cada <c>execute-candidates</c> que dispara o pathway IA criava um ticket que
    /// nunca era removido).
    ///
    /// <para><b>Por que BackgroundService e não varredura oportunista no Set/Get:</b> o Set/Get é
    /// caminho de request (o front faz polling do status) e não pode pagar por
    /// <c>EnumerateFiles</c> do diretório inteiro; além disso, varredura oportunista só roda quando
    /// há tráfego — numa instância ociosa o lixo do dia anterior ficaria em disco indefinidamente.
    /// O timer é previsível e independe de tráfego. O custo é um ciclo de vida novo, mas o projeto
    /// já tem esse padrão (<c>CachePermanentWarmupBackgroundService</c>,
    /// <c>LayoutValidationBackgroundService</c>). Como complemento O(1) e sem I/O, o próprio
    /// <c>Get</c> descarta a entrada vencida da memória entre duas varreduras.</para>
    /// </summary>
    public class AiCandidateStoreCleanupBackgroundService : BackgroundService
    {
        private readonly ILogger<AiCandidateStoreCleanupBackgroundService> _logger;
        private readonly AiCandidateStore _store;
        private readonly TimeSpan _intervalo;

        public AiCandidateStoreCleanupBackgroundService(
            ILogger<AiCandidateStoreCleanupBackgroundService> logger,
            AiCandidateStore store,
            IOptions<AiTransformationCandidateOptions> options)
        {
            _logger = logger;
            _store = store;

            var minutos = options.Value.CleanupIntervalMinutes;
            _intervalo = TimeSpan.FromMinutes(
                minutos > 0 ? minutos : AiTransformationCandidateOptions.DefaultCleanupIntervalMinutes);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Cede a thread de startup imediatamente (RemoveExpired é I/O síncrono) e evita
            // concorrer com o warm-up de cache nos primeiros segundos de vida da API.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _logger.LogInformation(
                "Limpeza da store do pathway IA ativa (TTL {TtlHoras}h, varredura a cada {IntervaloMinutos}min)",
                _store.Ttl.TotalHours, _intervalo.TotalMinutes);

            while (!stoppingToken.IsCancellationRequested)
            {
                // Primeira varredura já no start: o TTL é absoluto por escrita, então o que sobrou
                // de execuções anteriores da API entra vencido e é podado sem esperar um ciclo.
                try
                {
                    var resultado = _store.RemoveExpired();
                    if (resultado.Total > 0)
                    {
                        _logger.LogInformation(
                            "Limpeza da store do pathway IA removeu {TicketsEmMemoria} ticket(s) da memória e {ArquivosEmDisco} arquivo(s) do disco",
                            resultado.TicketsEmMemoria, resultado.ArquivosEmDisco);
                    }
                }
                catch (Exception ex)
                {
                    // Degrade: limpeza é manutenção — nunca pode derrubar o host nem interromper o
                    // loop (dotnet-standards.md §Resiliência / §Background work).
                    _logger.LogWarning(ex, "Falha na varredura de limpeza da store do pathway IA");
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
