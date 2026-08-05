using System.Text;
using System.Text.Json;

using LayoutParserApi.Controllers;
using LayoutParserApi.Models.Transformation;
using LayoutParserApi.Services.Transformation.LowCode;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Tests.Controllers
{
    /// <summary>
    /// Endpoints de consulta das transformações
    /// (<c>spec-entrega-da-transformacao-no-parse.md</c> §2.5): manifesto e corpo.
    ///
    /// <para>O que se trava aqui é a <b>borda</b>: ticket vindo do cliente vira nome de arquivo, e a
    /// resposta é serializada direto para o front. Dois invariantes não-negociáveis — validar o
    /// ticket por charset (nunca sanitizar por remoção) e nunca devolver caminho absoluto do
    /// servidor no payload.</para>
    /// </summary>
    public class ParseTransformationsEndpointsTests
    {
        private const string ShaInexistente = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private const string LayoutGuid = "79adf76a-4b07-428c-90d7-3c39d1296a5d";
        private const string Documento = "000001DADOS DO DOCUMENTO POSICIONAL";

        // ── 400: ticket fora do formato ─────────────────────────────────────────────────────────

        [Theory]
        [InlineData("..")]
        [InlineData("../../windows/win.ini")]
        [InlineData(@"..\..\appsettings.json")]
        [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.../..")]
        [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF.79adf76a")]
        [InlineData("nao-e-ticket")]
        [InlineData("")]
        public async Task Ticket_fora_do_charset_responde_400_no_manifesto(string ticket)
        {
            var (controller, _, _) = CriarController();

            var resposta = await controller.GetTransformations(ticket);

            Assert.IsType<BadRequestObjectResult>(resposta);
        }

        [Theory]
        [InlineData("..")]
        [InlineData(@"..\..\appsettings.json")]
        [InlineData("nao-e-ticket")]
        public async Task Ticket_fora_do_charset_responde_400_no_corpo(string ticket)
        {
            var (controller, _, _) = CriarController();

            var resposta = await controller.GetTransformationCandidate(ticket, "M1");

            Assert.IsType<BadRequestObjectResult>(resposta);
        }

        /// <summary>
        /// Recusar não basta: nada pode ser lido fora da raiz do store no caminho da tentativa.
        /// </summary>
        [Fact]
        public async Task Tentativa_de_travessia_nao_le_arquivo_fora_do_store()
        {
            var (controller, _, raiz) = CriarController();

            var fora = Path.Combine(Directory.GetParent(raiz)!.FullName, $"fora_{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(fora, "{\"segredo\":true}", Encoding.UTF8);

            try
            {
                var alvo = Path.GetFileNameWithoutExtension(fora);

                foreach (var ticket in new[] { $"../{alvo}", $@"..\{alvo}", $"{ShaInexistente}.../{alvo}" })
                    Assert.IsType<BadRequestObjectResult>(await controller.GetTransformations(ticket));

                Assert.Equal("{\"segredo\":true}", await File.ReadAllTextAsync(fora));
            }
            finally
            {
                File.Delete(fora);
            }
        }

        // ── 404: ticket válido, execução inexistente ────────────────────────────────────────────

        [Fact]
        public async Task Ticket_valido_sem_execucao_responde_404()
        {
            var (controller, _, _) = CriarController();

            Assert.IsType<NotFoundObjectResult>(await controller.GetTransformations($"{ShaInexistente}.{LayoutGuid}"));
            Assert.IsType<NotFoundObjectResult>(await controller.GetTransformationCandidate($"{ShaInexistente}.{LayoutGuid}", "M1"));
        }

        [Fact]
        public async Task Candidato_inexistente_no_manifesto_responde_404()
        {
            var (controller, store, raiz) = CriarController();
            await PersistirExecucaoAsync(store, raiz, "<nfe/>");

            var resposta = await controller.GetTransformationCandidate(TicketDoDocumento(), "MAPPER_QUE_NAO_EXISTE");

            Assert.IsType<NotFoundObjectResult>(resposta);
        }

        // ── 200: manifesto e corpo ──────────────────────────────────────────────────────────────

        [Fact]
        public async Task Manifesto_responde_com_vocabulario_do_execute_candidates()
        {
            var (controller, store, raiz) = CriarController();
            await PersistirExecucaoAsync(store, raiz, "<nfe>conteudo</nfe>");

            var ok = Assert.IsType<OkObjectResult>(await controller.GetTransformations(TicketDoDocumento()));
            var json = Serializar(ok.Value);

            Assert.Equal("completed", Ler<string>(ok.Value, "status"));
            Assert.False(Ler<bool>(ok.Value, "partial"));

            // Vocabulário do TransformationCandidate (o que o front já tipa) + descritores do
            // candidato low-code: superconjunto dos dois shapes, não um terceiro dialeto (§3.3).
            Assert.Contains("\"candidateId\":\"sysmiddle-M1\"", json);
            Assert.Contains("\"pathway\":\"sysmiddle\"", json);
            Assert.Contains("\"mapperGuid\":\"M1\"", json);
            Assert.Contains($"\"outputLength\":{"<nfe>conteudo</nfe>".Length}", json);

            // Manifesto é o lado "consultado sempre": vai sem XML (split do §2.4).
            Assert.DoesNotContain("<nfe>", json);
        }

        [Fact]
        public async Task Corpo_do_candidato_responde_com_o_xml()
        {
            var (controller, store, raiz) = CriarController();
            await PersistirExecucaoAsync(store, raiz, "<nfe>conteudo</nfe>");

            var ok = Assert.IsType<OkObjectResult>(await controller.GetTransformationCandidate(TicketDoDocumento(), "M1"));

            Assert.Equal("<nfe>conteudo</nfe>", Ler<string>(ok.Value, "outputXml"));
            Assert.Equal("<nfe>conteudo</nfe>".Length, Ler<int>(ok.Value, "outputLength"));
        }

        [Fact]
        public async Task Execucao_em_andamento_responde_processing()
        {
            var (controller, store, _) = CriarController();
            await store.WriteProcessingAsync(LowCodeTransformationStore.ComputeSha256(Documento), LayoutGuid);

            var ok = Assert.IsType<OkObjectResult>(await controller.GetTransformations(TicketDoDocumento()));

            // É isto que mata o rótulo "(processando...)" eterno: agora existe a quem perguntar, e a
            // resposta muda quando o trabalho termina.
            Assert.Equal("processing", Ler<string>(ok.Value, "status"));
        }

        // ── sem vazamento de caminho ────────────────────────────────────────────────────────────

        [Fact]
        public async Task Nenhum_payload_carrega_caminho_absoluto_do_servidor()
        {
            var (controller, store, raiz) = CriarController();

            // Entrada com a mensagem crua que o runner produzia (com caminho) — simula tanto índice
            // antigo quanto qualquer erro que escape do saneamento na escrita.
            var entrada = new LowCodeTransformationIndexEntry
            {
                BaseName = "base",
                DateFolder = "20260805",
                Candidates =
                {
                    new LowCodeTransformationIndexCandidate
                    {
                        MapperGuid = "M1",
                        Success = false,
                        ErrorMessage = @"Runner nao gerou outputFile: C:\inetpub\wwwroot\layoutparser\api\out_ab12.xml"
                    }
                }
            };
            await store.WriteCompletedAsync(LowCodeTransformationStore.ComputeSha256(Documento), LayoutGuid, entrada);

            var ok = Assert.IsType<OkObjectResult>(await controller.GetTransformations(TicketDoDocumento()));
            var json = Serializar(ok.Value);

            Assert.DoesNotContain(@"C:\", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("inetpub", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(raiz, json, StringComparison.OrdinalIgnoreCase);
            // O motivo do erro continua legível — saneamos o caminho, não a informação.
            Assert.Contains("Runner nao gerou outputFile", json);
        }

        // ─────────────────────────────── infraestrutura ───────────────────────────────

        private static string TicketDoDocumento()
            => LowCodeTransformationStore.BuildTicketFromContent(Documento, LayoutGuid)!;

        /// <summary>
        /// Os dois endpoints de consulta dependem só do store, do logger e das opções — as demais
        /// dependências do controller pertencem ao <c>Upload</c> (coberto em
        /// <c>ParseControllerTaxonomyTests</c>) e não são tocadas aqui.
        /// </summary>
        private static (ParseController controller, LowCodeTransformationStore store, string raiz) CriarController()
        {
            var raiz = Path.Combine(Path.GetTempPath(), "lp-tests", "lowcode-endpoints", Guid.NewGuid().ToString("N"));

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ML:LowCodeTransformationsPath"] = raiz
                })
                .Build();

            var opcoes = Options.Create(new LowCodeRunnerOptions());
            var store = new LowCodeTransformationStore(
                NullLogger<LowCodeTransformationStore>.Instance, config, opcoes, redis: null);

            var controller = new ParseController(
                parserService: null!,
                NullLogger<ParseController>.Instance,
                layoutDetector: null!,
                fileStorage: null!,
                learningService: null!,
                config,
                lowCodeAuto: null!,
                opcoes,
                store)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };

            return (controller, store, raiz);
        }

        private static async Task PersistirExecucaoAsync(LowCodeTransformationStore store, string raiz, string xml)
        {
            const string dateFolder = "20260805";
            const string outputFile = "base.cand0_M1.lowcode.xml";

            Directory.CreateDirectory(Path.Combine(raiz, dateFolder));
            await File.WriteAllTextAsync(Path.Combine(raiz, dateFolder, outputFile), xml, Encoding.UTF8);

            await store.WriteCompletedAsync(
                LowCodeTransformationStore.ComputeSha256(Documento),
                LayoutGuid,
                new LowCodeTransformationIndexEntry
                {
                    BaseName = "base",
                    DateFolder = dateFolder,
                    Candidates =
                    {
                        new LowCodeTransformationIndexCandidate
                        {
                            MapperGuid = "M1",
                            MapperName = "MAP_TESTE",
                            Success = true,
                            OutputFile = outputFile,
                            OutputLength = xml.Length
                        }
                    }
                });
        }

        private static string Serializar(object? payload) => JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

        private static T Ler<T>(object? payload, string propriedade)
        {
            Assert.NotNull(payload);

            var info = payload!.GetType().GetProperty(propriedade);
            Assert.True(info is not null, $"O payload não expõe a propriedade '{propriedade}'.");

            return (T)info!.GetValue(payload)!;
        }
    }
}
