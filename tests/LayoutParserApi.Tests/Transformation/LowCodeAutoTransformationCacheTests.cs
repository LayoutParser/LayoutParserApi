using LayoutParserApi.Models.Entities;
using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.Transformation.LowCode;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Tests.Transformation
{
    /// <summary>
    /// Cache-first do pathway low-code (<c>spec-entrega-da-transformacao-no-parse.md</c> §1.2).
    ///
    /// <para>O defeito: o MESMO documento rodava o runner duas vezes — uma no parse
    /// (<c>ParseController</c>) e outra no clique de "Gerar Transformação XML"
    /// (<c>execute-candidates</c>) — multiplicando por 2 o custo de N candidatos. O <c>sha256</c> do
    /// input já era calculado, mas servia só como nome de arquivo.</para>
    ///
    /// <para>Todos os cenários rodam <b>sem Redis</b>: o ganho tem de vir do disco (§4.3).</para>
    /// </summary>
    public class LowCodeAutoTransformationCacheTests
    {
        private const string LayoutGuid = "79adf76a-4b07-428c-90d7-3c39d1296a5d";
        private const string Documento = "000001DADOS DO DOCUMENTO POSICIONAL";

        [Fact]
        public async Task Mesmo_documento_e_layout_invocam_o_runner_uma_unica_vez()
        {
            var (auto, runner, mapperDb) = CriarPathway(Mapper("M1"));

            var primeira = await auto.RunAsync(LayoutGuid, "LAY_TESTE", Documento, "mqseries", "doc.txt");
            var segunda = await auto.RunAsync(LayoutGuid, "LAY_TESTE", Documento, "mqseries", "doc.txt");

            Assert.Equal(1, runner.Invocacoes);

            // Cache-first fica ANTES do banco: o clique do usuário não paga nem o SQL.
            Assert.Equal(1, mapperDb.Consultas);

            // E o segundo resultado é o mesmo conteúdo, não um casco vazio.
            Assert.True(segunda.Applicable);
            var candidato = Assert.Single(segunda.Candidates);
            Assert.True(candidato.Success);
            Assert.Equal(Assert.Single(primeira.Candidates).OutputXml, candidato.OutputXml);
            Assert.Equal(candidato.OutputXml!.Length, candidato.OutputLength);
        }

        [Fact]
        public async Task Multi_candidato_tambem_para_de_repetir_o_runner()
        {
            var (auto, runner, _) = CriarPathway(Mapper("M1"), Mapper("M2"), Mapper("M3"));

            await auto.RunAsync(LayoutGuid, "LAY_TESTE", Documento, "mqseries", "doc.txt");
            var segunda = await auto.RunAsync(LayoutGuid, "LAY_TESTE", Documento, "mqseries", "doc.txt");

            // 3 candidatos na primeira execução e ZERO na segunda — antes seriam 6 execuções do
            // runner para o mesmo documento.
            Assert.Equal(3, runner.Invocacoes);
            Assert.True(segunda.MultiCandidate);
            Assert.Equal(3, segunda.Candidates.Count);
            Assert.All(segunda.Candidates, c => Assert.False(string.IsNullOrEmpty(c.OutputXml)));
        }

        [Fact]
        public async Task Documento_diferente_volta_a_rodar_o_runner()
        {
            var (auto, runner, _) = CriarPathway(Mapper("M1"));

            await auto.RunAsync(LayoutGuid, "LAY_TESTE", Documento, "mqseries", "doc.txt");
            await auto.RunAsync(LayoutGuid, "LAY_TESTE", Documento + " (outro)", "mqseries", "doc.txt");

            // A chave é (conteúdo, layout): documento diferente NÃO pode ser servido do cache.
            Assert.Equal(2, runner.Invocacoes);
        }

        [Fact]
        public async Task Layout_diferente_volta_a_rodar_o_runner()
        {
            var (auto, runner, _) = CriarPathway(Mapper("M1"));

            await auto.RunAsync(LayoutGuid, "LAY_TESTE", Documento, "mqseries", "doc.txt");
            await auto.RunAsync("ad4fb6f4-9ff5-44fd-988b-3da5ed56b22c", "LAY_OUTRO", Documento, "mqseries", "doc.txt");

            Assert.Equal(2, runner.Invocacoes);
        }

        /// <summary>
        /// Falha do runner não pode virar cache — senão uma indisponibilidade momentânea congelaria
        /// "falhou" para todo upload idêntico seguinte, dentro da janela de frescor.
        /// </summary>
        [Fact]
        public async Task Execucao_que_falhou_nao_vira_cache()
        {
            var (auto, runner, _) = CriarPathway(Mapper("M1"));
            runner.Falhar = true;

            var primeira = await auto.RunAsync(LayoutGuid, "LAY_TESTE", Documento, "mqseries", "doc.txt");
            Assert.False(Assert.Single(primeira.Candidates).Success);

            runner.Falhar = false;
            var segunda = await auto.RunAsync(LayoutGuid, "LAY_TESTE", Documento, "mqseries", "doc.txt");

            Assert.Equal(2, runner.Invocacoes);
            Assert.True(Assert.Single(segunda.Candidates).Success);
        }

        /// <summary>
        /// A mensagem de erro do candidato sai no payload 200 do parse: nada de caminho de disco do
        /// servidor nela (spec §3.1).
        /// </summary>
        [Fact]
        public async Task Erro_do_candidato_chega_saneado_ao_resultado()
        {
            var (auto, runner, _) = CriarPathway(Mapper("M1"));
            runner.Falhar = true;

            var resultado = await auto.RunAsync(LayoutGuid, "LAY_TESTE", Documento, "mqseries", "doc.txt");

            var mensagem = Assert.Single(resultado.Candidates).ErrorMessage;
            Assert.False(string.IsNullOrWhiteSpace(mensagem));
            Assert.DoesNotContain(@"C:\", mensagem!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("inetpub", mensagem!, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Sem mapper não há execução — e também não pode sobrar entrada órfã no índice.</summary>
        [Fact]
        public async Task Sem_mapper_nao_registra_nada_no_indice()
        {
            var (auto, runner, _, store) = CriarPathwayCompleto();

            var resultado = await auto.RunAsync(LayoutGuid, "LAY_TESTE", Documento, "mqseries", "doc.txt");

            Assert.False(resultado.Applicable);
            Assert.Equal(0, runner.Invocacoes);
            Assert.Null(await store.ReadEntryAsync(LowCodeTransformationStore.ComputeSha256(Documento), LayoutGuid));
        }

        /// <summary>
        /// Regressão do diagnóstico de <c>execute-candidates:[]</c> (issue #38/#39, capítulo 3):
        /// uma falha de SQL (conexão/timeout/permissão) em
        /// <see cref="MapperDatabaseService.GetMappersByLayoutGuidForPackagesAsync"/> ERA engolida e
        /// virava lista vazia — indistinguível de "não existe mapper cadastrado" (mesmo
        /// <c>Applicable=false</c>, mesma mensagem no controller: "Nenhum mapeador low-code
        /// encontrado"). Isso mascarava um problema de infraestrutura como ausência de dado, mesmo
        /// quando os mappers existiam de verdade no banco (confirmado por SQL manual do dono do
        /// projeto). Agora a falha de banco PRECISA propagar como exceção — os dois únicos chamadores
        /// (<c>ParseController</c> e <c>TransformationExecutionController</c>) já capturam isso no
        /// nível certo e reportam com uma mensagem distinta ("error"/"Pathway sysmiddle falhou: ..."),
        /// nunca derrubando a resposta principal.
        /// </summary>
        [Fact]
        public async Task Falha_de_banco_ao_buscar_candidatos_propaga_como_excecao_em_vez_de_Applicable_false()
        {
            var (autoComFalha, _, mapperDbFalho) = CriarPathwayComBancoFalho();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                autoComFalha.RunAsync(LayoutGuid, "LAY_TESTE", Documento, "mqseries", "doc.txt"));

            Assert.Equal(1, mapperDbFalho.Consultas);
        }

        private static (LowCodeAutoTransformationService auto, RunnerEspiao runner, MapperDbFalho mapperDb) CriarPathwayComBancoFalho()
        {
            var raiz = Path.Combine(Path.GetTempPath(), "lp-tests", "lowcode-auto", Guid.NewGuid().ToString("N"));

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ML:LowCodeTransformationsPath"] = raiz,
                    ["Logging:File:Directory"] = Path.Combine(Path.GetTempPath(), "lp-tests", "runner-logs")
                })
                .Build();

            var opcoes = Options.Create(new LowCodeRunnerOptions
            {
                RunnerPath = "runner-inexistente.exe",
                SysmiddleDir = Path.GetTempPath(),
                GlobalFolder = Path.GetTempPath()
            });

            var store = new LowCodeTransformationStore(
                NullLogger<LowCodeTransformationStore>.Instance, config, opcoes, redis: null);

            var runner = new RunnerEspiao(opcoes, config);
            var mapperDb = new MapperDbFalho(config);

            var servicos = new ServiceCollection();
            servicos.AddScoped<MapperDatabaseService>(_ => mapperDb);

            var auto = new LowCodeAutoTransformationService(
                NullLogger<LowCodeAutoTransformationService>.Instance,
                servicos.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
                runner,
                store,
                opcoes);

            return (auto, runner, mapperDb);
        }

        // ─────────────────────────────── infraestrutura ───────────────────────────────

        private static Mapper Mapper(string guid) => new()
        {
            MapperGuid = guid,
            Name = $"MAP_{guid}",
            PackageGuid = "PAC_1",
            ProjectId = "2",
            TargetLayoutGuid = "TGT",
            TargetLayoutGuidFromXml = "TGT_XML"
        };

        private static (LowCodeAutoTransformationService auto, RunnerEspiao runner, MapperDbFalso mapperDb) CriarPathway(params Mapper[] mappers)
        {
            var (auto, runner, mapperDb, _) = CriarPathwayCompleto(mappers);
            return (auto, runner, mapperDb);
        }

        private static (LowCodeAutoTransformationService auto, RunnerEspiao runner, MapperDbFalso mapperDb, LowCodeTransformationStore store)
            CriarPathwayCompleto(params Mapper[] mappers)
        {
            var raiz = Path.Combine(Path.GetTempPath(), "lp-tests", "lowcode-auto", Guid.NewGuid().ToString("N"));

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ML:LowCodeTransformationsPath"] = raiz,
                    ["Logging:File:Directory"] = Path.Combine(Path.GetTempPath(), "lp-tests", "runner-logs")
                })
                .Build();

            var opcoes = Options.Create(new LowCodeRunnerOptions
            {
                RunnerPath = "runner-inexistente.exe",
                SysmiddleDir = Path.GetTempPath(),
                GlobalFolder = Path.GetTempPath()
            });

            // redis: null — cenário provável em produção; o cache precisa funcionar só com disco.
            var store = new LowCodeTransformationStore(
                NullLogger<LowCodeTransformationStore>.Instance, config, opcoes, redis: null);

            var runner = new RunnerEspiao(opcoes, config);
            var mapperDb = new MapperDbFalso(config, mappers.ToList());

            var servicos = new ServiceCollection();
            servicos.AddScoped<MapperDatabaseService>(_ => mapperDb);

            var auto = new LowCodeAutoTransformationService(
                NullLogger<LowCodeAutoTransformationService>.Instance,
                servicos.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
                runner,
                store,
                opcoes);

            return (auto, runner, mapperDb, store);
        }

        /// <summary>Conta invocações do runner — é a métrica do gate de cache-first.</summary>
        private sealed class RunnerEspiao : LowCodeTransformationService
        {
            private int _invocacoes;

            public RunnerEspiao(IOptions<LowCodeRunnerOptions> opcoes, IConfiguration config)
                : base(NullLogger<LowCodeTransformationService>.Instance, opcoes, config)
            {
            }

            public int Invocacoes => Volatile.Read(ref _invocacoes);

            public bool Falhar { get; set; }

            public override Task<string> TransformAsync(
                string inputContent,
                string? mapperId = null,
                string? mapperName = null,
                string? fileName = null,
                string? package = null,
                string? globalFolder = null,
                string? sysmiddleDir = null,
                CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _invocacoes);

                if (Falhar)
                {
                    // Mensagem no formato que o runner real produzia — com caminho absoluto.
                    throw new Exception($@"Runner nao gerou outputFile: C:\inetpub\wwwroot\layoutparser\api\out_{mapperId}.xml");
                }

                return Task.FromResult($"<nfe mapper=\"{mapperId}\">{inputContent.Length}</nfe>");
            }
        }

        private sealed class MapperDbFalso : MapperDatabaseService
        {
            private readonly List<Mapper> _mappers;
            private int _consultas;

            public MapperDbFalso(IConfiguration config, List<Mapper> mappers)
                : base(NullLogger<MapperDatabaseService>.Instance, null!, config)
            {
                _mappers = mappers;
            }

            public int Consultas => Volatile.Read(ref _consultas);

            public override Task<List<Mapper>> GetRankedMapperCandidatesForLayoutGuidAsync(
                string layoutGuid, int projectId, IReadOnlyCollection<string> allowedPackageGuids)
            {
                Interlocked.Increment(ref _consultas);
                return Task.FromResult(_mappers.ToList());
            }
        }

        /// <summary>
        /// Simula a falha real de <see cref="MapperDatabaseService.GetMappersByLayoutGuidForPackagesAsync"/>
        /// (ex.: conexão SQL indisponível) — precisa propagar, não virar lista vazia silenciosa.
        /// </summary>
        private sealed class MapperDbFalho : MapperDatabaseService
        {
            private int _consultas;

            public MapperDbFalho(IConfiguration config)
                : base(NullLogger<MapperDatabaseService>.Instance, null!, config)
            {
            }

            public int Consultas => Volatile.Read(ref _consultas);

            public override Task<List<Mapper>> GetRankedMapperCandidatesForLayoutGuidAsync(
                string layoutGuid, int projectId, IReadOnlyCollection<string> allowedPackageGuids)
            {
                Interlocked.Increment(ref _consultas);
                throw new InvalidOperationException("Falha simulada de conexão SQL ao buscar mapeadores.");
            }
        }
    }
}
