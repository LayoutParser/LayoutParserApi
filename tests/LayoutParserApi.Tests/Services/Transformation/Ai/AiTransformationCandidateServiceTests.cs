using System.Net;
using System.Text;
using System.Text.Json;

using LayoutParserApi.Services.Transformation.Ai;
using LayoutParserApi.Services.XmlAnalysis;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Tests.Services.Transformation.Ai
{
    /// <summary>
    /// Issue #40 — regressão do serviço do pathway IA: (a) sem gabarito sysmiddle, o job nem
    /// chega a chamar o Ollama (fica "not-applicable" na hora); (b) com gabarito, o job roda em
    /// background e converge quando o "modelo" (Ollama simulado) devolve exatamente o gabarito.
    /// Nunca atrasa/derruba o chamador — <c>EnqueueAsync</c> sempre retorna imediatamente.
    /// </summary>
    public class AiTransformationCandidateServiceTests
    {
        [Fact]
        public async Task Sem_gabarito_fica_not_applicable_sem_chamar_ollama()
        {
            var chamouOllama = false;
            var service = CriarService(_ => { chamouOllama = true; return "<nfe>não deveria ser gerado</nfe>"; }, out var tempDir);

            try
            {
                await service.EnqueueAsync(
                    ticket: "ticket-sem-gabarito",
                    layoutName: "NFe",
                    layoutGuid: Guid.NewGuid(),
                    mapperGuid: "mapper-x",
                    inputContent: "linha-posicional",
                    groundTruthXml: "",
                    cancellationToken: CancellationToken.None);

                var status = await service.GetStatusAsync("ticket-sem-gabarito", CancellationToken.None);

                Assert.Equal(AiCandidateStatus.StatusNotApplicable, status.Status);
                Assert.False(chamouOllama);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task Com_gabarito_converge_quando_modelo_acerta_de_primeira()
        {
            const string groundTruth = "<nfe><infNFe><campo>valor</campo></infNFe></nfe>";
            var service = CriarService(_ => groundTruth, out var tempDir);

            try
            {
                var ticket = "ticket-converge";
                await service.EnqueueAsync(
                    ticket: ticket,
                    layoutName: "NFe",
                    layoutGuid: Guid.NewGuid(),
                    mapperGuid: "mapper-x",
                    inputContent: "linha-posicional",
                    groundTruthXml: groundTruth,
                    cancellationToken: CancellationToken.None);

                // EnqueueAsync é fire-and-forget: aguarda o job em background terminar (poll com teto).
                var status = await PollUntilAsync(service, ticket, s => s.Status != AiCandidateStatus.StatusRunning, TimeSpan.FromSeconds(10));

                Assert.Equal(AiCandidateStatus.StatusConverged, status.Status);
                Assert.NotNull(status.Candidate);
                Assert.Equal("ia", status.Candidate!.Pathway);
                Assert.Equal("ia-mapper-x", status.Candidate.CandidateId);
                Assert.Equal(0, status.Diagnostics?.RemainingDiffs);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task Candidato_ia_convergido_nao_teria_como_virar_recommended_sem_score()
        {
            // Reforça §2.2 do desenho por outro ângulo: o candidato "ia" nunca carrega Score, então
            // mesmo se algum dia entrasse em candidates[], não competeria no ranking por Score.
            const string groundTruth = "<nfe><infNFe><campo>valor</campo></infNFe></nfe>";
            var service = CriarService(_ => groundTruth, out var tempDir);

            try
            {
                var ticket = "ticket-sem-score";
                await service.EnqueueAsync(ticket, "NFe", Guid.NewGuid(), "mapper-x", "linha", groundTruth, CancellationToken.None);
                var status = await PollUntilAsync(service, ticket, s => s.Status != AiCandidateStatus.StatusRunning, TimeSpan.FromSeconds(10));

                Assert.Equal(AiCandidateStatus.StatusConverged, status.Status);
                Assert.Null(status.Candidate!.Score);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        private static async Task<AiCandidateStatus> PollUntilAsync(
            IAiTransformationCandidateService service, string ticket, Func<AiCandidateStatus, bool> until, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var status = await service.GetStatusAsync(ticket, CancellationToken.None);
                if (until(status))
                    return status;

                await Task.Delay(50);
            }

            throw new TimeoutException($"Status do ticket {ticket} não atingiu a condição esperada dentro de {timeout}");
        }

        /// <summary>Monta o serviço com um HttpClient falso (sem rede real) simulando o Ollama.</summary>
        private static IAiTransformationCandidateService CriarService(Func<string, string> respostaModelo, out string tempStorePath)
        {
            var handler = new FakeOllamaHandler(respostaModelo);
            var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScoped<XmlDocumentTypeDetector>();
            services.AddScoped<XsdValidationService>();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            tempStorePath = Path.Combine(Path.GetTempPath(), "lpapi-ai-tests-" + Guid.NewGuid().ToString("N"));

            var store = new AiCandidateStore(
                NullLogger<AiCandidateStore>.Instance,
                Options.Create(new AiTransformationCandidateOptions { StorePath = tempStorePath }));

            return new AiTransformationCandidateService(
                NullLogger<AiTransformationCandidateService>.Instance,
                httpClient,
                Options.Create(new OllamaOptions { Url = "http://fake-ollama.local", Model = "fake-model" }),
                Options.Create(new AiTransformationCandidateOptions { MaxIterations = 3, SanityTimeoutMinutes = 1, StorePath = tempStorePath }),
                scopeFactory,
                store);
        }

        private class FakeOllamaHandler : HttpMessageHandler
        {
            private readonly Func<string, string> _respostaModelo;

            public FakeOllamaHandler(Func<string, string> respostaModelo) => _respostaModelo = respostaModelo;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult() ?? "{}";
                using var doc = JsonDocument.Parse(body);
                var prompt = doc.RootElement.TryGetProperty("prompt", out var p) ? p.GetString() ?? "" : "";

                var xml = _respostaModelo(prompt);
                var payload = JsonSerializer.Serialize(new { response = xml });

                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                return Task.FromResult(response);
            }
        }
    }
}
