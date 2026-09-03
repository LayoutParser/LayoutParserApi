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
        public async Task Sem_gabarito_dispara_fallback_automatico_via_ollama()
        {
            // Design docs/architecture/design-fallback-ia-automatico-2026-08-16.md §6: sem gabarito
            // sysmiddle, EnqueueAsync não fica mais "not-applicable" na hora — dispara o modo
            // fallback (Estado A), que também chama o Ollama (só o critério de convergência muda).
            var chamouOllama = false;
            var service = CriarService(_ => { chamouOllama = true; return "<nfe>candidato do fallback</nfe>"; }, out var tempDir, out _);

            try
            {
                await service.EnqueueAsync(
                    userId: "usuario-a",
                    ticket: "ticket-sem-gabarito",
                    layoutName: "NFe",
                    layoutGuid: Guid.NewGuid(),
                    mapperGuid: "mapper-x",
                    inputContent: "linha-posicional",
                    groundTruthXml: null,
                    cancellationToken: CancellationToken.None);

                var status = await PollUntilAsync(service, "usuario-a", "ticket-sem-gabarito", s => s.Status != AiCandidateStatus.StatusRunning, TimeSpan.FromSeconds(10));

                Assert.True(chamouOllama);
                Assert.NotEqual(AiCandidateStatus.StatusNotApplicable, status.Status);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task Fallback_sem_gabarito_usa_MaxIterationsFallback_e_registra_cooldown_ao_falhar()
        {
            // Sem schema XSD real disponível no ambiente de teste, TryValidateXsdAsync nunca
            // valida com sucesso — o loop sempre esgota as iterações. Isso é determinístico o
            // bastante para testar (a) o teto de MaxIterationsFallback (2, mais conservador que os
            // 3 do modo com gabarito) e (b) o registro de cooldown no gate ao falhar (§5 do desenho).
            var iteracoesChamadas = 0;
            var layoutGuid = Guid.NewGuid();
            var gate = new AiFallbackSuppressionGate();
            var service = CriarService(_ => { iteracoesChamadas++; return "<qualquer>candidato</qualquer>"; }, out var tempDir, out _, gate);

            try
            {
                Assert.False(gate.IsInCooldown(layoutGuid, out _));

                await service.EnqueueAsync(
                    userId: "usuario-a",
                    ticket: "ticket-fallback-falha",
                    layoutName: "NFe",
                    layoutGuid: layoutGuid,
                    mapperGuid: "mapper-x",
                    inputContent: "linha-posicional",
                    groundTruthXml: "   ", // só whitespace também conta como "sem gabarito"
                    cancellationToken: CancellationToken.None);

                var status = await PollUntilAsync(service, "usuario-a", "ticket-fallback-falha", s => s.Status != AiCandidateStatus.StatusRunning, TimeSpan.FromSeconds(10));

                Assert.Equal(AiCandidateStatus.StatusFailed, status.Status);
                Assert.False(status.Diagnostics?.HasGroundTruth);
                Assert.Equal(2, status.Diagnostics?.Iterations); // MaxIterationsFallback default
                Assert.Equal(2, iteracoesChamadas);
                Assert.True(gate.IsInCooldown(layoutGuid, out _)); // §5 — cooldown registrado ao falhar
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
                    userId: "usuario-a",
                    ticket: ticket,
                    layoutName: "NFe",
                    layoutGuid: Guid.NewGuid(),
                    mapperGuid: "mapper-x",
                    inputContent: "linha-posicional",
                    groundTruthXml: groundTruth,
                    cancellationToken: CancellationToken.None);

                // EnqueueAsync é fire-and-forget: aguarda o job em background terminar (poll com teto).
                var status = await PollUntilAsync(service, "usuario-a", ticket, s => s.Status != AiCandidateStatus.StatusRunning, TimeSpan.FromSeconds(10));

                Assert.Equal(AiCandidateStatus.StatusConverged, status.Status);
                Assert.NotNull(status.Candidate);
                Assert.Equal("ia", status.Candidate!.Pathway);
                Assert.Equal("ia-mapper-x", status.Candidate.CandidateId);
                Assert.Equal(0, status.Diagnostics?.RemainingDiffs);
                // Modo COM gabarito (Issue #40): HasGroundTruth deve ficar true — é o sinal de
                // contrato que diferencia este candidato do fallback automático sem gabarito.
                Assert.True(status.Diagnostics?.HasGroundTruth);
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
                await service.EnqueueAsync("usuario-a", ticket, "NFe", Guid.NewGuid(), "mapper-x", "linha", groundTruth, CancellationToken.None);
                var status = await PollUntilAsync(service, "usuario-a", ticket, s => s.Status != AiCandidateStatus.StatusRunning, TimeSpan.FromSeconds(10));

                Assert.Equal(AiCandidateStatus.StatusConverged, status.Status);
                Assert.Null(status.Candidate!.Score);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task Usuario_nao_consegue_ler_status_de_ticket_de_outro_usuario()
        {
            // Issue #92 — regressão de isolamento: a store era chaveada só por ticket, então
            // qualquer usuário que soubesse (ou adivinhasse) o ticket de outro conseguia ler o
            // status/candidato dele. Crítico com a issue #93 abrindo ia-status além do papel admin.
            const string groundTruth = "<nfe><infNFe><campo>valor</campo></infNFe></nfe>";
            var service = CriarService(_ => groundTruth, out var tempDir);

            try
            {
                var ticket = "ticket-isolado";
                await service.EnqueueAsync("usuario-dono", ticket, "NFe", Guid.NewGuid(), "mapper-x", "linha", groundTruth, CancellationToken.None);

                // O próprio dono consegue consultar normalmente.
                var statusDono = await PollUntilAsync(service, "usuario-dono", ticket, s => s.Status != AiCandidateStatus.StatusRunning, TimeSpan.FromSeconds(10));
                Assert.Equal(AiCandidateStatus.StatusConverged, statusDono.Status);

                // Outro usuário, mesmo ticket: nunca deve enxergar o status/candidato do dono —
                // o contrato é "comporta-se como inexistente" (o controller traduz isso em 404).
                var statusOutroUsuario = await service.GetStatusAsync("usuario-intruso", ticket, CancellationToken.None);
                Assert.Equal(AiCandidateStatus.StatusNotFound, statusOutroUsuario.Status);
                Assert.Null(statusOutroUsuario.Candidate);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task Instrucao_customizada_do_usuario_aparece_no_prompt_e_saida_ainda_passa_pelo_diff_canonico()
        {
            // Issue #98: a instrução customizada deve chegar ao prompt final (complementar, após o
            // prompt padrão), mas o candidato só converge se também bater o diff canônico contra o
            // gabarito — a instrução não pode virar um atalho que dispensa o verificador
            // determinístico (CanonicalDiffer), reforçando a mitigação de prompt injection da issue.
            const string groundTruth = "<nfe><infNFe><campo>valor</campo></infNFe></nfe>";
            const string instrucao = "Prefira nomes de tag em minúsculas.";
            string? promptCapturado = null;

            var instructionStore = new AiUserInstructionStore();
            instructionStore.Set("usuario-a", instrucao);

            var service = CriarService(prompt =>
            {
                promptCapturado = prompt;
                return groundTruth;
            }, out var tempDir, out _, userInstructionStore: instructionStore);

            try
            {
                var ticket = "ticket-prompt-customizado";
                await service.EnqueueAsync("usuario-a", ticket, "NFe", Guid.NewGuid(), "mapper-x", "linha", groundTruth, CancellationToken.None);
                var status = await PollUntilAsync(service, "usuario-a", ticket, s => s.Status != AiCandidateStatus.StatusRunning, TimeSpan.FromSeconds(10));

                Assert.Equal(AiCandidateStatus.StatusConverged, status.Status);
                Assert.NotNull(promptCapturado);
                Assert.Contains("INSTRUÇÃO ADICIONAL DO USUÁRIO", promptCapturado);
                Assert.Contains(instrucao, promptCapturado);
                // A instrução vem depois do prompt de sistema fixo — nunca antes/substituindo.
                Assert.True(promptCapturado!.IndexOf("Você é um especialista", StringComparison.Ordinal)
                    < promptCapturado.IndexOf("INSTRUÇÃO ADICIONAL DO USUÁRIO", StringComparison.Ordinal));
                Assert.Equal(0, status.Diagnostics?.RemainingDiffs);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task Sem_instrucao_customizada_prompt_nao_ganha_secao_adicional()
        {
            const string groundTruth = "<nfe><infNFe><campo>valor</campo></infNFe></nfe>";
            string? promptCapturado = null;

            var service = CriarService(prompt =>
            {
                promptCapturado = prompt;
                return groundTruth;
            }, out var tempDir, out _, userInstructionStore: new AiUserInstructionStore());

            try
            {
                var ticket = "ticket-sem-prompt-customizado";
                await service.EnqueueAsync("usuario-a", ticket, "NFe", Guid.NewGuid(), "mapper-x", "linha", groundTruth, CancellationToken.None);
                await PollUntilAsync(service, "usuario-a", ticket, s => s.Status != AiCandidateStatus.StatusRunning, TimeSpan.FromSeconds(10));

                Assert.NotNull(promptCapturado);
                Assert.DoesNotContain("INSTRUÇÃO ADICIONAL DO USUÁRIO", promptCapturado);
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

        /// <summary>Monta o serviço com um HttpClient falso (sem rede real) simulando o Ollama.</summary>
        private static IAiTransformationCandidateService CriarService(Func<string, string> respostaModelo, out string tempStorePath)
            => CriarService(respostaModelo, out tempStorePath, out _);

        private static IAiTransformationCandidateService CriarService(
            Func<string, string> respostaModelo, out string tempStorePath, out IAiFallbackSuppressionGate gate,
            IAiFallbackSuppressionGate? gateOverride = null, AiUserInstructionStore? userInstructionStore = null)
        {
            var handler = new FakeOllamaHandler(respostaModelo);
            var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScoped<XmlDocumentTypeDetector>();
            services.AddScoped<XsdValidationService>();
            services.AddScoped<XmlAnalysisService>();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            tempStorePath = Path.Combine(Path.GetTempPath(), "lpapi-ai-tests-" + Guid.NewGuid().ToString("N"));

            var store = new AiCandidateStore(
                NullLogger<AiCandidateStore>.Instance,
                Options.Create(new AiTransformationCandidateOptions { StorePath = tempStorePath }));

            gate = gateOverride ?? new AiFallbackSuppressionGate();

            return new AiTransformationCandidateService(
                NullLogger<AiTransformationCandidateService>.Instance,
                httpClient,
                Options.Create(new OllamaOptions { Url = "http://fake-ollama.local", Model = "fake-model" }),
                Options.Create(new AiTransformationCandidateOptions { MaxIterations = 3, MaxIterationsFallback = 2, SanityTimeoutMinutes = 1, StorePath = tempStorePath }),
                scopeFactory,
                store,
                gate,
                userInstructionStore ?? new AiUserInstructionStore(),
                CreateSessionStore());
        }

        // Issue #102: AiTransformationCandidateService agora grava historico terminal via
        // SqlAiUserSessionStore. Config vazia -> connection string invalida, mas o store degrada
        // (try/catch interno loga Warning e segue) - suficiente para os testes existentes, que nao
        // cobrem persistencia de historico em si.
        private static LayoutParserApi.Services.Database.SqlAiUserSessionStore CreateSessionStore()
            => new(NullLogger<LayoutParserApi.Services.Database.SqlAiUserSessionStore>.Instance,
                   new ConfigurationBuilder().Build(),
                   Microsoft.Extensions.Options.Options.Create(new LayoutParserApi.Services.Database.AiUserSessionHistoryOptions()));

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
