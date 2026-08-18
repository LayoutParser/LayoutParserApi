using LayoutParserApi.Services.Transformation.Ai;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Tests.Services.Transformation.Ai
{
    /// <summary>
    /// Issue #51 — a store do pathway IA não tinha nenhuma expiração: cada <c>execute-candidates</c>
    /// que disparava o pathway criava um ticket que ficava para sempre em memória (<c>ConcurrentDictionary</c>)
    /// e em disco (JSON por ticket). A issue chegou a ser fechada citando a retenção de <c>RunManifest.cs</c>,
    /// que é outro subsistema (run dirs do Job 1 de métricas) e não tocava neste leak.
    ///
    /// <para>Os testes cobrem as DUAS camadas e usam relógio injetado — nada de <c>Task.Delay</c> real.</para>
    /// </summary>
    public class AiCandidateStoreTtlTests
    {
        [Fact]
        public void Varredura_remove_ticket_vencido_de_memoria_e_disco_e_preserva_o_que_esta_no_ttl()
        {
            var dir = CriarDiretorioTemporario();
            try
            {
                var agora = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
                var store = CriarStore(dir, () => agora, ttlHoras: 24);

                store.Set("usuario-a", "ticket-velho", new AiCandidateStatus { Status = AiCandidateStatus.StatusFailed });

                // Envelhece o relógio além do TTL e grava um ticket novo já na "era" seguinte.
                agora = agora.AddHours(25);
                store.Set("usuario-a", "ticket-novo", new AiCandidateStatus { Status = AiCandidateStatus.StatusConverged });

                var arquivoVelho = Path.Combine(dir, "usuario-a", "ticket-velho.json");
                var arquivoNovo = Path.Combine(dir, "usuario-a", "ticket-novo.json");
                Assert.True(File.Exists(arquivoVelho), "o ticket vencido ainda deve existir em disco ANTES da varredura");

                var resultado = store.RemoveExpired();

                Assert.Equal(1, resultado.TicketsEmMemoria);
                Assert.Equal(1, resultado.ArquivosEmDisco);

                // Vencido: sumiu das duas camadas.
                Assert.False(File.Exists(arquivoVelho));
                Assert.Null(store.Get("usuario-a", "ticket-velho"));

                // Dentro do TTL: intocado nas duas camadas.
                Assert.True(File.Exists(arquivoNovo));
                Assert.Equal(AiCandidateStatus.StatusConverged, store.Get("usuario-a", "ticket-novo")?.Status);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void Get_nao_devolve_ticket_vencido_mesmo_antes_da_varredura()
        {
            var dir = CriarDiretorioTemporario();
            try
            {
                var agora = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
                var store = CriarStore(dir, () => agora, ttlHoras: 24);

                store.Set("usuario-a", "ticket-poll", new AiCandidateStatus { Status = AiCandidateStatus.StatusRunning });
                Assert.NotNull(store.Get("usuario-a", "ticket-poll"));

                agora = agora.AddHours(25);
                Assert.Null(store.Get("usuario-a", "ticket-poll"));

                // O Get já liberou a entrada da memória (O(1), sem I/O): à varredura seguinte
                // sobra apenas o arquivo em disco.
                var resultado = store.RemoveExpired();
                Assert.Equal(0, resultado.TicketsEmMemoria);
                Assert.Equal(1, resultado.ArquivosEmDisco);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void Store_nova_nao_ressuscita_ticket_vencido_do_disco()
        {
            var dir = CriarDiretorioTemporario();
            try
            {
                var agora = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
                var storeAntiga = CriarStore(dir, () => agora, ttlHoras: 24);
                storeAntiga.Set("usuario-a", "ticket-restart", new AiCandidateStatus { Status = AiCandidateStatus.StatusConverged });

                agora = agora.AddHours(25);

                // Simula restart da API: memória zerada, disco cheio. O TTL é absoluto a partir da
                // escrita, então o ticket não pode voltar à vida só porque o processo reiniciou.
                var storeNova = CriarStore(dir, () => agora, ttlHoras: 24);
                Assert.Null(storeNova.Get("usuario-a", "ticket-restart"));

                var resultado = storeNova.RemoveExpired();
                Assert.Equal(0, resultado.TicketsEmMemoria);
                Assert.Equal(1, resultado.ArquivosEmDisco);
                Assert.False(File.Exists(Path.Combine(dir, "usuario-a", "ticket-restart.json")));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void Ttl_nao_configurado_ou_invalido_cai_no_default()
        {
            var dir = CriarDiretorioTemporario();
            try
            {
                var padrao = CriarStore(dir, () => DateTimeOffset.UtcNow, ttlHoras: AiTransformationCandidateOptions.DefaultTicketTtlHours);
                var invalido = CriarStore(dir, () => DateTimeOffset.UtcNow, ttlHoras: 0);

                Assert.Equal(TimeSpan.FromHours(72), padrao.Ttl);
                Assert.Equal(padrao.Ttl, invalido.Ttl);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void Store_e_servico_de_limpeza_sao_resolviveis_pelo_container()
        {
            // O relógio é parâmetro opcional do construtor (só os testes injetam) — este teste
            // garante que isso não quebra o AddSingleton<AiCandidateStore>() do Program.cs.
            var dir = CriarDiretorioTemporario();
            try
            {
                var services = new ServiceCollection();
                services.AddLogging();
                services.Configure<AiTransformationCandidateOptions>(o => o.StorePath = dir);
                services.AddSingleton<AiCandidateStore>();
                services.AddSingleton<AiCandidateStoreCleanupBackgroundService>();

                using var provider = services.BuildServiceProvider();

                var store = provider.GetRequiredService<AiCandidateStore>();
                Assert.Equal(TimeSpan.FromHours(AiTransformationCandidateOptions.DefaultTicketTtlHours), store.Ttl);
                Assert.NotNull(provider.GetRequiredService<AiCandidateStoreCleanupBackgroundService>());
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void Set_poda_tickets_mais_antigos_quando_excede_o_limite_de_tamanho()
        {
            // Issue #51 (segunda metade): TTL sozinho não protege um pico de tickets ainda dentro
            // da janela — MaxStoredTickets é o teto duro, aplicado no caminho de escrita.
            var dir = CriarDiretorioTemporario();
            try
            {
                var agora = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
                var store = CriarStore(dir, () => agora, ttlHoras: 24, maxStoredTickets: 3);

                for (var i = 0; i < 5; i++)
                {
                    store.Set("usuario-a", $"ticket-{i}", new AiCandidateStatus { Status = AiCandidateStatus.StatusRunning });
                    agora = agora.AddMinutes(1); // Cada Set em instante distinto — desempata a poda por idade.
                }

                var arquivosRestantes = Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories).ToList();
                Assert.Equal(3, arquivosRestantes.Count);

                // Os dois mais antigos (ticket-0, ticket-1) foram podados; os três mais recentes sobrevivem.
                Assert.Null(store.Get("usuario-a", "ticket-0"));
                Assert.Null(store.Get("usuario-a", "ticket-1"));
                Assert.NotNull(store.Get("usuario-a", "ticket-2"));
                Assert.NotNull(store.Get("usuario-a", "ticket-3"));
                Assert.NotNull(store.Get("usuario-a", "ticket-4"));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void MaxStoredTickets_nao_configurado_ou_invalido_cai_no_default()
        {
            var dir = CriarDiretorioTemporario();
            try
            {
                var padrao = CriarStore(dir, () => DateTimeOffset.UtcNow, ttlHoras: 24, maxStoredTickets: AiTransformationCandidateOptions.DefaultMaxStoredTickets);
                var invalido = CriarStore(dir, () => DateTimeOffset.UtcNow, ttlHoras: 24, maxStoredTickets: 0);

                // Nenhum ticket escrito ainda — só confere que o construtor não lança com o default
                // nem com valor inválido (mesma convenção de TicketTtlHours).
                Assert.NotNull(padrao);
                Assert.NotNull(invalido);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        private static AiCandidateStore CriarStore(string storePath, Func<DateTimeOffset> relogio, int ttlHoras, int maxStoredTickets = 0)
            => new(
                NullLogger<AiCandidateStore>.Instance,
                Options.Create(new AiTransformationCandidateOptions
                {
                    StorePath = storePath,
                    TicketTtlHours = ttlHoras,
                    MaxStoredTickets = maxStoredTickets
                }),
                relogio);

        private static string CriarDiretorioTemporario()
        {
            var dir = Path.Combine(Path.GetTempPath(), "lpapi-ai-ttl-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
