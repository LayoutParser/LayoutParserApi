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

        /// <summary>
        /// Falha de LEITURA do catálogo não pode virar "catálogo vazio". CachedLayoutService/
        /// LayoutDatabaseService engolem a exceção de SQL e devolvem <c>Success=false</c> — o catch
        /// do retry nunca vê exceção nesse caminho. Antes isso virava <c>SetResult(0)</c>:
        /// warm-up marcado como CONCLUÍDO no estado definitivo "Vazio", com o retry da issue #67
        /// morto justamente no cenário que ele existe para cobrir (blip de SQL).
        /// </summary>
        [Fact]
        public async Task Busca_sem_sucesso_nao_conclui_warmup_e_continua_aquecendo()
        {
            // Refresh não lança (exceção engolida lá dentro), mas a busca subsequente não tem sucesso.
            var fakeLayoutService = new FakeCachedLayoutService(failuresBeforeSuccess: 0, successCount: 0, searchSuccess: false);
            var fakeMapperService = new FakeCachedMapperService(failuresBeforeSuccess: 0);
            var catalogState = new CatalogWarmupState();

            var services = new ServiceCollection();
            services.AddSingleton<ICachedLayoutService>(fakeLayoutService);
            services.AddSingleton<ICachedMapperService>(fakeMapperService);
            await using var provider = services.BuildServiceProvider();

            var delayCalls = 0;
            using var cts = new CancellationTokenSource();
            Task NoDelay(TimeSpan _, CancellationToken __)
            {
                delayCalls++;
                if (delayCalls >= 3) cts.Cancel(); // sem isso o loop (corretamente) tentaria para sempre
                return Task.CompletedTask;
            }

            var sut = new CachePermanentWarmupBackgroundService(
                provider,
                NullLogger<CachePermanentWarmupBackgroundService>.Instance,
                catalogState,
                NoDelay);

            await sut.StartAsync(cts.Token);
            await WaitUntilAsync(() => cts.IsCancellationRequested && fakeLayoutService.RefreshAttempts >= 3, TimeSpan.FromSeconds(5));

            Assert.False(catalogState.Completed);
            Assert.Equal(CatalogWarmupStatus.Aquecendo, catalogState.Status);
            Assert.Equal(-1, catalogState.LayoutCount); // nunca registrou 0 como conclusão
            Assert.True(catalogState.FailedAttempts >= 3);
            Assert.True(fakeLayoutService.RefreshAttempts >= 3); // continuou tentando

            await sut.StopAsync(CancellationToken.None);
        }

        /// <summary>
        /// O outro lado da moeda: busca BEM-SUCEDIDA devolvendo 0 layouts é conclusão de verdade
        /// (o catálogo está mesmo vazio) → estado "Vazio", definitivo, sem retry infinito. Readiness
        /// segue Unhealthy — é o sinal correto para o operador, não um Healthy mentiroso.
        /// </summary>
        [Fact]
        public async Task Catalogo_realmente_vazio_conclui_como_vazio_definitivo()
        {
            var fakeLayoutService = new FakeCachedLayoutService(failuresBeforeSuccess: 0, successCount: 0);
            var fakeMapperService = new FakeCachedMapperService(failuresBeforeSuccess: 0);
            var catalogState = new CatalogWarmupState();

            var services = new ServiceCollection();
            services.AddSingleton<ICachedLayoutService>(fakeLayoutService);
            services.AddSingleton<ICachedMapperService>(fakeMapperService);
            await using var provider = services.BuildServiceProvider();

            var delayCalls = 0;
            Task NoDelay(TimeSpan _, CancellationToken __) { delayCalls++; return Task.CompletedTask; }

            var sut = new CachePermanentWarmupBackgroundService(
                provider,
                NullLogger<CachePermanentWarmupBackgroundService>.Instance,
                catalogState,
                NoDelay);

            using var cts = new CancellationTokenSource();
            await sut.StartAsync(cts.Token);
            await WaitUntilAsync(() => catalogState.Completed, TimeSpan.FromSeconds(5));

            Assert.True(catalogState.Completed);
            Assert.Equal(CatalogWarmupStatus.Vazio, catalogState.Status);
            Assert.Equal(0, catalogState.LayoutCount);
            Assert.Equal(1, fakeLayoutService.RefreshAttempts); // não fica retentando à toa
            Assert.Equal(0, delayCalls);

            await sut.StopAsync(cts.Token);
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
            private readonly bool _searchSuccess;
            public int RefreshAttempts { get; private set; }

            public FakeCachedLayoutService(int failuresBeforeSuccess, int successCount, bool searchSuccess = true)
            {
                _failuresBeforeSuccess = failuresBeforeSuccess;
                _successCount = successCount;
                _searchSuccess = searchSuccess;
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
                // searchSuccess=false simula o que o serviço real faz quando o SQL cai: ENGOLE a
                // exceção e devolve Success=false (não propaga) — "não consegui ler", não "vazio".
                return Task.FromResult(new LayoutSearchResponse
                {
                    Success = _searchSuccess,
                    TotalFound = _searchSuccess ? _successCount : 0,
                    ErrorMessage = _searchSuccess ? "" : "SQL indisponivel (simulado)"
                });
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
