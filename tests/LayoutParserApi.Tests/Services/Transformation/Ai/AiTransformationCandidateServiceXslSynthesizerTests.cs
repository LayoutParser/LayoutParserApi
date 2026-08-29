using System.Net;

using LayoutParserApi.Services.Transformation.Ai;
using LayoutParserApi.Services.XmlAnalysis;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Tests.Services.Transformation.Ai
{
    /// <summary>
    /// Cobre a integração do motor real (<see cref="IXslSynthesizerService"/>, que encapsula o
    /// <c>RepairOrchestrator</c> de <c>ai/XslSynth.Core</c>) dentro de
    /// <see cref="AiTransformationCandidateService"/> — docs/architecture/design-integracao-repairorchestrator-runtime-2026-08-21.md.
    /// Usa um <see cref="IXslSynthesizerService"/> fake (sem Ollama real) pra isolar o wiring do
    /// loop de correção em si (já coberto por <c>ai/XslSynth.Core.Tests</c>).
    /// </summary>
    public class AiTransformationCandidateServiceXslSynthesizerTests
    {
        [Fact]
        public async Task Quando_synthesizer_converge_usa_o_resultado_dele_e_preenche_GeneratedXslt()
        {
            const string groundTruth = "<nfe><infNFe><campo>valor</campo></infNFe></nfe>";
            var fakeSynthesizer = new FakeXslSynthesizerService(new XslSynthesisResult
            {
                Success = true,
                Converged = true,
                GeneratedXslt = "<xsl:stylesheet>fake</xsl:stylesheet>",
                FinalOutputXml = groundTruth,
                IterationsUsed = 2,
                XsdValid = true
            });

            var service = CriarService(fakeSynthesizer, out var tempDir);
            try
            {
                var ticket = "ticket-synth-converge";
                await service.EnqueueAsync(
                    userId: "usuario-a", ticket: ticket, layoutName: "NFe", layoutGuid: Guid.NewGuid(),
                    mapperGuid: "mapper-x", inputContent: "<qualquer/>", groundTruthXml: groundTruth,
                    cancellationToken: CancellationToken.None);

                var status = await PollUntilAsync(service, "usuario-a", ticket, s => s.Status != AiCandidateStatus.StatusRunning, TimeSpan.FromSeconds(10));

                Assert.Equal(AiCandidateStatus.StatusConverged, status.Status);
                Assert.Equal("<xsl:stylesheet>fake</xsl:stylesheet>", status.Candidate?.GeneratedXslt);
                Assert.Equal(groundTruth, status.Candidate?.TransformedXml);
                Assert.Equal(2, status.Diagnostics?.Iterations);
                Assert.True(fakeSynthesizer.WasCalled);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task Quando_synthesizer_falha_degrada_para_o_loop_legado_XML_direto()
        {
            // Success=false (ex.: entrada não é XML/mapper não resolvido) — o serviço precisa
            // continuar funcionando via o caminho antigo, nunca travar o job.
            const string groundTruth = "<nfe><infNFe><campo>valor</campo></infNFe></nfe>";
            var fakeSynthesizer = new FakeXslSynthesizerService(new XslSynthesisResult
            {
                Success = false,
                Error = "entrada não é XML"
            });

            var service = CriarService(fakeSynthesizer, out var tempDir, respostaModelo: _ => groundTruth);
            try
            {
                var ticket = "ticket-synth-falha-degrada";
                await service.EnqueueAsync(
                    userId: "usuario-a", ticket: ticket, layoutName: "NFe", layoutGuid: Guid.NewGuid(),
                    mapperGuid: "mapper-x", inputContent: "linha-posicional", groundTruthXml: groundTruth,
                    cancellationToken: CancellationToken.None);

                var status = await PollUntilAsync(service, "usuario-a", ticket, s => s.Status != AiCandidateStatus.StatusRunning, TimeSpan.FromSeconds(10));

                // O loop legado (fake Ollama devolvendo o próprio gabarito) converge normalmente.
                Assert.Equal(AiCandidateStatus.StatusConverged, status.Status);
                Assert.True(fakeSynthesizer.WasCalled);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        private static async Task<AiCandidateStatus> PollUntilAsync(
            IAiTransformationCandidateService service, string userId, string ticket, Func<AiCandidateStatus, bool> until, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var status = await service.GetStatusAsync(userId, ticket, CancellationToken.None);
                if (until(status))
                    return status;

                await Task.Delay(50);
            }

            throw new TimeoutException($"Status do ticket {ticket} não atingiu a condição esperada dentro de {timeout}");
        }

        private static IAiTransformationCandidateService CriarService(
            IXslSynthesizerService synthesizer, out string tempStorePath, Func<string, string>? respostaModelo = null)
        {
            respostaModelo ??= _ => "<nfe><infNFe><campo>valor</campo></infNFe></nfe>";
            var handler = new FakeOllamaHandler(respostaModelo);
            var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScoped<XmlDocumentTypeDetector>();
            services.AddScoped<XsdValidationService>();
            services.AddScoped<XmlAnalysisService>();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            services.AddSingleton(synthesizer); // fake do motor novo
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
                Options.Create(new AiTransformationCandidateOptions { MaxIterations = 3, MaxIterationsFallback = 2, SanityTimeoutMinutes = 1, StorePath = tempStorePath }),
                scopeFactory,
                store,
                new AiFallbackSuppressionGate());
        }

        private sealed class FakeXslSynthesizerService : IXslSynthesizerService
        {
            private readonly XslSynthesisResult _result;
            public bool WasCalled { get; private set; }

            public FakeXslSynthesizerService(XslSynthesisResult result) => _result = result;

            public Task<XslSynthesisResult> SynthesizeAsync(
                string mapperGuid, string inputXml, string groundTruthXml, int maxIterations, string? layoutName,
                CancellationToken cancellationToken)
            {
                WasCalled = true;
                return Task.FromResult(_result);
            }
        }

        private sealed class FakeOllamaHandler : HttpMessageHandler
        {
            private readonly Func<string, string> _respostaModelo;

            public FakeOllamaHandler(Func<string, string> respostaModelo) => _respostaModelo = respostaModelo;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult() ?? "{}";
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var prompt = doc.RootElement.TryGetProperty("prompt", out var p) ? p.GetString() ?? "" : "";

                var xml = _respostaModelo(prompt);
                var payload = System.Text.Json.JsonSerializer.Serialize(new { response = xml });

                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
                };
                return Task.FromResult(response);
            }
        }
    }
}
