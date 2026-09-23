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
    /// Issue #473 (fase 2 do trigger lazy #438, ADR §3/§4/§6) —
    /// <see cref="GeneratedMapperArtifactSweepService"/>. Duas frentes:
    /// (1) <see cref="GeneratedMapperArtifactSweepService.SelectCandidatesForSweep"/> — lógica pura de
    /// seleção (catálogo menos os já em geração, priorizado por recência, limitado por rodada), sem
    /// I/O, testável direto; (2) <see cref="GeneratedMapperArtifactSweepService.RunSweepOnceAsync"/> —
    /// com fakes de <see cref="ICachedMapperService"/>/<see cref="IGeneratedMapperArtifactStore"/>, sem
    /// o <c>BackgroundService</c> rodando de verdade (sem <c>ExecuteAsync</c>/timer).
    /// </summary>
    public sealed class GeneratedMapperArtifactSweepServiceTests
    {
        // ── SelectCandidatesForSweep: lógica pura, sem I/O ──────────────────────────────

        [Fact]
        public void SelectCandidatesForSweep_ExcluiOsJaEmGeracao()
        {
            var catalog = new List<Mapper>
            {
                new() { MapperGuid = "MAP_A", LastUpdateDate = new DateTime(2026, 1, 1) },
                new() { MapperGuid = "MAP_B", LastUpdateDate = new DateTime(2026, 1, 2) },
            };
            var generating = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MAP_A" };

            var result = GeneratedMapperArtifactSweepService.SelectCandidatesForSweep(catalog, generating, maxPerRound: 10);

            Assert.Single(result);
            Assert.Equal("MAP_B", result[0].MapperGuid);
        }

        [Fact]
        public void SelectCandidatesForSweep_PriorizaMaisRecentePrimeiro()
        {
            var catalog = new List<Mapper>
            {
                new() { MapperGuid = "MAP_ANTIGO", LastUpdateDate = new DateTime(2020, 1, 1) },
                new() { MapperGuid = "MAP_NOVO", LastUpdateDate = new DateTime(2026, 9, 1) },
                new() { MapperGuid = "MAP_MEIO", LastUpdateDate = new DateTime(2023, 5, 1) },
            };

            var result = GeneratedMapperArtifactSweepService.SelectCandidatesForSweep(
                catalog, new HashSet<string>(), maxPerRound: 10);

            Assert.Equal(new[] { "MAP_NOVO", "MAP_MEIO", "MAP_ANTIGO" }, result.Select(m => m.MapperGuid));
        }

        [Fact]
        public void SelectCandidatesForSweep_RespeitaTetoPorRodada()
        {
            var catalog = Enumerable.Range(0, 50)
                .Select(i => new Mapper { MapperGuid = $"MAP_{i}", LastUpdateDate = DateTime.UtcNow.AddMinutes(-i) })
                .ToList();

            var result = GeneratedMapperArtifactSweepService.SelectCandidatesForSweep(
                catalog, new HashSet<string>(), maxPerRound: 5);

            Assert.Equal(5, result.Count);
        }

        [Fact]
        public void SelectCandidatesForSweep_CatalogoVazio_DevolveVazio()
        {
            var result = GeneratedMapperArtifactSweepService.SelectCandidatesForSweep(
                new List<Mapper>(), new HashSet<string>(), maxPerRound: 10);

            Assert.Empty(result);
        }

        [Fact]
        public void SelectCandidatesForSweep_MaxPerRoundZeroOuNegativo_DevolveVazio()
        {
            var catalog = new List<Mapper> { new() { MapperGuid = "MAP_A", LastUpdateDate = DateTime.UtcNow } };

            Assert.Empty(GeneratedMapperArtifactSweepService.SelectCandidatesForSweep(catalog, new HashSet<string>(), maxPerRound: 0));
            Assert.Empty(GeneratedMapperArtifactSweepService.SelectCandidatesForSweep(catalog, new HashSet<string>(), maxPerRound: -1));
        }

        // ── RunSweepOnceAsync: com fakes, sem o BackgroundService/timer rodando ─────────

        private sealed class FakeCachedMapperService : ICachedMapperService
        {
            public List<Mapper> Mappers { get; } = new();
            public Task<List<Mapper>> GetAllMappersAsync() => Task.FromResult(Mappers);
            public Task<List<Mapper>> GetMappersByInputLayoutGuidAsync(string inputLayoutGuid) => Task.FromResult(new List<Mapper>());
            public Task<List<Mapper>> GetMappersByTargetLayoutGuidAsync(string targetLayoutGuid) => Task.FromResult(new List<Mapper>());
            public Task RefreshCacheFromDatabaseAsync() => Task.CompletedTask;
        }

        private sealed class FakeStore : IGeneratedMapperArtifactStore
        {
            private readonly Dictionary<string, GeneratedMapperArtifactRecord> _records = new(StringComparer.OrdinalIgnoreCase);

            public void Seed(GeneratedMapperArtifactRecord record) => _records[record.MapperGuid] = record;

            public Task<GeneratedMapperArtifactRecord?> GetAsync(string mapperGuid, CancellationToken ct)
                => Task.FromResult(_records.TryGetValue(mapperGuid, out var r) ? r : null);

            public Task<(IReadOnlyList<GeneratedMapperArtifactRecord> Items, int TotalCount)> ListAsync(string? status, int skip, int take, CancellationToken ct)
            {
                var items = _records.Values.Where(r => status is null || r.Status == status).ToList();
                return Task.FromResult<(IReadOnlyList<GeneratedMapperArtifactRecord>, int)>((items, items.Count));
            }

            public Task<bool> TryBeginGeneratingAsync(string mapperGuid, string correlationId, CancellationToken ct)
            {
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
                return Task.CompletedTask;
            }

            public Task FailAsync(string mapperGuid, CancellationToken ct)
            {
                _records.Remove(mapperGuid);
                return Task.CompletedTask;
            }
        }

        // Formato "sample" aceito por MapperExtractor — mesmo XML mínimo de GeneratedMapperArtifactServiceTests.
        private static string MapperVoXml(string guid) => $"""
            <MapperVO>
              <MapperGuid>{guid}</MapperGuid>
              <Name>Mapper_{guid}</Name>
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
              <Rules></Rules>
            </MapperVO>
            """;

        private static (GeneratedMapperArtifactSweepService Sweep, FakeCachedMapperService Mappers, FakeStore Store, ServiceProvider Provider) Build(int maxMappersPerRound = 10)
        {
            var mapperService = new FakeCachedMapperService();
            var store = new FakeStore();

            var services = new ServiceCollection();
            services.AddSingleton<ICachedMapperService>(mapperService);
            services.AddSingleton<IGeneratedMapperArtifactStore>(store);
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

            var sweepOptions = Options.Create(new GeneratedMapperSweepOptions { MaxMappersPerRound = maxMappersPerRound });
            var limiter = new GeneratedMapperGenerationLimiter(sweepOptions);
            var ollamaOptions = Options.Create(new OllamaOptions { Url = "http://127.0.0.1:1", Model = "n/a" });

            services.AddSingleton<IGeneratedMapperArtifactService>(sp => new GeneratedMapperArtifactService(
                mapperService, store, sp.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<GeneratedMapperArtifactService>.Instance, ollamaOptions, limiter));

            var provider = services.BuildServiceProvider();

            var sweep = new GeneratedMapperArtifactSweepService(
                NullLogger<GeneratedMapperArtifactSweepService>.Instance,
                provider.GetRequiredService<IServiceScopeFactory>(),
                sweepOptions);

            return (sweep, mapperService, store, provider);
        }

        [Fact]
        public async Task RunSweepOnceAsync_CatalogoVazio_NaoFalha()
        {
            var (sweep, _, _, provider) = Build();
            try
            {
                await sweep.RunSweepOnceAsync(CancellationToken.None);
                // Não lança — é o único comportamento observável sem catálogo.
            }
            finally
            {
                await provider.DisposeAsync();
            }
        }

        [Fact]
        public async Task RunSweepOnceAsync_MapperSemCandidato_DisparaGeracaoEConverge()
        {
            var (sweep, mappers, store, provider) = Build();
            try
            {
                const string guid = "MAP_SWEEP_1";
                mappers.Mappers.Add(new Mapper { MapperGuid = guid, Name = "Teste", DecryptedContent = MapperVoXml(guid), LastUpdateDate = DateTime.UtcNow });

                await sweep.RunSweepOnceAsync(CancellationToken.None);

                // A geração é fire-and-forget (Task.Run) dentro do trigger lazy reaproveitado —
                // aguarda convergir para "ready" (sem Ollama, cai no fallback determinístico).
                GeneratedMapperArtifactRecord? record = null;
                for (var i = 0; i < 100 && (record is null || record.Status == GeneratedMapperArtifactStatus.Generating); i++)
                {
                    record = await store.GetAsync(guid, CancellationToken.None);
                    if (record is null || record.Status == GeneratedMapperArtifactStatus.Generating)
                        await Task.Delay(50);
                }

                Assert.NotNull(record);
                Assert.Equal(GeneratedMapperArtifactStatus.Ready, record!.Status);
            }
            finally
            {
                await provider.DisposeAsync();
            }
        }

        [Fact]
        public async Task RunSweepOnceAsync_MapperJaEmGeracao_NaoRedispara()
        {
            var (sweep, mappers, store, provider) = Build();
            try
            {
                const string guid = "MAP_SWEEP_2";
                mappers.Mappers.Add(new Mapper { MapperGuid = guid, Name = "Teste", DecryptedContent = MapperVoXml(guid), LastUpdateDate = DateTime.UtcNow });
                store.Seed(new GeneratedMapperArtifactRecord(
                    guid, GeneratedMapperArtifactStatus.Generating, null, null, null, null, "corr-existente", null, DateTimeOffset.UtcNow));

                await sweep.RunSweepOnceAsync(CancellationToken.None);

                var record = await store.GetAsync(guid, CancellationToken.None);
                // SelectCandidatesForSweep já exclui quem está "generating" — não deveria nem ter sido
                // examinado; o correlationId original permanece intacto (prova de que não foi tocado).
                Assert.NotNull(record);
                Assert.Equal("corr-existente", record!.CorrelationId);
            }
            finally
            {
                await provider.DisposeAsync();
            }
        }

        [Fact]
        public async Task RunSweepOnceAsync_MapperJaPronto_NaoRedispara()
        {
            var (sweep, mappers, store, provider) = Build();
            try
            {
                const string guid = "MAP_SWEEP_3";
                var xml = MapperVoXml(guid);
                mappers.Mappers.Add(new Mapper { MapperGuid = guid, Name = "Teste", DecryptedContent = xml, LastUpdateDate = DateTime.UtcNow });

                var mapperVo = new XslSynth.Core.RealMapperParser().Parse(System.Xml.Linq.XDocument.Parse(xml));
                var hash = GeneratedMapperArtifactService.ComputeMapperVoHash(mapperVo);
                store.Seed(new GeneratedMapperArtifactRecord(
                    guid, GeneratedMapperArtifactStatus.Ready, "<xsl:stylesheet/>", "{}",
                    GeneratedMapperArtifactService.ValidationBasisDeclaredDsl, hash, "corr-pronto",
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

                await sweep.RunSweepOnceAsync(CancellationToken.None);

                var record = await store.GetAsync(guid, CancellationToken.None);
                Assert.Equal("corr-pronto", record!.CorrelationId); // não foi regenerado.
            }
            finally
            {
                await provider.DisposeAsync();
            }
        }
    }
}
