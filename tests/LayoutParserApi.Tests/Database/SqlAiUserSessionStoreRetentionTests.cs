using LayoutParserApi.Services.Database;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Tests.Database
{
    /// <summary>
    /// Issue #97 (gap de TTL/retenção) — <c>tbLpAiUserSessionHistoryEntry</c> não tinha nenhuma
    /// expiração, "no mesmo espírito" do TTL já aplicado ao <c>AiCandidateStore</c> na issue #51
    /// (ver <c>AiCandidateStoreTtlTests</c>). Como a store é SQL (não há banco disponível no ambiente
    /// de teste), os testes cobrem o que é determinístico sem rede: resolução do TTL efetivo com
    /// fallback, resolução via DI (sem captive dependency do BackgroundService sobre um serviço
    /// Scoped) e degradação graciosa de <c>PurgeExpiredHistoryAsync</c> quando o SQL está
    /// inacessível — mesmo padrão de resiliência já coberto pelo resto da store.
    /// </summary>
    public class SqlAiUserSessionStoreRetentionTests
    {
        [Fact]
        public void HistoryRetention_nao_configurado_ou_invalido_cai_no_default()
        {
            var padrao = CriarStore(AiUserSessionHistoryOptions.DefaultHistoryRetentionDays);
            var invalido = CriarStore(0);
            var negativo = CriarStore(-5);

            Assert.Equal(TimeSpan.FromDays(180), padrao.HistoryRetention);
            Assert.Equal(padrao.HistoryRetention, invalido.HistoryRetention);
            Assert.Equal(padrao.HistoryRetention, negativo.HistoryRetention);
        }

        [Fact]
        public void HistoryRetention_configurado_e_respeitado()
        {
            var store = CriarStore(30);

            Assert.Equal(TimeSpan.FromDays(30), store.HistoryRetention);
        }

        [Fact]
        public async Task PurgeExpiredHistoryAsync_degrada_graciosamente_quando_sql_inacessivel()
        {
            // Mesmo princípio de resiliência do resto da store (EnsureSessionAsync/AddHistoryEntryAsync
            // já degradam) — connection string aponta para um host que não existe; a purga não pode
            // lançar exceção nem derrubar o BackgroundService que a chama.
            var store = CriarStore(30, server: "host-inexistente-lpapi-teste.invalid", timeoutRapido: true);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var removidos = await store.PurgeExpiredHistoryAsync(cts.Token);

            Assert.Equal(0, removidos);
        }

        [Fact]
        public void Store_e_servico_de_limpeza_sao_resolviveis_pelo_container_sem_captive_dependency()
        {
            // SqlAiUserSessionStore é Scoped (grupo Database) — o BackgroundService precisa resolver
            // via IServiceScopeFactory, não capturar direto no construtor. Este teste confirma que o
            // grafo de DI resolve o hosted service sem o ObjectDisposedException/captive-dependency
            // que aconteceria se o construtor pedisse SqlAiUserSessionStore diretamente.
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IdentityDatabase:Server"] = "host-inexistente-lpapi-teste.invalid",
                ["IdentityDatabase:Database"] = "db",
                ["IdentityDatabase:UserId"] = "user",
                ["IdentityDatabase:Password"] = "pwd",
            }).Build());
            services.Configure<AiUserSessionHistoryOptions>(o => o.HistoryRetentionDays = 30);
            services.AddScoped<SqlAiUserSessionStore>();
            services.AddSingleton<AiUserSessionHistoryCleanupBackgroundService>();

            using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

            var hostedService = provider.GetRequiredService<AiUserSessionHistoryCleanupBackgroundService>();
            Assert.NotNull(hostedService);

            // Resolve o store dentro de um scope (o jeito correto) — não deve lançar.
            using var scope = provider.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<SqlAiUserSessionStore>();
            Assert.Equal(TimeSpan.FromDays(30), store.HistoryRetention);
        }

        private static SqlAiUserSessionStore CriarStore(int retentionDays, string server = "host-inexistente-lpapi-teste.invalid", bool timeoutRapido = false)
        {
            var configValues = new Dictionary<string, string?>
            {
                ["IdentityDatabase:Server"] = timeoutRapido ? $"{server},1" : server,
                ["IdentityDatabase:Database"] = "db",
                ["IdentityDatabase:UserId"] = "user",
                ["IdentityDatabase:Password"] = "pwd",
            };
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();

            return new SqlAiUserSessionStore(
                NullLogger<SqlAiUserSessionStore>.Instance,
                configuration,
                Options.Create(new AiUserSessionHistoryOptions { HistoryRetentionDays = retentionDays }));
        }
    }
}
