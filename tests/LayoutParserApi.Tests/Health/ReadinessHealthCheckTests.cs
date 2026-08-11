using LayoutParserApi.Services.Health;
using LayoutParserApi.Services.Transformation.LowCode;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Tests.Health
{
    /// <summary>
    /// Trava a semântica de readiness do P1.3: dependência ESSENCIAL fora → Unhealthy (503);
    /// dependência OPCIONAL fora → Degraded (200, resiliência é princípio do projeto).
    /// </summary>
    public class ReadinessHealthCheckTests
    {
        // ---- Catálogo: gate duro da readiness ---------------------------------------------------

        [Fact]
        public async Task Catalogo_vazio_apos_warmup_e_unhealthy()
        {
            var state = new CatalogWarmupState();
            state.SetResult(0);

            var result = await new CatalogHealthCheck(state).CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Unhealthy, result.Status);
        }

        [Fact]
        public async Task Catalogo_ainda_nao_concluido_e_unhealthy()
        {
            // Warm-up não rodou (Completed=false) → instância não está pronta.
            var result = await new CatalogHealthCheck(new CatalogWarmupState()).CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Unhealthy, result.Status);
        }

        [Fact]
        public async Task Catalogo_populado_e_healthy()
        {
            var state = new CatalogWarmupState();
            state.SetResult(42);

            var result = await new CatalogHealthCheck(state).CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Healthy, result.Status);
        }

        // ---- Dependências opcionais: Degraded, não Unhealthy ------------------------------------

        [Fact]
        public async Task Redis_ausente_e_degraded_nao_unhealthy()
        {
            // Injeção opcional nula = Redis fora do container.
            var result = await new RedisHealthCheck(null).CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Degraded, result.Status);
        }

        [Fact]
        public async Task Runner_sem_path_e_degraded()
        {
            var options = Options.Create(new LowCodeRunnerOptions { RunnerPath = "" });

            var result = await new LowCodeRunnerHealthCheck(options).CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Degraded, result.Status);
        }

        // ---- Agregação: dependência forçada a falhar → readiness responde 503 -------------------

        /// <summary>
        /// "Com uma dependência forçada a falhar, /health/ready responde 503." O 503 vem do
        /// mapeamento <c>Unhealthy → 503</c> configurado em Program.cs (MapHealthChecks
        /// ResultStatusCodes); aqui provamos a metade que decide isso: o agregado das sondas "ready"
        /// vira <b>Unhealthy</b> quando o catálogo está vazio.
        /// </summary>
        [Fact]
        public async Task Readiness_com_catalogo_vazio_agrega_unhealthy()
        {
            var services = new ServiceCollection();
            services.AddLogging();

            var state = new CatalogWarmupState();
            state.SetResult(0); // dependência essencial forçada a falhar
            services.AddSingleton(state);

            services.AddHealthChecks()
                .Add(new HealthCheckRegistration("catalog",
                    sp => new CatalogHealthCheck(sp.GetRequiredService<CatalogWarmupState>()),
                    HealthStatus.Unhealthy, new[] { "ready" }))
                // Redis Degraded no mesmo lote não pode "puxar" o agregado para pior nem mascarar.
                .Add(new HealthCheckRegistration("redis",
                    _ => new RedisHealthCheck(null),
                    HealthStatus.Unhealthy, new[] { "ready" }));

            await using var provider = services.BuildServiceProvider();
            var svc = provider.GetRequiredService<HealthCheckService>();

            var report = await svc.CheckHealthAsync(r => r.Tags.Contains("ready"));

            Assert.Equal(HealthStatus.Unhealthy, report.Status);
        }

        [Fact]
        public async Task Readiness_so_com_opcionais_degradadas_nao_e_unhealthy()
        {
            var services = new ServiceCollection();
            services.AddLogging();

            var state = new CatalogWarmupState();
            state.SetResult(7); // catálogo OK
            services.AddSingleton(state);

            services.AddHealthChecks()
                .Add(new HealthCheckRegistration("catalog",
                    sp => new CatalogHealthCheck(sp.GetRequiredService<CatalogWarmupState>()),
                    HealthStatus.Unhealthy, new[] { "ready" }))
                .Add(new HealthCheckRegistration("redis",
                    _ => new RedisHealthCheck(null),
                    HealthStatus.Unhealthy, new[] { "ready" }));

            await using var provider = services.BuildServiceProvider();
            var report = await provider.GetRequiredService<HealthCheckService>()
                .CheckHealthAsync(r => r.Tags.Contains("ready"));

            // Redis Degraded + catálogo Healthy → Degraded (200), NUNCA Unhealthy (503).
            Assert.NotEqual(HealthStatus.Unhealthy, report.Status);
        }
    }
}
