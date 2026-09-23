using System.IO.Compression;
using System.Text;

using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Services.Fiscal;
using LayoutParserApi.Services.Interfaces;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Services.Fiscal
{
    /// <summary>
    /// Issue #424 — sinais determinísticos de qualidade da planilha de especificação. Planilhas 100%
    /// sintéticas, geradas como OOXML mínimo (o extrator lê o pacote bruto via ZipArchive).
    /// </summary>
    public class FiscalSpecQualityTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "fiscal-quality-" + Guid.NewGuid());

        public FiscalSpecQualityTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        // ---- fixture sintética ----

        /// <summary>Cada aba = linhas de células de texto (inlineStr).</summary>
        private static byte[] BuildXlsx(params (string Name, string[][] Rows)[] sheets)
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                void Add(string path, string content)
                {
                    var e = zip.CreateEntry(path);
                    using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                    w.Write(content);
                }

                var wb = new StringBuilder("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
                var rels = new StringBuilder("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
                for (var i = 0; i < sheets.Length; i++)
                {
                    wb.Append($"<sheet name=\"{sheets[i].Name}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
                    rels.Append($"<Relationship Id=\"rId{i + 1}\" Target=\"worksheets/sheet{i + 1}.xml\"/>");

                    var sd = new StringBuilder("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
                    for (var r = 0; r < sheets[i].Rows.Length; r++)
                    {
                        sd.Append($"<row r=\"{r + 1}\">");
                        for (var c = 0; c < sheets[i].Rows[r].Length; c++)
                            sd.Append($"<c r=\"{(char)('A' + c)}{r + 1}\" t=\"inlineStr\"><is><t>{sheets[i].Rows[r][c]}</t></is></c>");
                        sd.Append("</row>");
                    }
                    sd.Append("</sheetData></worksheet>");
                    Add($"xl/worksheets/sheet{i + 1}.xml", sd.ToString());
                }
                wb.Append("</sheets></workbook>");
                rels.Append("</Relationships>");
                Add("xl/workbook.xml", wb.ToString());
                Add("xl/_rels/workbook.xml.rels", rels.ToString());
            }
            return ms.ToArray();
        }

        private static readonly (string Name, string[][] Rows) RuleSheet = ("Regras", new[]
        {
            new[] { "Regra", "orig", "CST", "Resultado" },
            new[] { "1", "0", "40", "gera vICMS" },
        });

        // ---- montagem do service ----

        private sealed class Store : IFiscalPackageStore
        {
            public string? Path { get; set; }
            public Task<bool> EnsureProjectExistsAsync(Guid w, Guid p, CancellationToken c) => Task.FromResult(true);
            public Task<PackageDetail> CreatePackageAsync(Guid w, Guid p, Guid u, string n, string k, IReadOnlyList<PackageArtifact> a, CancellationToken c) => throw new NotSupportedException();
            public Task<PackageDetail?> GetPackageIfMemberAsync(Guid p, Guid u, CancellationToken c) => Task.FromResult<PackageDetail?>(null);
            public Task<PackageDetail?> FindPackageByIdempotencyKeyAsync(Guid w, Guid p, string k, CancellationToken c) => Task.FromResult<PackageDetail?>(null);
            public Task<ArtifactSummary?> FindArtifactByHashAsync(Guid p, string s, CancellationToken c) => Task.FromResult<ArtifactSummary?>(null);
            public Task UpdateInspectionStatusAsync(Guid a, string s, CancellationToken c) => Task.CompletedTask;
            public Task<IReadOnlyList<ProjectSummary>> ListProjectsForMemberAsync(Guid w, Guid u, CancellationToken c) => Task.FromResult<IReadOnlyList<ProjectSummary>>(Array.Empty<ProjectSummary>());
            public Task<PackageDetail> CreateRevisionAsync(Guid p, Guid u, IReadOnlyList<PackageArtifact> a, CancellationToken c) => throw new NotSupportedException();
            public Task<string?> GetArtifactStoragePathAsync(Guid a, CancellationToken c) => Task.FromResult(Path);
        }

        private sealed class NoScan : IAntivirusScanner
        {
            public Task<bool?> ScanAsync(string filePath, CancellationToken cancellationToken) => Task.FromResult<bool?>(null);
        }

        private async Task<SpecQualityResult> Analyze(byte[] bytes, params string[] required)
        {
            const string file = "spec.xlsx";
            File.WriteAllBytes(System.IO.Path.Combine(_dir, file), bytes);

            var cfg = new Dictionary<string, string?> { ["ML:FiscalMappingPackagesPath"] = _dir };
            for (var i = 0; i < required.Length; i++)
                cfg[$"{FiscalSpecQualityAnalyzer.RequiredColumnsConfigKey}:{i}"] = required[i];
            var config = new ConfigurationBuilder().AddInMemoryCollection(cfg).Build();

            var service = new FiscalPackageService(new Store { Path = file }, new NoScan(),
                new FiscalMappingRuleExtractor(NullLogger<FiscalMappingRuleExtractor>.Instance),
                NullLogger<FiscalPackageService>.Instance, config);

            var artifactId = Guid.NewGuid();
            var package = new PackageDetail(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "P", DateTimeOffset.UtcNow,
                new RevisionSummary(Guid.NewGuid(), 1, DateTimeOffset.UtcNow,
                    new[] { new ArtifactSummary(artifactId, ArtifactKind.Spec, "h", 1, file, InspectionStatus.Pending, DateTimeOffset.UtcNow) }));

            var result = await service.GetSpecQualityAsync(package, CancellationToken.None);
            return result[artifactId];
        }

        // ---- casos ----

        [Fact]
        public async Task Caso_limpo_tem_checksRun_preenchido_e_sinais_vazios()
        {
            var r = await Analyze(BuildXlsx(RuleSheet), "Regra", "CST");

            Assert.Equal(QualityStatus.Complete, r.Status);
            Assert.Empty(r.Signals!.MissingRequiredColumns);
            Assert.Empty(r.Signals.SkippedSheets);
            Assert.Empty(r.Signals.EmptySheets);
            Assert.Equal(new[] { "missingRequiredColumns", "skippedSheets", "emptySheets" }, r.Signals.ChecksRun);
        }

        [Fact]
        public async Task Coluna_obrigatoria_ausente_e_reportada_por_aba()
        {
            var r = await Analyze(BuildXlsx(RuleSheet), "Regra", "vICMS");

            Assert.Equal(new[] { "Regras!vICMS" }, r.Signals!.MissingRequiredColumns);
        }

        [Fact]
        public async Task Sem_lista_configurada_o_check_de_colunas_nao_roda_e_nao_consta_em_checksRun()
        {
            var r = await Analyze(BuildXlsx(RuleSheet));

            Assert.DoesNotContain("missingRequiredColumns", r.Signals!.ChecksRun);
            Assert.Empty(r.Signals.MissingRequiredColumns);
        }

        [Fact]
        public async Task Conflitos_e_referencias_ausentes_nunca_constam_em_checksRun()
        {
            var r = await Analyze(BuildXlsx(RuleSheet), "Regra");

            Assert.DoesNotContain("conflicts", r.Signals!.ChecksRun);
            Assert.DoesNotContain("absentReferences", r.Signals.ChecksRun);
        }

        [Fact]
        public async Task Aba_ignorada_vira_sinal()
        {
            var layoutSheet = ("Layout", new[] { new[] { "Item", "Campo", "Tamanho" }, new[] { "1", "x", "3" } });
            var r = await Analyze(BuildXlsx(RuleSheet, layoutSheet));

            Assert.Equal(new[] { "Layout" }, r.Signals!.SkippedSheets);
        }

        [Fact]
        public async Task Aba_de_regra_sem_regras_vira_sinal()
        {
            var vazia = ("SemRegras", new[] { new[] { "Regra", "orig", "CST", "Resultado" } });
            var r = await Analyze(BuildXlsx(RuleSheet, vazia));

            Assert.Equal(new[] { "SemRegras" }, r.Signals!.EmptySheets);
        }

        [Fact]
        public async Task Planilha_ilegivel_devolve_failed_sem_excecao_e_sem_sinais()
        {
            var r = await Analyze(Encoding.UTF8.GetBytes("isto nao e um xlsx"), "Regra");

            Assert.Equal(QualityStatus.Failed, r.Status);
            Assert.Null(r.Signals);
            Assert.False(string.IsNullOrWhiteSpace(r.Error));
        }
    }
}
