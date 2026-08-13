using LayoutParserApi.Models.Entities;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Transformation.Ai;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Transformation
{
    /// <summary>
    /// Cobertura do pathway IA de <c>execute-candidates</c> (Issue #40): job assíncrono
    /// (<c>EnqueueAsync</c>/<c>GetStatusAsync</c>), sem depender de um Ollama real — os cenários
    /// aqui exercitam os caminhos de degrade gracioso (mapeador ausente, input não-XML) que
    /// nunca chegam a chamar o loop RAG, e a consulta de status por ticket desconhecido.
    /// </summary>
    public class AiTransformationCandidateServiceTests
    {
        private const string Ticket = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.LAY_TESTE";

        [Fact]
        public async Task GetStatusAsync_para_ticket_desconhecido_devolve_not_found()
        {
            var service = CriarService(new MapperCacheServiceFalso());

            var status = await service.GetStatusAsync("ticket-inexistente", CancellationToken.None);

            Assert.Equal("not-found", status.Status);
        }

        [Fact]
        public async Task EnqueueAsync_fica_running_e_depois_converge_para_not_applicable_sem_mapeador()
        {
            // Nenhum mapeador cadastrado com esse guid — o job deve terminar "not-applicable"
            // (degrade gracioso, dotnet-standards.md §Resiliência) e NUNCA lançar para o chamador.
            var service = CriarService(new MapperCacheServiceFalso());

            await service.EnqueueAsync(
                Ticket,
                "LAY_TESTE",
                Guid.NewGuid(),
                mapperGuid: "MAPPER_INEXISTENTE",
                inputContent: "<Nota><campo>1</campo></Nota>",
                groundTruthXml: "<Nota><campo>1</campo></Nota>",
                cancellationToken: CancellationToken.None);

            var status = await AguardarConclusao(service, Ticket);

            Assert.Equal("not-applicable", status.Status);
            Assert.Null(status.Candidate);
            Assert.NotNull(status.Diagnostics?.LastError);
        }

        [Fact]
        public async Task EnqueueAsync_com_input_nao_xml_termina_not_applicable()
        {
            var mapperCache = new MapperCacheServiceFalso();
            mapperCache.Mappers.Add(new Mapper
            {
                MapperGuid = "M1",
                DecryptedContent = "<MapperVO><TargetLayoutGuid>G</TargetLayoutGuid></MapperVO>"
            });

            var service = CriarService(mapperCache);

            await service.EnqueueAsync(
                Ticket,
                "LAY_TESTE",
                Guid.NewGuid(),
                mapperGuid: "M1",
                inputContent: "000001DADOS POSICIONAIS SEM XML",
                groundTruthXml: "<Nota/>",
                cancellationToken: CancellationToken.None);

            var status = await AguardarConclusao(service, Ticket);

            Assert.Equal("not-applicable", status.Status);
        }

        [Fact]
        public async Task EnqueueAsync_sem_ticket_nao_lanca_e_nao_registra_job()
        {
            var service = CriarService(new MapperCacheServiceFalso());

            await service.EnqueueAsync(
                ticket: "",
                layoutName: "LAY_TESTE",
                layoutGuid: Guid.NewGuid(),
                mapperGuid: "M1",
                inputContent: "<Nota/>",
                groundTruthXml: "<Nota/>",
                cancellationToken: CancellationToken.None);

            var status = await service.GetStatusAsync("", CancellationToken.None);
            Assert.Equal("not-found", status.Status);
        }

        private static AiTransformationCandidateService CriarService(ICachedMapperService mapperCache)
        {
            var services = new ServiceCollection();
            services.AddSingleton(mapperCache);
            var provider = services.BuildServiceProvider();

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ML:AiTransformationCandidatesPath"] = Path.Combine(Path.GetTempPath(), "AiTransformationCandidatesTests", Guid.NewGuid().ToString())
                }).Build();

            return new AiTransformationCandidateService(
                NullLogger<AiTransformationCandidateService>.Instance,
                provider.GetRequiredService<IServiceScopeFactory>(),
                configuration);
        }

        /// <summary>Job roda em <c>Task.Run</c> — aguarda (com teto curto) até sair de "running".</summary>
        private static async Task<AiCandidateStatus> AguardarConclusao(IAiTransformationCandidateService service, string ticket)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            AiCandidateStatus status;
            do
            {
                status = await service.GetStatusAsync(ticket, CancellationToken.None);
                if (status.Status != "running")
                    return status;

                await Task.Delay(50);
            } while (DateTime.UtcNow < deadline);

            return status;
        }

        private sealed class MapperCacheServiceFalso : ICachedMapperService
        {
            public List<Mapper> Mappers { get; } = new();

            public Task<List<Mapper>> GetAllMappersAsync() => Task.FromResult(Mappers);

            public Task<List<Mapper>> GetMappersByInputLayoutGuidAsync(string inputLayoutGuid) =>
                Task.FromResult(Mappers.Where(m => m.InputLayoutGuid == inputLayoutGuid).ToList());

            public Task<List<Mapper>> GetMappersByTargetLayoutGuidAsync(string targetLayoutGuid) =>
                Task.FromResult(Mappers.Where(m => m.TargetLayoutGuid == targetLayoutGuid).ToList());

            public Task RefreshCacheFromDatabaseAsync() => Task.CompletedTask;
        }
    }
}
