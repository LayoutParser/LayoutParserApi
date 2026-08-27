using System.Text;
using System.Text.Json;

using LayoutParserApi.Models.Transformation;
using LayoutParserApi.Services.Transformation.LowCode;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Tests.Transformation
{
    /// <summary>
    /// Store/índice de transformações low-code (<c>spec-entrega-da-transformacao-no-parse.md</c> §2).
    ///
    /// <para>Dois invariantes são o coração destes testes:</para>
    /// <list type="number">
    /// <item>o ticket é <b>validado por charset</b>, nunca sanitizado — e nenhuma leitura escapa da
    /// raiz do store, mesmo que o índice em disco aponte para fora;</item>
    /// <item><b>tudo funciona sem Redis</b> (todos os testes rodam com <c>redis: null</c>, que é o
    /// cenário provável em produção — §4.3).</item>
    /// </list>
    /// </summary>
    public class LowCodeTransformationStoreTests
    {
        private const string ShaValido = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private const string LayoutGuidValido = "79adf76a-4b07-428c-90d7-3c39d1296a5d";

        // ── ticket: validar, não sanitizar ──────────────────────────────────────────────────────

        [Fact]
        public void Ticket_valido_e_quebrado_em_sha_e_layoutGuid()
        {
            var ok = LowCodeTransformationStore.TryParseTicket($"{ShaValido}.{LayoutGuidValido}", out var sha, out var guid);

            Assert.True(ok);
            Assert.Equal(ShaValido, sha);
            Assert.Equal(LayoutGuidValido, guid);
        }

        /// <summary>
        /// Toda entrada fora do charset é RECUSADA inteira. Repare que nenhum caso aqui é
        /// "consertado": não existe versão sanitizada de <c>..</c> que devesse ser aceita.
        /// </summary>
        [Theory]
        [InlineData("..")]
        [InlineData("../../windows/win.ini")]
        [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef...")]
        [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef./..")]
        [InlineData(@"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.\..\segredo")]
        [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef/79adf76a")]
        [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF.79adf76a")] // sha em maiúsculas
        [InlineData("0123456789abcdef.79adf76a")]                                                  // sha curto
        [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]           // sem layoutGuid
        [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.")]          // layoutGuid vazio
        [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.a.b")]       // ponto extra
        [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.a b")]       // espaço
        [InlineData("")]
        [InlineData(null)]
        public void Ticket_fora_do_charset_e_recusado(string? ticket)
        {
            Assert.False(LowCodeTransformationStore.TryParseTicket(ticket, out var sha, out var guid));
            Assert.Equal("", sha);
            Assert.Equal("", guid);
        }

        [Fact]
        public void Ticket_com_layoutGuid_longo_demais_e_recusado()
        {
            var guidLongo = new string('a', 65);

            Assert.False(LowCodeTransformationStore.TryParseTicket($"{ShaValido}.{guidLongo}", out _, out _));
            Assert.Null(LowCodeTransformationStore.BuildTicket(ShaValido, guidLongo));
        }

        [Fact]
        public void BuildTicket_recusa_layoutGuid_que_nao_volta_pela_validacao()
        {
            // Simetria obrigatória: só emitimos ticket que a leitura aceitaria de volta. Emitir um
            // ticket que o próprio endpoint recusaria seria entregar ao front um beco sem saída.
            Assert.Null(LowCodeTransformationStore.BuildTicket(ShaValido, @"..\..\segredo"));
            Assert.Null(LowCodeTransformationStore.BuildTicket(ShaValido, "guid com espaço"));
            Assert.Null(LowCodeTransformationStore.BuildTicket(ShaValido, ""));
            Assert.NotNull(LowCodeTransformationStore.BuildTicket(ShaValido, LayoutGuidValido));
        }

        [Fact]
        public void Sha_do_ticket_e_o_sha_do_conteudo()
        {
            var ticket = LowCodeTransformationStore.BuildTicketFromContent("conteudo posicional", LayoutGuidValido);

            Assert.NotNull(ticket);
            Assert.Equal($"{LowCodeTransformationStore.ComputeSha256("conteudo posicional")}.{LayoutGuidValido}", ticket);
        }

        // ── travessia de caminho ────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Leitura_nao_escapa_da_raiz_do_store_nem_com_indice_envenenado()
        {
            var (store, raiz) = CriarStore();

            // Arquivo "secreto" FORA do store, no diretório pai.
            var fora = Path.Combine(Directory.GetParent(raiz)!.FullName, $"fora_{Guid.NewGuid():N}.xml");
            await File.WriteAllTextAsync(fora, "<segredo/>", Encoding.UTF8);

            try
            {
                // Índice apontando para fora: mesmo sendo arquivo NOSSO, o caminho é canonicalizado
                // e conferido contra a raiz antes de abrir (defesa em profundidade).
                var entrada = new LowCodeTransformationIndexEntry
                {
                    DateFolder = "..",
                    Candidates = { new LowCodeTransformationIndexCandidate { MapperGuid = "M1", Success = true, OutputFile = Path.GetFileName(fora) } }
                };

                var xml = await store.ReadCandidateXmlAsync(entrada, ShaValido, LayoutGuidValido, entrada.Candidates[0]);

                Assert.Null(xml);
                Assert.Equal("<segredo/>", await File.ReadAllTextAsync(fora)); // intocado
            }
            finally
            {
                File.Delete(fora);
            }
        }

        [Fact]
        public async Task Sha_ou_layoutGuid_invalidos_nao_geram_leitura()
        {
            var (store, _) = CriarStore();

            Assert.Null(await store.ReadEntryAsync("../../etc", LayoutGuidValido));
            Assert.Null(await store.ReadEntryAsync(ShaValido, @"..\..\segredo"));
            Assert.Null(await store.ReadEntryByTicketAsync(".."));
        }

        // ── round-trip em disco (sem Redis) ─────────────────────────────────────────────────────

        [Fact]
        public async Task Escrita_e_leitura_funcionam_so_com_disco()
        {
            var (store, raiz) = CriarStore();
            await PersistirExecucaoAsync(store, raiz, "<nfe/>");

            var entrada = await store.ReadEntryAsync(ShaValido, LayoutGuidValido);

            Assert.NotNull(entrada);
            Assert.Equal(LowCodeTransformationIndexEntry.CompletedStatus, entrada!.Status);
            Assert.False(entrada.Partial);
            var candidato = Assert.Single(entrada.Candidates);
            Assert.Equal("M1", candidato.MapperGuid);
            Assert.True(candidato.Success);

            // O índice vive AO LADO dos artefatos, sem renomear nada do esquema append-only.
            Assert.True(File.Exists(Path.Combine(raiz, "index", $"{ShaValido}.{LayoutGuidValido}.json")));
        }

        [Fact]
        public async Task Processing_e_legivel_mas_nao_serve_de_cache()
        {
            var (store, _) = CriarStore();
            await store.WriteProcessingAsync(ShaValido, LayoutGuidValido);

            var entrada = await store.ReadEntryAsync(ShaValido, LayoutGuidValido);
            Assert.Equal(LowCodeTransformationIndexEntry.ProcessingStatus, entrada!.Status);

            // Legível (o front sabe que ainda está rodando) e ainda assim não é hit de cache.
            Assert.Null(await store.TryGetCachedResultAsync(ShaValido, LayoutGuidValido));
        }

        // ── cache-first: quando é hit e quando NÃO é ────────────────────────────────────────────

        [Fact]
        public async Task Execucao_completa_vira_hit_com_xml_do_disco()
        {
            var (store, raiz) = CriarStore();
            await PersistirExecucaoAsync(store, raiz, "<nfe>conteudo</nfe>");

            var hit = await store.TryGetCachedResultAsync(ShaValido, LayoutGuidValido);

            Assert.NotNull(hit);
            Assert.True(hit!.Applicable);
            var candidato = Assert.Single(hit.Candidates);
            Assert.Equal("<nfe>conteudo</nfe>", candidato.OutputXml);
            Assert.Equal("<nfe>conteudo</nfe>".Length, candidato.OutputLength);
        }

        [Fact]
        public async Task Execucao_parcial_nunca_vira_cache()
        {
            var (store, raiz) = CriarStore();
            await PersistirExecucaoAsync(store, raiz, "<nfe/>", parcial: true);

            // Consultável por ticket (o front vê o que deu tempo de sair)...
            Assert.NotNull(await store.ReadEntryAsync(ShaValido, LayoutGuidValido));
            // ...mas nunca congela o resultado truncado para os próximos uploads idênticos.
            Assert.Null(await store.TryGetCachedResultAsync(ShaValido, LayoutGuidValido));
        }

        [Fact]
        public async Task Execucao_so_com_falhas_nao_vira_cache()
        {
            var (store, raiz) = CriarStore();

            var entrada = new LowCodeTransformationIndexEntry
            {
                BaseName = "base",
                DateFolder = "20260805",
                Candidates = { new LowCodeTransformationIndexCandidate { MapperGuid = "M1", Success = false, ErrorMessage = "falhou" } }
            };
            await store.WriteCompletedAsync(ShaValido, LayoutGuidValido, entrada);

            Assert.Null(await store.TryGetCachedResultAsync(ShaValido, LayoutGuidValido));
        }

        // ── contrato aditivo 2026-08-27: fase "failed" quando nenhum candidato teve sucesso ────

        [Fact]
        public async Task WriteCompletedAsync_com_falhouEstruturalmente_fecha_como_failed()
        {
            var (store, _) = CriarStore();

            var entrada = new LowCodeTransformationIndexEntry
            {
                BaseName = "base",
                DateFolder = "20260805",
                Candidates = { new LowCodeTransformationIndexCandidate { MapperGuid = "M1", Success = false, ErrorMessage = "falhou" } }
            };
            await store.WriteCompletedAsync(ShaValido, LayoutGuidValido, entrada, falhouEstruturalmente: true);

            var lida = await store.ReadEntryAsync(ShaValido, LayoutGuidValido);
            Assert.Equal(LowCodeTransformationIndexEntry.FailedStatus, lida!.Status);
        }

        [Fact]
        public async Task WriteCompletedAsync_com_sucesso_nao_vira_failed_mesmo_se_flag_default()
        {
            var (store, raiz) = CriarStore();
            await PersistirExecucaoAsync(store, raiz, "<nfe/>");

            var lida = await store.ReadEntryAsync(ShaValido, LayoutGuidValido);
            Assert.Equal(LowCodeTransformationIndexEntry.CompletedStatus, lida!.Status);
        }

        [Fact]
        public async Task Entrada_expirada_nao_vira_cache()
        {
            var (store, raiz) = CriarStore(ttlHoras: 2);
            await PersistirExecucaoAsync(store, raiz, "<nfe/>");

            // Envelhece a entrada no disco: pular o runner com base numa execução antiga é decisão
            // diferente de guardar o artefato (o catálogo de mappers pode ter mudado no meio).
            await EnvelhecerIndiceAsync(raiz, horas: 3);

            Assert.Null(await store.TryGetCachedResultAsync(ShaValido, LayoutGuidValido));
            Assert.NotNull(await store.ReadEntryAsync(ShaValido, LayoutGuidValido)); // leitura por ticket continua
        }

        [Fact]
        public async Task Artefato_ausente_derruba_o_hit_inteiro()
        {
            var (store, raiz) = CriarStore();
            await PersistirExecucaoAsync(store, raiz, "<nfe/>");

            foreach (var arquivo in Directory.GetFiles(Path.Combine(raiz, "20260805")))
                File.Delete(arquivo);

            // Meio conjunto é pior que nenhum: melhor recalcular do que devolver candidato sem XML.
            Assert.Null(await store.TryGetCachedResultAsync(ShaValido, LayoutGuidValido));
        }

        // ─────────────────────────────── infraestrutura ───────────────────────────────

        private static (LowCodeTransformationStore store, string raiz) CriarStore(int ttlHoras = 2)
        {
            var raiz = Path.Combine(Path.GetTempPath(), "lp-tests", "lowcode-store", Guid.NewGuid().ToString("N"));

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ML:LowCodeTransformationsPath"] = raiz
                })
                .Build();

            var store = new LowCodeTransformationStore(
                NullLogger<LowCodeTransformationStore>.Instance,
                config,
                Options.Create(new LowCodeRunnerOptions { TransformationCacheTtlHours = ttlHoras }),
                redis: null);

            return (store, raiz);
        }

        /// <summary>Grava um artefato de saída + a entrada de índice que aponta para ele.</summary>
        private static async Task PersistirExecucaoAsync(
            LowCodeTransformationStore store, string raiz, string xml, bool parcial = false)
        {
            const string dateFolder = "20260805";
            const string outputFile = "base.cand0_M1.lowcode.xml";

            Directory.CreateDirectory(Path.Combine(raiz, dateFolder));
            await File.WriteAllTextAsync(Path.Combine(raiz, dateFolder, outputFile), xml, Encoding.UTF8);

            var entrada = new LowCodeTransformationIndexEntry
            {
                BaseName = "base",
                DateFolder = dateFolder,
                Partial = parcial,
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
            };

            await store.WriteCompletedAsync(ShaValido, LayoutGuidValido, entrada);
        }

        private static async Task EnvelhecerIndiceAsync(string raiz, int horas)
        {
            var caminho = Path.Combine(raiz, "index", $"{ShaValido}.{LayoutGuidValido}.json");
            var opcoes = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

            var entrada = JsonSerializer.Deserialize<LowCodeTransformationIndexEntry>(
                await File.ReadAllTextAsync(caminho), opcoes)!;
            entrada.CreatedAtUtc = DateTime.UtcNow.AddHours(-horas);

            await File.WriteAllTextAsync(caminho, JsonSerializer.Serialize(entrada, opcoes), Encoding.UTF8);
        }
    }
}
