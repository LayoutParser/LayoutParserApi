using LayoutParserApi.Models.Database;
using LayoutParserApi.Models.Entities;
using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.Health;
using LayoutParserApi.Services.Interfaces;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Database
{
    /// <summary>
    /// Issue #67: SQL indisponível numa tentativa isolada de warm-up não pode travar
    /// <see cref="CatalogWarmupState.LayoutCount"/> em 0 (e a readiness em Unhealthy) para
    /// sempre. Estes testes provam que <see cref="CachePermanentWarmupBackgroundService"/>
    /// tenta de novo (com backoff) até conseguir, sem precisar de restart externo.
    /// </summary>
    public class CachePermanentWarmupBackgroundServiceRetryTests
    {
        /// <summary>
        /// Falha SQL (banco indisponível) na 1ª tentativa, sucede na 2ª — o cenário exato do
        /// critério de aceite da issue.
        /// </summary>
        [Fact]
        public async Task Falha_na_primeira_tentativa_recupera_na_segunda_sem_restart()
        {
            var fakeLayoutService = new FakeCachedLayoutService(failuresBeforeSuccess: 1, successCount: 10);
            var fakeMapperService = new FakeCachedMapperService(failuresBeforeSuccess: 0);
            var catalogState = new CatalogWarmupState();

            var services = new ServiceCollection();
            services.AddSingleton<ICachedLayoutService>(fakeLayoutService);
            services.AddSingleton<ICachedMapperService>(fakeMapperService);
            await using var provider = services.BuildServiceProvider();

            // Delay "instantâneo": prova o retry sem esperar o backoff real em segundos.
            var delayCalls = 0;
            Task NoDelay(TimeSpan _, CancellationToken __) { delayCalls++; return Task.CompletedTask; }

            var sut = new CachePermanentWarmupBackgroundService(
                provider,
                NullLogger<CachePermanentWarmupBackgroundService>.Instance,
                catalogState,
                NoDelay);

            using var cts = new CancellationTokenSource();
            await sut.StartAsync(cts.Token);

            // BackgroundService.ExecuteAsync roda em background (Task interno); aguarda a
            // conclusão explicitamente em vez de confiar em timing.
            await WaitUntilAsync(() => catalogState.Completed, TimeSpan.FromSeconds(5));

            Assert.True(catalogState.Completed);
            Assert.Equal(10, catalogState.LayoutCount);
            Assert.Equal(2, fakeLayoutService.RefreshAttempts); // 1 falha + 1 sucesso
            Assert.True(delayCalls >= 1); // esperou antes de tentar de novo

            await sut.StopAsync(cts.Token);
        }

        /// <summary>
        /// Enquanto o warm-up ainda está tentando (nenhuma tentativa teve sucesso ainda),
        /// <see cref="CatalogWarmupState"/> permanece "não concluído" — nunca registra 0 de forma
        /// definitiva por causa de uma falha isolada que o retry ainda vai corrigir sozinho.
        /// </summary>
        [Fact]
        public async Task Enquanto_todas_as_tentativas_falham_estado_permanece_nao_concluido()
        {
            // Nunca sucede (simula SQL indisponível de forma persistente nesta janela).
            var fakeLayoutService = new FakeCachedLayoutService(failuresBeforeSuccess: int.MaxValue, successCount: 5);
            var fakeMapperService = new FakeCachedMapperService(failuresBeforeSuccess: 0);
            var catalogState = new CatalogWarmupState();

            var services = new ServiceCollection();
            services.AddSingleton<ICachedLayoutService>(fakeLayoutService);
            services.AddSingleton<ICachedMapperService>(fakeMapperService);
            await using var provider = services.BuildServiceProvider();

            // O fake de delay cancela o CTS após algumas tentativas — sem isso o loop
            // (corretamente) tentaria para sempre, o que é o comportamento de produção, mas
            // travaria o teste. stoppingToken.ThrowIfCancellationRequested() no topo do loop de
            // produção garante que o cancelamento é respeitado independente do delay usado.
            var delayCalls = 0;
            using var cts = new CancellationTokenSource();
            Task NoDelay(TimeSpan _, CancellationToken ct)
            {
                delayCalls++;
                if (delayCalls >= 5) cts.Cancel();
                return Task.CompletedTask;
            }

            var sut = new CachePermanentWarmupBackgroundService(
                provider,
                NullLogger<CachePermanentWarmupBackgroundService>.Instance,
                catalogState,
                NoDelay);

            await sut.StartAsync(cts.Token);

            // Dá tempo para várias tentativas ocorrerem (todas falhando) e o cancelamento encerrar o loop.
            await WaitUntilAsync(() => cts.IsCancellationRequested && fakeLayoutService.RefreshAttempts >= 5, TimeSpan.FromSeconds(5));

            Assert.False(catalogState.Completed);
            Assert.Equal(-1, catalogState.LayoutCount);
            Assert.True(fakeLayoutService.RefreshAttempts >= 5);

            await sut.StopAsync(CancellationToken.None);
        }

        private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!condition() && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }
        }

        /// <summary>Fake do cache de layouts: falha N vezes seguidas antes de suceder.</summary>
        private sealed class FakeCachedLayoutService : ICachedLayoutService
        {
            private readonly int _failuresBeforeSuccess;
            private readonly int _successCount;
            public int RefreshAttempts { get; private set; }

            public FakeCachedLayoutService(int failuresBeforeSuccess, int successCount)
            {
                _failuresBeforeSuccess = failuresBeforeSuccess;
                _successCount = successCount;
            }

            public Task RefreshCacheFromDatabaseAsync()
            {
                RefreshAttempts++;
                if (RefreshAttempts <= _failuresBeforeSuccess)
                {
                    throw new InvalidOperationException("SQL indisponivel (simulado)");
                }
                return Task.CompletedTask;
            }

            public Task<LayoutSearchResponse> SearchLayoutsAsync(LayoutSearchRequest request)
            {
                return Task.FromResult(new LayoutSearchResponse { Success = true, TotalFound = _successCount });
            }

            public Task<LayoutRecord?> GetLayoutByIdAsync(int id) => Task.FromResult<LayoutRecord?>(null);
            public Task<LayoutRecord?> GetLayoutByGuidAsync(string layoutGuid) => Task.FromResult<LayoutRecord?>(null);
            public Task ClearCacheAsync() => Task.CompletedTask;
            public ILayoutDatabaseService GetLayoutDatabaseService() => null!;
        }

        /// <summary>Fake do cache de mapeadores: falha N vezes seguidas antes de suceder.</summary>
        private sealed class FakeCachedMapperService : ICachedMapperService
        {
            private readonly int _failuresBeforeSuccess;
            public int RefreshAttempts { get; private set; }

            public FakeCachedMapperService(int failuresBeforeSuccess)
            {
                _failuresBeforeSuccess = failuresBeforeSuccess;
            }

            public Task RefreshCacheFromDatabaseAsync()
            {
                RefreshAttempts++;
                if (RefreshAttempts <= _failuresBeforeSuccess)
                {
                    throw new InvalidOperationException("SQL indisponivel (simulado)");
                }
                return Task.CompletedTask;
            }

            public Task<List<Mapper>> GetAllMappersAsync() => Task.FromResult(new List<Mapper>());
            public Task<List<Mapper>> GetMappersByInputLayoutGuidAsync(string inputLayoutGuid) => Task.FromResult(new List<Mapper>());
            public Task<List<Mapper>> GetMappersByTargetLayoutGuidAsync(string targetLayoutGuid) => Task.FromResult(new List<Mapper>());
        }
    }
}
