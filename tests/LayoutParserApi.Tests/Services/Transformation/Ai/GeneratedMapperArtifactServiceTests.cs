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

        private static (GeneratedMapperArtifactService Service, FakeStore Store, FakeCachedMapperService Mappers, ServiceProvider Provider) Build()
        {
            var mapperService = new FakeCachedMapperService
            {
                Mappers = { new Mapper { MapperGuid = MapperGuid, Name = "MapTeste438", DecryptedContent = MapperVoXml } }
            };
            var store = new FakeStore();

            var services = new ServiceCollection();
            services.AddSingleton<ICachedMapperService>(mapperService);
            services.AddSingleton<IGeneratedMapperArtifactStore>(store);
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            var provider = services.BuildServiceProvider();

            var ollamaOptions = Options.Create(new OllamaOptions { Url = "http://127.0.0.1:1", Model = "n/a" });
            var service = new GeneratedMapperArtifactService(
                mapperService, store, provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<GeneratedMapperArtifactService>.Instance, ollamaOptions);

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
    }
}
