using LayoutParserApi.Models.Entities;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Transformation.Ai;
using LayoutParserApi.Services.XmlAnalysis;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Xunit;

namespace LayoutParserApi.Tests.Services.Transformation.Ai
{
    /// <summary>
    /// Issue #438 (ADR docs/architecture/adr-geracao-automatica-gabarito-sysmiddle.md §5) —
    /// <see cref="GeneratedMapperArtifactService"/>. Cobre os 4 estados do contrato do
    /// <c>GET .../generated-transformation</c>: <c>none</c>→dispara e devolve <c>generating</c>;
    /// <c>generating</c>→devolve sem disparar de novo; <c>ready</c>→devolve conteúdo com
    /// <c>validationBasis=declared_dsl</c>; <c>stale</c>→redispara. Ollama não está disponível neste
    /// ambiente de teste (localhost:11434 sem servidor) — exercita o fallback determinístico
    /// (degradação graciosa), sem I/O de banco real (store fake em memória).
    /// </summary>
    public sealed class GeneratedMapperArtifactServiceTests
    {
        private const string MapperGuid = "MAP_TESTE_438";

        // Formato "sample" aceito por MapperExtractor (RealMapperParser não reconhece isto, cai
        // no fallback — mesmo caminho de RepairOrchestratorXslSynthesizerService.ParseMapperVo).
        private const string MapperVoXml = """
            <MapperVO>
              <MapperGuid>MAP_TESTE_438</MapperGuid>
              <Name>MapTeste438</Name>
              <InputLayoutGuid>LAY_IN</InputLayoutGuid>
              <TargetLayoutGuid>LAY_OUT</TargetLayoutGuid>
              <LinkMappings>
                <LinkMappingItem>
                  <Name>campoA</Name>
                  <Sequence>1</Sequence>
                  <InputLayoutGuid>nfeProc/campoA</InputLayoutGuid>
                  <TargetLayoutGuid>nfeProc/campoA</TargetLayoutGuid>
                </LinkMappingItem>
              </LinkMappings>
              <Rules>
              </Rules>
            </MapperVO>
            """;

        private sealed class FakeCachedMapperService : ICachedMapperService
        {
            public List<Mapper> Mappers { get; } = new();
            public Task<List<Mapper>> GetAllMappersAsync() => Task.FromResult(Mappers);
            public Task<List<Mapper>> GetMappersByInputLayoutGuidAsync(string inputLayoutGuid) => Task.FromResult(new List<Mapper>());
            public Task<List<Mapper>> GetMappersByTargetLayoutGuidAsync(string targetLayoutGuid) => Task.FromResult(new List<Mapper>());
            public Task RefreshCacheFromDatabaseAsync() => Task.CompletedTask;
        }

        /// <summary>Store fake em memória — sinaliza <see cref="Completed"/> quando a geração em
        /// background termina (sucesso ou falha), para os testes esperarem sem sleep/poll.</summary>
        private sealed class FakeStore : IGeneratedMapperArtifactStore
        {
            private readonly Dictionary<string, GeneratedMapperArtifactRecord> _records = new(StringComparer.OrdinalIgnoreCase);
            public int BeginCalls;
            public TaskCompletionSource<bool> Completed { get; private set; } = new();

            public void Seed(GeneratedMapperArtifactRecord record) => _records[record.MapperGuid] = record;

            public Task<GeneratedMapperArtifactRecord?> GetAsync(string mapperGuid, CancellationToken ct)
                => Task.FromResult(_records.TryGetValue(mapperGuid, out var r) ? r : null);

            public Task<(IReadOnlyList<GeneratedMapperArtifactRecord> Items, int TotalCount)> ListAsync(string? status, int skip, int take, CancellationToken ct)
                => throw new NotSupportedException();

            public Task<bool> TryBeginGeneratingAsync(string mapperGuid, string correlationId, CancellationToken ct)
            {
                BeginCalls++;
                if (_records.TryGetValue(mapperGuid, out var existing) && existing.Status == GeneratedMapperArtifactStatus.Generating)
                    return Task.FromResult(false);

                _records[mapperGuid] = new GeneratedMapperArtifactRecord(
                    mapperGuid, GeneratedMapperArtifactStatus.Generating, null, null, null, null, correlationId, null, DateTimeOffset.UtcNow);
                return Task.FromResult(true);
            }

            public Task CompleteAsync(string mapperGuid, string content, string coverageJson, string validationBasis, string mapperVoHash, string correlationId, CancellationToken ct)
            {
                _records[mapperGuid] = new GeneratedMapperArtifactRecord(
                    mapperGuid, GeneratedMapperArtifactStatus.Ready, content, coverageJson, validationBasis, mapperVoHash, correlationId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                Completed.TrySetResult(true);
                return Task.CompletedTask;
            }

            public Task FailAsync(string mapperGuid, CancellationToken ct)
            {
                _records.Remove(mapperGuid);
                Completed.TrySetResult(false);
                return Task.CompletedTask;
            }
        }

        private static (GeneratedMapperArtifactService Service, FakeStore Store, FakeCachedMapperService Mappers, ServiceProvider Provider) Build(
            string? mapperXml = null, string? mapperGuid = null, ICachedLayoutService? layoutService = null)
        {
            var mapperService = new FakeCachedMapperService
            {
                Mappers =
                {
                    new Mapper
                    {
                        MapperGuid = mapperGuid ?? MapperGuid, Name = "MapTeste438",
                        DecryptedContent = mapperXml ?? MapperVoXml, TargetLayoutGuid = "LAY_OUT",
                    }
                }
            };
            var store = new FakeStore();

            var services = new ServiceCollection();
            services.AddSingleton<ICachedMapperService>(mapperService);
            services.AddSingleton<IGeneratedMapperArtifactStore>(store);
            if (layoutService is not null) services.AddSingleton(layoutService);
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            var provider = services.BuildServiceProvider();

            var ollamaOptions = Options.Create(new OllamaOptions { Url = "http://127.0.0.1:1", Model = "n/a" });
            // Issue #473: mesmo limite compartilhado que a versão real usaria via DI — instanciado
            // direto aqui (não Singleton no container de teste) para não vazar entre testes.
            var limiter = new GeneratedMapperGenerationLimiter(Options.Create(new GeneratedMapperSweepOptions()));
            var service = new GeneratedMapperArtifactService(
                mapperService, store, provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<GeneratedMapperArtifactService>.Instance, ollamaOptions, limiter, layoutService);

            return (service, store, mapperService, provider);
        }

        [Fact]
        public async Task None_DisparaGeracaoEDevolveGenerating()
        {
            var (service, store, _, provider) = Build();
            try
            {
                var result = await service.GetOrTriggerAsync(MapperGuid, "corr-1", CancellationToken.None);

                Assert.NotNull(result);
                Assert.Equal(GeneratedMapperArtifactStatus.Generating, result!.Status);
                Assert.Equal(1, store.BeginCalls);

                // Espera a geração em background terminar (sucesso ou falha) antes de finalizar o teste.
                var completedOk = await store.Completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(completedOk, "Geração em background deveria ter convergido via fallback determinístico (sem Ollama).");
            }
            finally
            {
                await provider.DisposeAsync();
            }
        }

        [Fact]
        public async Task Generating_NaoDisparaDeNovo()
        {
            var (service, store, _, provider) = Build();
            try
            {
                store.Seed(new GeneratedMapperArtifactRecord(
                    MapperGuid, GeneratedMapperArtifactStatus.Generating, null, null, null, null, "corr-0", null, DateTimeOffset.UtcNow));

                var result = await service.GetOrTriggerAsync(MapperGuid, "corr-2", CancellationToken.None);

                Assert.NotNull(result);
                Assert.Equal(GeneratedMapperArtifactStatus.Generating, result!.Status);
                Assert.Equal(0, store.BeginCalls); // não chamou TryBeginGeneratingAsync de novo — sem geração duplicada.
            }
            finally
            {
                await provider.DisposeAsync();
            }
        }

        [Fact]
        public async Task Ready_DevolveConteudoComValidationBasisDeclaredDsl()
        {
            var (service, store, mapperService, provider) = Build();
            try
            {
                // Mesma sequência de GeneratedMapperArtifactService.ParseMapperVo: tenta o
                // RealMapperParser (Sysmiddle real) primeiro — para este XML "sample" ele NÃO lança
                // (produz um MapperVo vazio/diferente do MapperExtractor), então é esse o hash que o
                // serviço realmente calcula; replica aqui para o teste bater com o comportamento real.
                var doc = System.Xml.Linq.XDocument.Parse(MapperVoXml);
                var mapperVo = new XslSynth.Core.RealMapperParser().Parse(doc);
                var hash = GeneratedMapperArtifactService.ComputeMapperVoHash(mapperVo);

                store.Seed(new GeneratedMapperArtifactRecord(
                    MapperGuid, GeneratedMapperArtifactStatus.Ready, "<xsl:stylesheet/>", "{\"compiles\":true}",
                    GeneratedMapperArtifactService.ValidationBasisDeclaredDsl, hash, "corr-0",
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

                var result = await service.GetOrTriggerAsync(MapperGuid, "corr-3", CancellationToken.None);

                Assert.NotNull(result);
                Assert.Equal(GeneratedMapperArtifactStatus.Ready, result!.Status);
                Assert.Equal("<xsl:stylesheet/>", result.Content);
                Assert.Equal(GeneratedMapperArtifactService.ValidationBasisDeclaredDsl, result.ValidationBasis);
                Assert.Equal(0, store.BeginCalls); // candidato já pronto — não dispara geração.
            }
            finally
            {
                await provider.DisposeAsync();
            }
        }

        [Fact]
        public async Task Stale_HashDivergente_Redispara()
        {
            var (service, store, _, provider) = Build();
            try
            {
                store.Seed(new GeneratedMapperArtifactRecord(
                    MapperGuid, GeneratedMapperArtifactStatus.Ready, "<xsl:stylesheet/>", "{\"compiles\":true}",
                    GeneratedMapperArtifactService.ValidationBasisDeclaredDsl, "HASH-ANTIGO-DIVERGENTE", "corr-0",
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

                var result = await service.GetOrTriggerAsync(MapperGuid, "corr-4", CancellationToken.None);

                Assert.NotNull(result);
                Assert.Equal(GeneratedMapperArtifactStatus.Generating, result!.Status);
                Assert.Equal(1, store.BeginCalls); // mapper "mudou" (hash divergente) — regeração disparada.

                var completedOk = await store.Completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(completedOk);
            }
            finally
            {
                await provider.DisposeAsync();
            }
        }

        [Fact]
        public async Task MapperInexistente_DevolveNull()
        {
            var (service, _, _, provider) = Build();
            try
            {
                var result = await service.GetOrTriggerAsync("MAP_NAO_EXISTE", "corr-5", CancellationToken.None);
                Assert.Null(result);
            }
            finally
            {
                await provider.DisposeAsync();
            }
        }

        // ── Casca do documento (issue #438) ─────────────────────────────────────────────

        private const string ShellMapperGuid = "MAP_TESTE_CASCA_438";

        // Formato REAL (RealMapperParser). `versao` vem como TAG_: só o LayoutVO de destino diz que é atributo.
        private const string ShellMapperXml = """
            <MapperVO>
              <MapperGuid>MAP_TESTE_CASCA_438</MapperGuid>
              <Name>Inutilizacao</Name>
              <InputLayoutGuid>LAY_IN</InputLayoutGuid>
              <TargetLayoutGuid>LAY_OUT</TargetLayoutGuid>
              <Rules>
                <Rule><Name>Rule_xmlns</Name><Sequence>1</Sequence><TargetElementGuid>ATT_1</TargetElementGuid>
                  <ContentValue>%beginRuleContent;
            T.inutNFe/xmlns = 'http://www.portalfiscal.inf.br/nfe';
            %endRuleContent;</ContentValue></Rule>
                <Rule><Name>Rule_versao</Name><Sequence>2</Sequence><TargetElementGuid>TAG_2</TargetElementGuid>
                  <ContentValue>%beginRuleContent;
            T.inutNFe/versao = '4.00';
            %endRuleContent;</ContentValue></Rule>
                <Rule><Name>Rule_xJust</Name><Sequence>3</Sequence><TargetElementGuid>TAG_3</TargetElementGuid>
                  <ContentValue>%beginRuleContent;
            T.inutNFe/infInut/xJust = Concat('Justificativa Inutilizacao:', Substring(I.ROOT/Header/xJust, 0, 227));
            %endRuleContent;</ContentValue></Rule>
              </Rules>
            </MapperVO>
            """;

        private const string ShellTargetLayoutXml = """
            <LayoutVO xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:type="XmlLayoutVO">
              <LayoutGuid>LAY_OUT</LayoutGuid>
              <Elements>
                <Element xsi:type="GroupTagElementVO">
                  <ElementGuid>GRT_1</ElementGuid><Name>inutNFe</Name>
                  <Elements>
                    <Element xsi:type="AttributeElementVO"><ElementGuid>ATT_1</ElementGuid><Name>xmlns</Name></Element>
                    <Element xsi:type="AttributeElementVO"><ElementGuid>TAG_2</ElementGuid><Name>versao</Name></Element>
                    <Element xsi:type="GroupTagElementVO"><ElementGuid>GRT_9</ElementGuid><Name>infInut</Name>
                      <Elements><Element xsi:type="TagElementVO"><ElementGuid>TAG_3</ElementGuid><Name>xJust</Name><Elements/></Element></Elements>
                    </Element>
                  </Elements>
                </Element>
              </Elements>
            </LayoutVO>
            """;

        private sealed class FakeLayoutService : ICachedLayoutService
        {
            public string? Xml { get; init; }
            public bool Throw { get; init; }
            public Task<LayoutParserApi.Models.Database.LayoutRecord?> GetLayoutByGuidAsync(string layoutGuid)
            {
                if (Throw) throw new InvalidOperationException("catalogo de layouts fora do ar");
                return Task.FromResult<LayoutParserApi.Models.Database.LayoutRecord?>(
                    Xml is null ? null : new LayoutParserApi.Models.Database.LayoutRecord { Name = "Destino", DecryptedContent = Xml });
            }
            public Task<LayoutParserApi.Models.Database.LayoutSearchResponse> SearchLayoutsAsync(LayoutParserApi.Models.Database.LayoutSearchRequest request) => throw new NotSupportedException();
            public Task<LayoutParserApi.Models.Database.LayoutRecord?> GetLayoutByIdAsync(int id) => throw new NotSupportedException();
            public Task RefreshCacheFromDatabaseAsync() => Task.CompletedTask;
            public Task ClearCacheAsync() => Task.CompletedTask;
            public ILayoutDatabaseService GetLayoutDatabaseService() => throw new NotSupportedException();
        }

        private static async Task<GeneratedMapperArtifactRecord> GenerateAsync(ICachedLayoutService? layout)
        {
            var (service, store, _, provider) = Build(ShellMapperXml, ShellMapperGuid, layout);
            try
            {
                await service.GetOrTriggerAsync(ShellMapperGuid, "corr-shell", CancellationToken.None);
                Assert.True(await store.Completed.Task.WaitAsync(TimeSpan.FromSeconds(60)), "geração em background deveria concluir");
                var record = await store.GetAsync(ShellMapperGuid, CancellationToken.None);
                Assert.NotNull(record);
                return record!;
            }
            finally
            {
                await provider.DisposeAsync();
            }
        }

        [Fact]
        public async Task Geracao_ComLayoutDeDestino_EmiteNamespaceEAtributo_NaoElementos()
        {
            var record = await GenerateAsync(new FakeLayoutService { Xml = ShellTargetLayoutXml });

            var xslt = System.Xml.Linq.XDocument.Parse(record.Content!);
            System.Xml.Linq.XNamespace nfe = "http://www.portalfiscal.inf.br/nfe";
            var root = xslt.Descendants(nfe + "inutNFe").Single();
            Assert.Empty(xslt.Descendants().Where(e => e.Name.LocalName is "xmlns" or "versao"));
            Assert.Single(root.Elements(System.Xml.Linq.XNamespace.Get("http://www.w3.org/1999/XSL/Transform") + "attribute"),
                a => (string?)a.Attribute("name") == "versao");
            Assert.Contains("concat('Justificativa Inutilizacao:',substring(ROOT/Header/xJust,1,227))", record.Content);

            var coverage = System.Text.Json.JsonDocument.Parse(record.CoverageJson!).RootElement;
            Assert.True(coverage.GetProperty("compiles").GetBoolean());
            Assert.Equal(GeneratedMapperArtifactService.GeneratorVersion, coverage.GetProperty("generatorVersion").GetString());
            Assert.Equal("inutNFe", coverage.GetProperty("shell").GetProperty("rootElement").GetString());
            Assert.Equal("http://www.portalfiscal.inf.br/nfe", coverage.GetProperty("shell").GetProperty("namespace").GetString());
            Assert.Equal(0, coverage.GetProperty("limitations").GetArrayLength());
        }

        [Fact]
        public async Task Geracao_SemServicoDeLayout_Degrada_VersaoFicaElementoENamespaceSai()
        {
            // Sem o LayoutVO nada distingue `versao` (TAG_) de elemento: limite honesto, não inventa.
            var record = await GenerateAsync(layout: null);

            var xslt = System.Xml.Linq.XDocument.Parse(record.Content!);
            System.Xml.Linq.XNamespace nfe = "http://www.portalfiscal.inf.br/nfe";
            Assert.Single(xslt.Descendants(nfe + "inutNFe"));
            Assert.Empty(xslt.Descendants().Where(e => e.Name.LocalName == "xmlns"));
            Assert.Single(xslt.Descendants().Where(e => e.Name.LocalName == "versao"));
            Assert.True(System.Text.Json.JsonDocument.Parse(record.CoverageJson!).RootElement.GetProperty("compiles").GetBoolean());
        }

        [Fact]
        public async Task Geracao_LayoutIndisponivelOuComFalha_NaoDerrubaAGeracao()
        {
            var comFalha = await GenerateAsync(new FakeLayoutService { Throw = true });
            var ausente = await GenerateAsync(new FakeLayoutService { Xml = null });

            Assert.Equal(GeneratedMapperArtifactStatus.Ready, comFalha.Status);
            Assert.Equal(GeneratedMapperArtifactStatus.Ready, ausente.Status);
        }

        [Fact]
        public async Task Hash_IncluiVersaoDoGerador_ArtefatoLegadoViraStale()
        {
            var (service, store, _, provider) = Build(ShellMapperXml, ShellMapperGuid);
            try
            {
                var mapperVo = new XslSynth.Core.RealMapperParser().Parse(System.Xml.Linq.XDocument.Parse(ShellMapperXml));

                // Hash como o gerador ANTERIOR (sem versão) calculava — é o que os artefatos persistidos têm.
                var legacy = new System.Text.StringBuilder();
                foreach (var rule in mapperVo.Rules.OrderBy(r => r.Sequence).ThenBy(r => r.Name, StringComparer.Ordinal))
                    legacy.Append("R|").Append(rule.Name).Append('|').Append(rule.TargetPath).Append('|').Append(rule.ContentValue).Append('\n');
                var legacyHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(legacy.ToString())));

                Assert.NotEqual(legacyHash, GeneratedMapperArtifactService.ComputeMapperVoHash(mapperVo));

                store.Seed(new GeneratedMapperArtifactRecord(
                    ShellMapperGuid, GeneratedMapperArtifactStatus.Ready, "<xsl:stylesheet/>", "{}",
                    GeneratedMapperArtifactService.ValidationBasisDeclaredDsl, legacyHash, "corr-0",
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

                var result = await service.GetOrTriggerAsync(ShellMapperGuid, "corr-legado", CancellationToken.None);

                Assert.Equal(GeneratedMapperArtifactStatus.Generating, result!.Status);
                Assert.Equal(1, store.BeginCalls);
                Assert.True(await store.Completed.Task.WaitAsync(TimeSpan.FromSeconds(60)));
            }
            finally
            {
                await provider.DisposeAsync();
            }
        }
    }
}
