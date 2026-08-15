using LayoutParserApi.Models.Database;
using LayoutParserApi.Services.Health;
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
        // ✅ Issue #67: backoff progressivo entre tentativas de warm-up (5s, 15s, 30s, depois
        // fixo em 60s). Sem teto de tentativas — é warm-up de cache, não algo crítico de request;
        // desistir permanentemente reintroduz o bug original (readiness Unhealthy até restart
        // manual). O loop só para com sucesso ou com o processo sendo encerrado (cancellation).
        private static readonly TimeSpan[] RetryDelays =
        {
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60)
        };

        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<CachePermanentWarmupBackgroundService> _logger;
        private readonly CatalogWarmupState _catalogState;

        // Injetável só para teste: evita esperar o backoff real (segundos/minutos) na suíte.
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;

        public CachePermanentWarmupBackgroundService(
            IServiceProvider serviceProvider,
            ILogger<CachePermanentWarmupBackgroundService> logger,
            CatalogWarmupState catalogState)
            : this(serviceProvider, logger, catalogState, Task.Delay)
        {
        }

        public CachePermanentWarmupBackgroundService(
            IServiceProvider serviceProvider,
            ILogger<CachePermanentWarmupBackgroundService> logger,
            CatalogWarmupState catalogState,
            Func<TimeSpan, CancellationToken, Task> delay)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _catalogState = catalogState;
            _delay = delay;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Roda em segundo plano, após o app já estar respondendo requisições, com retry até
            // conseguir. Antes, uma única tentativa falha (ex.: blip transitório de SQL) travava
            // CatalogWarmupState.LayoutCount em 0 para sempre e o /health/ready ficava Unhealthy
            // permanentemente — só recuperava com restart manual do processo.
            try
            {
                _logger.LogInformation("Iniciando populacao em background do cache permanente de layouts e mapeadores");

                await RefreshLayoutCacheWithRetryAsync(stoppingToken);
                await RefreshMapperCacheWithRetryAsync(stoppingToken);

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
        /// Popula o cache de layouts a partir do banco, tentando de novo com backoff progressivo
        /// enquanto falhar. Só desiste em caso de cancelamento (shutdown).
        /// </summary>
        private async Task RefreshLayoutCacheWithRetryAsync(CancellationToken stoppingToken)
        {
            var attempt = 0;
            while (true)
            {
                stoppingToken.ThrowIfCancellationRequested();
                attempt++;
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var cachedLayoutService = scope.ServiceProvider.GetRequiredService<ICachedLayoutService>();

                    _logger.LogInformation("Populando cache de layouts em background (tentativa {Attempt})...", attempt);
                    await cachedLayoutService.RefreshCacheFromDatabaseAsync();

                    // ✅ P1.3: registra a contagem EFETIVA para a sonda de readiness. Lê o catálogo
                    // recém-populado (cache "all" se houver Redis, senão banco) — reflete o que a API
                    // consegue servir, não o tamanho de um cache que pode nem existir sem Redis.
                    var todos = await cachedLayoutService.SearchLayoutsAsync(new LayoutSearchRequest { SearchTerm = "" });

                    // ✅ "Nao consegui LER o catalogo" != "o catalogo esta vazio". CachedLayoutService/
                    // LayoutDatabaseService ENGOLEM a exceção de SQL e devolvem Success=false em vez de
                    // propagar — ou seja, o catch abaixo NUNCA dispara num blip de SQL. Antes,
                    // Success=false virava contagem 0 e SetResult(0) marcava o warm-up como CONCLUÍDO:
                    // estado "Vazio" (definitivo) + `return` matavam o retry da issue #67 justamente no
                    // cenário que ele existe para cobrir. Agora conta como tentativa falha: o estado
                    // fica "Aquecendo" e o backoff tenta de novo até o SQL voltar.
                    if (!todos.Success)
                    {
                        var delayLeitura = GetRetryDelay(attempt);
                        _catalogState.RegisterFailedAttempt("BuscaDeLayoutsSemSucesso");
                        _logger.LogWarning(
                            "Busca do catalogo apos o refresh nao teve sucesso (tentativa {Attempt}): {Erro}. Warm-up segue AQUECENDO; nova tentativa em {DelaySeconds}s",
                            attempt, todos.ErrorMessage, delayLeitura.TotalSeconds);
                        await _delay(delayLeitura, stoppingToken);
                        continue;
                    }

                    var layoutCount = todos.TotalFound;

                    // Contagem 0 com busca BEM-SUCEDIDA é conclusão de verdade: o catálogo está mesmo
                    // vazio. Registra como concluído (estado "Vazio") — readiness Unhealthy definitivo,
                    // que é o sinal correto para o operador, e não fica retentando à toa.
                    if (layoutCount <= 0)
                        _logger.LogError("Warm-up concluiu mas o catalogo veio VAZIO (0 layouts) na tentativa {Attempt} — readiness ficara Unhealthy ate haver layouts", attempt);
                    else
                        _logger.LogInformation("Cache de layouts populado com sucesso em background: {Count} layouts (tentativa {Attempt})", layoutCount, attempt);

                    _catalogState.SetResult(layoutCount);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var delay = GetRetryDelay(attempt);
                    _catalogState.RegisterFailedAttempt(ex.GetType().Name);
                    _logger.LogWarning(ex, "Erro ao popular cache de layouts em background (SQL indisponivel/lento? tentativa {Attempt}). Nova tentativa em {DelaySeconds}s", attempt, delay.TotalSeconds);

                    // Não registra 0 aqui de propósito: registrar travaria a readiness em Unhealthy
                    // "definitivo" mesmo enquanto o retry ainda está em andamento e prestes a
                    // recuperar sozinho — CatalogWarmupState.Completed=false até o 1º sucesso é o
                    // sinal correto (readiness segue Unhealthy do mesmo jeito, mas reflete "ainda
                    // não pronto", não "falhou de vez").
                    await _delay(delay, stoppingToken);
                }
            }
        }

        /// <summary>
        /// Popula o cache de mapeadores a partir do banco, com o mesmo retry do cache de layouts.
        /// Falha isolada não impede (nem é impedida por) o cache de layouts, que já terminou antes.
        /// </summary>
        private async Task RefreshMapperCacheWithRetryAsync(CancellationToken stoppingToken)
        {
            var attempt = 0;
            while (true)
            {
                stoppingToken.ThrowIfCancellationRequested();
                attempt++;
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var cachedMapperService = scope.ServiceProvider.GetRequiredService<ICachedMapperService>();

                    _logger.LogInformation("Populando cache de mapeadores em background (tentativa {Attempt})...", attempt);
                    await cachedMapperService.RefreshCacheFromDatabaseAsync();

                    var allMappers = await cachedMapperService.GetAllMappersAsync();
                    _logger.LogInformation("Cache de mapeadores populado com sucesso em background: {Count} mapeadores disponiveis (tentativa {Attempt})", allMappers?.Count ?? 0, attempt);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var delay = GetRetryDelay(attempt);
                    _logger.LogWarning(ex, "Erro ao popular cache de mapeadores em background (SQL indisponivel/lento? tentativa {Attempt}). Nova tentativa em {DelaySeconds}s", attempt, delay.TotalSeconds);
                    await _delay(delay, stoppingToken);
                }
            }
        }

        /// <summary>Backoff progressivo: 5s, 15s, 30s, depois fixo em 60s.</summary>
        private static TimeSpan GetRetryDelay(int attempt)
        {
            var index = attempt - 1;
            return index < RetryDelays.Length ? RetryDelays[index] : RetryDelays[^1];
        }
    }
}
