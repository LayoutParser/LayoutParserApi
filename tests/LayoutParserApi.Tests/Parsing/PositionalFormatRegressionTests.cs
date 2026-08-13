using System.Security.Cryptography;
using System.Text;

using LayoutParserApi.Models.Logging;
using LayoutParserApi.Services.Implementations;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Parsing.Implementations;
using LayoutParserApi.Services.Validation;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Newtonsoft.Json;

namespace LayoutParserApi.Tests.Parsing
{
    /// <summary>
    /// Testes de não-regressão do discriminador de formato posicional (<c>WithBreakLines</c>).
    ///
    /// Depende de amostras REAIS de cliente que NÃO são versionadas (ficam em <c>.claude/tmp/</c>,
    /// que está no .gitignore). Quando as amostras não estão na máquina, os testes são pulados —
    /// dado fiscal de cliente não entra no repositório.
    /// </summary>
    public class PositionalFormatRegressionTests
    {
        // ── Amostra de CONTROLE (MQSeries / ContinuousStream): não pode mudar nada ──────────────
        private const string MqLayoutRelPath = ".claude/tmp/26072026/LAY_TXT_MQSERIES_ENVNFE_4.00_NFe.xml";
        private const string MqTxtRelPath = ".claude/tmp/26072026/QMWNFe1_QMWNFE1.SAPiens_MRB.INBOX_07-11-2025.mq_series.txt";

        // ── Amostra do DEFEITO (IDOC / RecordPerLine): corrupção silenciosa de 2026-08-03 ───────
        private const string IdocLayoutRelPath = ".claude/tmp/03082026/LAY_MARELLI_TXT_SAP_ENVNFE_4.00_NFe - LAY_aa423936-33b2-4aaf-b7d7-3868be141107";
        private const string IdocTxtRelPath = ".claude/tmp/03082026/NF_IDOC_EM_TXT_35260802990605001174550090000507581605247311.TXT";

        /// <summary>
        /// Snapshot do MQSeries de controle. O assert é sobre o HASH do dump completo dos campos:
        /// qualquer alteração de valor, posição, status ou ordem quebra o teste.
        ///
        /// O hash abaixo foi capturado ANTES da introdução do PositionalFormat (baseline do
        /// comportamento legado) rodando este mesmo teste no commit anterior.
        /// </summary>
        [Fact]
        public async Task MqSeries_de_controle_mantem_saida_identica_ao_baseline()
        {
            var amostra = ResolveAmostra(MqLayoutRelPath, MqTxtRelPath);
            if (amostra is null) return; // amostra real ausente nesta máquina

            var (layoutPath, txtPath) = amostra.Value;

            var resultado = await ParseAsync(layoutPath, txtPath);

            Assert.True(resultado.Success, resultado.ErrorMessage);
            // ✅ Issue #37: 702 (baseline legado) + 2 = 704. A agregação de LINHA081
            // (IsPositionalGroupRepetition=true, campo infCpl) anexa 2 ParsedFields adicionais
            // (Occurrence=0: InformacoesParaEDI e Filler agregados) sem remover nenhum dos 702
            // fragmentos originais — ver AggregatePositionalGroupRepetitions.
            Assert.Equal(704, resultado.ParsedFields.Count);

            string dump = DumpCampos(resultado.ParsedFields);
            string hash = Sha256(dump);

            // Dump gravado ao lado do assembly de teste para inspeção/diff quando o hash quebra.
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "mq-controle-dump.json"), dump);

            string? baselineEsperado = Environment.GetEnvironmentVariable("LP_MQ_BASELINE_SHA256");
            if (!string.IsNullOrWhiteSpace(baselineEsperado))
            {
                // Modo captura/comparação manual (usado no desenvolvimento da correção).
                Assert.Equal(baselineEsperado, hash);
                return;
            }

            Assert.Equal(MqBaselineSha256, hash);
        }

        /// <summary>
        /// Baseline do MQSeries de controle — SHA-256 do dump dos 704 campos parseados
        /// (702 fragmentos físicos legados + 2 valores agregados de LINHA081, issue #37).
        /// Recapturado por execução deste commit contra exatamente a mesma amostra usada por
        /// este teste. O hash anterior (pré-#37, 702 campos) era
        /// <c>eea774e6409e10a9806015b49816b98a1fbb487550aa309469d9dc49dd1e2375</c>.
        /// </summary>
        private const string MqBaselineSha256 = "99e4688590b1ec6df01146bf3c43a3c429b1ecdaa03e6b75a2069ed45e690024";

        /// <summary>
        /// Issue #37: LINHA081 (<c>IsPositionalGroupRepetition=true</c>) forma o campo <c>infCpl</c>
        /// da NF-e concatenando N ocorrências físicas da mesma linha no MQSeries. Este teste trava o
        /// valor agregado byte a byte contra o XML final REAL esperado para esta amostra
        /// (<c>.claude/tmp/exemplos/xml output/...-env.xml</c>, tag <c>&lt;infCpl&gt;</c>) — não uma
        /// hipótese sintética. Confirma: concatenação SEM separador artificial, COM trim de cada
        /// fragmento (o padding de alinhamento de texto dentro do campo de largura fixa não é parte
        /// do valor lógico).
        /// </summary>
        [Fact]
        public async Task LINHA081_agrega_infCpl_igual_ao_xml_esperado_do_gabarito_real()
        {
            var amostra = ResolveAmostra(MqLayoutRelPath, MqTxtRelPath);
            if (amostra is null) return; // amostra real ausente nesta máquina

            var (layoutPath, txtPath) = amostra.Value;

            var resultado = await ParseAsync(layoutPath, txtPath);

            Assert.True(resultado.Success, resultado.ErrorMessage);

            const string infCplEsperado =
                "Solicitante: Ana B Costa// Pamela GuedesPIS ST - Valor 0,00COFINS ST - Valor 0,00";

            var agregado = resultado.ParsedFields.SingleOrDefault(f =>
                f.LineName == "LINHA081" && f.FieldName == "InformacoesParaEDI" && f.Occurrence == 0);

            Assert.NotNull(agregado);
            Assert.Equal(infCplEsperado, agregado!.Value);

            // Aditivo: os 4 fragmentos físicos (Occurrence 1..4) continuam intactos — é deles que
            // ValidateLineOccurrences deriva o MinimalOccurrence/MaximumOccurrence de LINHA081.
            var fragmentos = resultado.ParsedFields
                .Where(f => f.LineName == "LINHA081" && f.FieldName == "InformacoesParaEDI" && f.Occurrence > 0)
                .OrderBy(f => f.Occurrence)
                .ToList();
            Assert.Equal(4, fragmentos.Count);
            Assert.Equal(new[] { 1, 2, 3, 4 }, fragmentos.Select(f => f.Occurrence));
        }

        /// <summary>
        /// O defeito real: no IDOC da Marelli o offset do sequencial MQ (6 chars) era aplicado a um
        /// formato que não tem esse campo, deslocando 100% dos campos. Evidência do teste de
        /// 2026-08-03: CUF saía '47' (posição 69) em vez de '35' (posição 63, início dos dados
        /// após o header EDI_DD40 de 63 chars).
        /// </summary>
        [Fact]
        public async Task Idoc_extrai_CUF_e_MOD_corretos_do_documento_real()
        {
            var amostra = ResolveAmostra(IdocLayoutRelPath, IdocTxtRelPath);
            if (amostra is null) return; // amostra real ausente nesta máquina

            var (layoutPath, txtPath) = amostra.Value;

            var resultado = await ParseAsync(layoutPath, txtPath);

            Assert.True(resultado.Success, resultado.ErrorMessage);

            // Dump gravado ao lado do assembly para inspeção manual dos 55 segmentos.
            File.WriteAllText(
                Path.Combine(AppContext.BaseDirectory, "idoc-marelli-dump.json"),
                DumpCampos(resultado.ParsedFields));

            string? cuf = resultado.ParsedFields
                .FirstOrDefault(f => f.FieldName.Equals("CUF", StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
            string? mod = resultado.ParsedFields
                .FirstOrDefault(f => f.FieldName.Equals("MOD", StringComparison.OrdinalIgnoreCase))?.Value?.Trim();

            Assert.Equal("35", cuf);
            Assert.Equal("55", mod);
        }

        /// <summary>
        /// Mesmo layout IDOC, 8 documentos diferentes coletados do servidor (pasta Examples).
        /// Garante que a correção vale para o formato, não para um documento específico.
        /// </summary>
        [Fact]
        public async Task Idoc_extrai_CUF_e_MOD_corretos_em_todas_as_amostras_do_Examples()
        {
            string raiz = LocalizarRaizDoRepo();
            string layoutPath = Path.Combine(raiz, IdocLayoutRelPath.Replace('/', Path.DirectorySeparatorChar));
            string examplesDir = Path.Combine(
                raiz, ".claude", "tmp", "servidor", "layoutparser", "Examples", "LAY_MARELLI_TXT_SAP_ENVNFE_4.00_NFe");

            if (!File.Exists(layoutPath) || !Directory.Exists(examplesDir))
                return; // amostras reais ausentes nesta máquina

            var amostras = Directory.GetFiles(examplesDir, "*.txt").OrderBy(f => f).ToList();
            Assert.NotEmpty(amostras);

            foreach (var amostra in amostras)
            {
                var resultado = await ParseAsync(layoutPath, amostra);

                Assert.True(resultado.Success, $"{Path.GetFileName(amostra)}: {resultado.ErrorMessage}");

                string? cuf = resultado.ParsedFields
                    .FirstOrDefault(f => f.FieldName.Equals("CUF", StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
                string? mod = resultado.ParsedFields
                    .FirstOrDefault(f => f.FieldName.Equals("MOD", StringComparison.OrdinalIgnoreCase))?.Value?.Trim();

                Assert.Equal("35", cuf);
                Assert.Equal("55", mod);
            }
        }

        // ─────────────────────────────── infraestrutura do teste ───────────────────────────────

        private static async Task<LayoutParserApi.Models.Parsing.ParsingResult> ParseAsync(string layoutPath, string txtPath)
        {
            var techLogger = new NoOpTechLogger();
            var auditLogger = new NoOpAuditLogger();

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Mantém o aprendizado ML (fire-and-forget do ParseAsync) fora do repo.
                    ["ML:LearningDataPath"] = Path.Combine(Path.GetTempPath(), "lp-tests", "DocumentPatterns"),
                    ["ML:TrainingSamplesPath"] = Path.Combine(Path.GetTempPath(), "lp-tests", "TrainingSamples")
                })
                .Build();

            var service = new LayoutParserService(
                techLogger,
                auditLogger,
                new LineSplitter(techLogger),
                new LayoutValidator(techLogger),
                new LayoutNormalizer(),
                new DocumentValidationService(techLogger, NullLogger<DocumentValidationService>.Instance),
                new DocumentMLValidationService(techLogger, NullLogger<DocumentMLValidationService>.Instance, config),
                NullLogger<LayoutParserService>.Instance);

            using var layoutStream = AbrirLayout(layoutPath);
            using var txtStream = File.OpenRead(txtPath);

            return await service.ParseAsync(layoutStream, txtStream);
        }

        /// <summary>
        /// Algumas amostras de layout foram extraídas de um payload JSON e chegaram ao disco com as
        /// aspas escapadas (<c>\"</c>). Isso é artefato da coleta da amostra, não do produto —
        /// o desescape acontece só aqui, no harness.
        /// </summary>
        private static Stream AbrirLayout(string layoutPath)
        {
            string conteudo = File.ReadAllText(layoutPath, Encoding.UTF8);
            if (conteudo.Contains("=\\\""))
                conteudo = conteudo.Replace("\\\"", "\"");

            return new MemoryStream(Encoding.UTF8.GetBytes(conteudo));
        }

        private static string DumpCampos(List<LayoutParserApi.Models.Entities.ParsedField>? campos)
        {
            return JsonConvert.SerializeObject(
                (campos ?? new List<LayoutParserApi.Models.Entities.ParsedField>())
                    .Select(f => new
                    {
                        f.LineName,
                        f.FieldName,
                        f.Sequence,
                        f.Start,
                        f.Length,
                        f.Value,
                        f.Status,
                        f.IsRequired,
                        f.Occurrence,
                        f.LineSequence
                    }),
                Formatting.Indented);
        }

        private static string Sha256(string conteudo)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(conteudo));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>
        /// Resolve o par (layout, documento) da amostra real. Devolve <c>null</c> quando a amostra não
        /// existe nesta máquina — o xUnit 2.x não tem skip programático, então o teste retorna cedo.
        /// Prefira isso a versionar dado fiscal de cliente só para o teste rodar em qualquer lugar.
        /// </summary>
        private static (string LayoutPath, string TxtPath)? ResolveAmostra(string layoutRel, string txtRel)
        {
            string raiz = LocalizarRaizDoRepo();
            string layoutPath = Path.Combine(raiz, layoutRel.Replace('/', Path.DirectorySeparatorChar));
            string txtPath = Path.Combine(raiz, txtRel.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(layoutPath) || !File.Exists(txtPath))
                return null;

            return (layoutPath, txtPath);
        }

        private static string LocalizarRaizDoRepo()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LayoutParserApi.csproj")))
                dir = dir.Parent;

            return dir?.FullName ?? AppContext.BaseDirectory;
        }

        private sealed class NoOpTechLogger : ITechLogger
        {
            public void LogTechnical(LogEntry entry) { }
        }

        private sealed class NoOpAuditLogger : IAuditLogger
        {
            public void LogAudit(AuditLogEntry entry) { }
        }
    }
}
