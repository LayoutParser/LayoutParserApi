using System.Net;

using LayoutParserApi.Models.Entities;
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

        /// <summary>
        /// Fecha o gap documentado em docs/architecture/decisao-pendente-input-xml-repairorchestrator-
        /// 2026-08-29.md: prova que o <c>ParsedField</c> produzido pelo PARSER REAL da API
        /// (<see cref="LayoutParserApi.Services.Implementations.LayoutParserService"/>, não um mock)
        /// chega intacto até <see cref="IXslSynthesizerService.SynthesizeAsync"/> — e que
        /// <see cref="ParsedFieldRootTreeBuilder"/> consegue montar um <c>XDocument input</c> real a
        /// partir dele, no mesmo dialeto que <c>RepairOrchestratorXslSynthesizerService</c> usa em
        /// produção.
        /// </summary>
        [Fact]
        public async Task ParsedFields_do_parser_real_chegam_ao_synthesizer_e_ParsedFieldRootTreeBuilder_monta_o_ROOT()
        {
            const string groundTruth = "<nfe><infNFe><campo>valor</campo></infNFe></nfe>";
            var fakeSynthesizer = new FakeXslSynthesizerService(new XslSynthesisResult
            {
                Success = true,
                Converged = true,
                GeneratedXslt = "<xsl:stylesheet>fake</xsl:stylesheet>",
                FinalOutputXml = groundTruth,
                IterationsUsed = 1,
                XsdValid = true
            });

            // ── Parse posicional REAL (mesmo fixture de LineInfoAdditiveSignalsTests) ──────────
            var parsingResult = await ParseTxtRealAsync(
                LayoutMqDe20CharsParaSynthesizer, "000001000AAAAAAAAAAA");
            Assert.True(parsingResult.Success, parsingResult.ErrorMessage);
            Assert.NotEmpty(parsingResult.ParsedFields);

            var service = CriarService(fakeSynthesizer, out var tempDir);
            try
            {
                var ticket = "ticket-synth-parsedfields-reais";
                await service.EnqueueAsync(
                    userId: "usuario-a", ticket: ticket, layoutName: "NFe", layoutGuid: Guid.NewGuid(),
                    mapperGuid: "mapper-x", inputContent: "000001000AAAAAAAAAAA", groundTruthXml: groundTruth,
                    cancellationToken: CancellationToken.None, parsedFields: parsingResult.ParsedFields);

                await PollUntilAsync(service, "usuario-a", ticket, s => s.Status != AiCandidateStatus.StatusRunning, TimeSpan.FromSeconds(10));

                // O motor novo foi disparado E recebeu os ParsedFields reais (não null, não vazio).
                Assert.True(fakeSynthesizer.WasCalled);
                Assert.NotNull(fakeSynthesizer.LastParsedFields);
                Assert.Equal(parsingResult.ParsedFields.Count, fakeSynthesizer.LastParsedFields!.Count);
                Assert.Contains(fakeSynthesizer.LastParsedFields, f => f.FieldName == "DADO" && f.Value.Trim() == "AAAAAAAAAAA");

                // ── Disparo real da construção do XDocument (não mockado) ───────────────────────
                var built = ParsedFieldRootTreeBuilder.Build(fakeSynthesizer.LastParsedFields);
                Assert.NotNull(built.Root);
                Assert.Null(built.Motivo);
                var linha = built.Root!.Root!.Element("LINHA000");
                Assert.NotNull(linha);
                Assert.Equal("AAAAAAAAAAA", linha!.Element("DADO")?.Value);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        private const string LayoutMqDe20CharsParaSynthesizer = """
            <?xml version="1.0" encoding="utf-8"?>
            <LayoutVO>
              <LayoutGuid>LAY_TESTE_MQ_SYNTH</LayoutGuid>
              <LayoutType>MQSeries</LayoutType>
              <Name>LAY_TESTE_MQ_SYNTH</Name>
              <LimitOfCaracters>20</LimitOfCaracters>
              <WithBreakLines>false</WithBreakLines>
              <Elements>
                <Element type="LineElementVO">
                  <ElementGuid>ELM_LINHA000</ElementGuid>
                  <Name>LINHA000</Name>
                  <Sequence>1</Sequence>
                  <InitialValue>000</InitialValue>
                  <Elements>
                    <Element type="FieldElementVO">
                      <ElementGuid>FLD_SEQUENCIA</ElementGuid>
                      <Name>Sequencia</Name>
                      <Sequence>1</Sequence>
                      <LengthField>6</LengthField>
                    </Element>
                    <Element type="FieldElementVO">
                      <ElementGuid>FLD_DADO</ElementGuid>
                      <Name>DADO</Name>
                      <Sequence>2</Sequence>
                      <LengthField>11</LengthField>
                    </Element>
                  </Elements>
                </Element>
              </Elements>
            </LayoutVO>
            """;

        /// <summary>Mesma infraestrutura mínima de <c>LineInfoAdditiveSignalsTests.ParseAsync</c> —
        /// invoca o <c>LayoutParserService</c> REAL, não um mock/stub de parsing.</summary>
        private static async Task<Models.Parsing.ParsingResult> ParseTxtRealAsync(string layoutXml, string documento)
        {
            var techLogger = new NoOpTechLoggerLocal();
            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ML:LearningDataPath"] = Path.Combine(Path.GetTempPath(), "lp-tests", "DocumentPatterns"),
                    ["ML:TrainingSamplesPath"] = Path.Combine(Path.GetTempPath(), "lp-tests", "TrainingSamples")
                })
                .Build();

            var service = new LayoutParserApi.Services.Implementations.LayoutParserService(
                techLogger,
                new NoOpAuditLoggerLocal(),
                new LayoutParserApi.Services.Parsing.Implementations.LineSplitter(techLogger),
                new LayoutParserApi.Services.Parsing.Implementations.LayoutValidator(techLogger),
                new LayoutParserApi.Services.Parsing.Implementations.LayoutNormalizer(),
                new LayoutParserApi.Services.Validation.DocumentValidationService(techLogger, NullLogger<LayoutParserApi.Services.Validation.DocumentValidationService>.Instance),
                new LayoutParserApi.Services.Validation.DocumentMLValidationService(techLogger, NullLogger<LayoutParserApi.Services.Validation.DocumentMLValidationService>.Instance, config),
                NullLogger<LayoutParserApi.Services.Implementations.LayoutParserService>.Instance);

            using var layoutStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(layoutXml));
            using var txtStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(documento));
            return await service.ParseAsync(layoutStream, txtStream);
        }

        private sealed class NoOpTechLoggerLocal : LayoutParserApi.Services.Interfaces.ITechLogger
        {
            public void LogTechnical(LayoutParserApi.Models.Logging.LogEntry entry) { }
        }

        private sealed class NoOpAuditLoggerLocal : LayoutParserApi.Services.Interfaces.IAuditLogger
        {
            public void LogAudit(LayoutParserApi.Models.Logging.AuditLogEntry entry) { }
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
                new AiFallbackSuppressionGate(),
                new AiUserInstructionStore(),
                CreateSessionStore());
        }

        // Issue #102: mesma justificativa de AiTransformationCandidateServiceTests.CreateSessionStore.
        private static LayoutParserApi.Services.Database.SqlAiUserSessionStore CreateSessionStore()
            => new(NullLogger<LayoutParserApi.Services.Database.SqlAiUserSessionStore>.Instance,
                   new ConfigurationBuilder().Build());

        private sealed class FakeXslSynthesizerService : IXslSynthesizerService
        {
            private readonly XslSynthesisResult _result;
            public bool WasCalled { get; private set; }
            public IReadOnlyList<ParsedField>? LastParsedFields { get; private set; }

            public FakeXslSynthesizerService(XslSynthesisResult result) => _result = result;

            public Task<XslSynthesisResult> SynthesizeAsync(
                string mapperGuid, string inputXml, string groundTruthXml, int maxIterations, string? layoutName,
                CancellationToken cancellationToken, IReadOnlyList<ParsedField>? parsedFields = null)
            {
                WasCalled = true;
                LastParsedFields = parsedFields;
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
