using System.Security.Cryptography;
using System.Text;

using LayoutParserApi.Controllers;
using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Services.Fiscal;
using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Tests.Fiscal
{
    /// <summary>
    /// Issue #366 — histórico de análises fiscais: <see cref="FiscalAnalysisService"/> (disco + store) e
    /// <see cref="FiscalAnalysesController"/> (isolamento por dono), com store/workspace em memória e
    /// raiz temporária em disco. O SQL real (<c>SqlFiscalAnalysisStore</c>) NÃO é exercitado aqui.
    /// </summary>
    public sealed class FiscalAnalysisHistoryTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "lp-tests", "fiscal-analyses", Guid.NewGuid().ToString("N"));
        private readonly Guid _workspace = Guid.NewGuid();
        private readonly Guid _userA = Guid.NewGuid();
        private readonly Guid _userB = Guid.NewGuid();
        private readonly FakeStore _store = new();
        private readonly FakeClock _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        private readonly FakeWorkspaces _workspaces = new();

        public FiscalAnalysisHistoryTests()
        {
            _workspaces.Members.Add((_workspace, _userA));
            _workspaces.Members.Add((_workspace, _userB));
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best-effort */ }
        }

        private FiscalAnalysisService CriarServico(IFiscalAnalysisStore? store = null)
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ML:FiscalAnalysesPath"] = _root })
                .Build();
            return new FiscalAnalysisService(
                store ?? _store, _workspaces, Options.Create(new FiscalAnalysisHistoryOptions()),
                NullLogger<FiscalAnalysisService>.Instance, config, _clock);
        }

        private static FiscalAnalysisRegistration Registro(Guid ws, Guid owner, params (string role, string name, string content)[] files)
            => new(ws, owner, FiscalAnalysisSource.Upload, FiscalAnalysisLayoutMode.File, null, "Layout X", "txt",
                files.Select(f => new FiscalAnalysisFileInput(f.role, f.name, Encoding.UTF8.GetBytes(f.content))).ToList());

        private Task<Guid?> Registrar(FiscalAnalysisService svc, Guid owner, string doc = "conteudo")
            => svc.RegisterAsync(Registro(_workspace, owner,
                (FiscalAnalysisFileRole.Document, "doc.txt", doc),
                (FiscalAnalysisFileRole.Layout, "layout.xml", "<Layout/>")), TimeSpan.FromSeconds(5));

        // ───────────────────────────── registro ─────────────────────────────

        [Fact]
        public async Task Registro_com_N_arquivos_grava_disco_e_sql_com_hash()
        {
            var svc = CriarServico();
            var id = await svc.RegisterAsync(Registro(_workspace, _userA,
                (FiscalAnalysisFileRole.Document, "a.txt", "AAA"),
                (FiscalAnalysisFileRole.Document, "b.txt", "BBBB"),
                (FiscalAnalysisFileRole.Layout, "layout.xml", "<Layout/>")), TimeSpan.FromSeconds(5));

            Assert.NotNull(id);
            var detail = await svc.GetAsync(_workspace, _userA, id!.Value, default);
            Assert.NotNull(detail);
            Assert.Equal(3, detail!.Files.Count);
            Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddDays(90), detail.Analysis.ExpiresAtUtc);

            foreach (var f in detail.Files)
            {
                var abs = Path.Combine(_root, f.StoragePath);
                Assert.True(File.Exists(abs));
                Assert.Equal(f.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(abs))).ToLowerInvariant());
                Assert.Equal(f.SizeBytes, new FileInfo(abs).Length);
            }
        }

        [Fact]
        public async Task Nome_com_path_traversal_fica_dentro_da_raiz()
        {
            var svc = CriarServico();
            var id = await svc.RegisterAsync(Registro(_workspace, _userA,
                (FiscalAnalysisFileRole.Document, @"..\..\..\evil.txt", "x")), TimeSpan.FromSeconds(5));

            var detail = await svc.GetAsync(_workspace, _userA, id!.Value, default);
            var abs = Path.GetFullPath(Path.Combine(_root, detail!.Files[0].StoragePath));
            Assert.StartsWith(Path.GetFullPath(_root), abs);
            Assert.DoesNotContain("..", detail.Files[0].OriginalFileName);
        }

        [Fact]
        public async Task Nao_membro_nao_registra()
        {
            var svc = CriarServico();
            var id = await Registrar(svc, Guid.NewGuid());
            Assert.Null(id);
            Assert.Empty(_store.Analyses);
        }

        [Fact]
        public async Task Falha_do_sql_devolve_null_e_apaga_o_que_foi_para_o_disco()
        {
            _store.ThrowOnCreate = true;
            var svc = CriarServico();

            var id = await Registrar(svc, _userA);

            Assert.Null(id); // não lança
            Assert.False(Directory.Exists(Path.Combine(_root, _workspace.ToString())) &&
                         Directory.EnumerateFileSystemEntries(Path.Combine(_root, _workspace.ToString())).Any());
        }

        [Fact]
        public async Task Timeout_do_registro_devolve_null_sem_lancar()
        {
            _store.BlockOnCreate = true;
            var svc = CriarServico();

            var id = await svc.RegisterAsync(Registro(_workspace, _userA, (FiscalAnalysisFileRole.Document, "a.txt", "x")),
                TimeSpan.FromMilliseconds(200));

            Assert.Null(id);
        }

        // ───────────────────────────── isolamento / lista ─────────────────────────────

        [Fact]
        public async Task Lista_e_paginada_e_so_do_dono()
        {
            var svc = CriarServico();
            for (var i = 0; i < 5; i++) { await Registrar(svc, _userA); _clock.Advance(TimeSpan.FromMinutes(1)); }
            for (var i = 0; i < 2; i++) { await Registrar(svc, _userB); _clock.Advance(TimeSpan.FromMinutes(1)); }

            var p1 = await svc.ListAsync(_workspace, _userA, 1, 2, default);
            var p3 = await svc.ListAsync(_workspace, _userA, 3, 2, default);
            var b = await svc.ListAsync(_workspace, _userB, 1, 20, default);

            Assert.Equal(5, p1.Total);
            Assert.Equal(2, p1.Items.Count);
            Assert.Single(p3.Items);
            Assert.True(p1.Items[0].CreatedAtUtc >= p1.Items[1].CreatedAtUtc); // mais recente primeiro
            Assert.All(p1.Items, i => Assert.Equal(2, i.FileCount));
            Assert.Equal(2, b.Total);
            Assert.Empty(p1.Items.Select(i => i.AnalysisId).Intersect(b.Items.Select(i => i.AnalysisId)));
        }

        [Fact]
        public async Task Outro_usuario_recebe_404_em_detalhe_download_e_delete()
        {
            var svc = CriarServico();
            var id = (await Registrar(svc, _userA))!.Value;
            var fileId = (await svc.GetAsync(_workspace, _userA, id, default))!.Files[0].AnalysisFileId;

            var ctrlB = CriarController(svc, _userB);

            Assert.IsType<NotFoundResult>(await ctrlB.Get(_workspace, id, default));
            Assert.IsType<NotFoundResult>(await ctrlB.DownloadFile(_workspace, id, fileId, default));
            Assert.IsType<NotFoundResult>(await ctrlB.Delete(_workspace, id, default));

            // A análise do dono segue intacta.
            Assert.IsType<OkObjectResult>(await CriarController(svc, _userA).Get(_workspace, id, default));
        }

        [Fact]
        public async Task Anonimo_recebe_404()
        {
            var svc = CriarServico();
            var ctrl = CriarController(svc, null);
            Assert.IsType<NotFoundResult>(await ctrl.List(_workspace, 1, 20, default));
        }

        [Fact]
        public async Task Paginacao_invalida_devolve_400()
        {
            var ctrl = CriarController(CriarServico(), _userA);
            Assert.IsType<BadRequestObjectResult>(await ctrl.List(_workspace, 0, 20, default));
            Assert.IsType<BadRequestObjectResult>(await ctrl.List(_workspace, 1, 101, default));
        }

        [Fact]
        public async Task Detalhe_nao_expoe_caminho_de_disco()
        {
            var svc = CriarServico();
            var id = (await Registrar(svc, _userA))!.Value;

            var ok = Assert.IsType<OkObjectResult>(await CriarController(svc, _userA).Get(_workspace, id, default));
            var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);

            Assert.DoesNotContain(_root, json.Replace("\\\\", "\\"), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("StoragePath", json, StringComparison.OrdinalIgnoreCase);
        }

        // ───────────────────────────── download ─────────────────────────────

        [Fact]
        public async Task Download_devolve_bytes_originais()
        {
            var svc = CriarServico();
            var id = (await Registrar(svc, _userA, "meu-documento"))!.Value;
            var detail = (await svc.GetAsync(_workspace, _userA, id, default))!;
            var doc = detail.Files.First(f => f.Role == FiscalAnalysisFileRole.Document);

            var result = await CriarController(svc, _userA).DownloadFile(_workspace, id, doc.AnalysisFileId, default);

            var file = Assert.IsType<FileContentResult>(result);
            Assert.Equal("meu-documento", Encoding.UTF8.GetString(file.FileContents));
            Assert.Equal("application/octet-stream", file.ContentType);
        }

        [Fact]
        public async Task Download_com_arquivo_adulterado_falha_na_verificacao_de_hash()
        {
            var svc = CriarServico();
            var id = (await Registrar(svc, _userA, "original"))!.Value;
            var doc = (await svc.GetAsync(_workspace, _userA, id, default))!.Files.First(f => f.Role == FiscalAnalysisFileRole.Document);
            await File.WriteAllTextAsync(Path.Combine(_root, doc.StoragePath), "ADULTERADO");

            var content = await svc.OpenFileAsync(_workspace, _userA, id, doc.AnalysisFileId, default);
            Assert.Equal(FiscalAnalysisFileStatus.IntegrityFailure, content.Status);
            Assert.Null(content.Content);

            var result = await CriarController(svc, _userA).DownloadFile(_workspace, id, doc.AnalysisFileId, default);
            Assert.Equal(500, Assert.IsType<ObjectResult>(result).StatusCode);
        }

        // ───────────────────────────── delete ─────────────────────────────

        [Fact]
        public async Task Delete_remove_sql_e_disco()
        {
            var svc = CriarServico();
            var id = (await Registrar(svc, _userA))!.Value;
            var dir = Path.Combine(_root, _workspace.ToString(), id.ToString());
            Assert.True(Directory.Exists(dir));

            Assert.IsType<NoContentResult>(await CriarController(svc, _userA).Delete(_workspace, id, default));

            Assert.Empty(_store.Analyses);
            Assert.False(Directory.Exists(dir));
            Assert.IsType<NotFoundResult>(await CriarController(svc, _userA).Delete(_workspace, id, default));
        }

        // ───────────────────────────── purga ─────────────────────────────

        [Fact]
        public async Task Purga_remove_so_o_que_passou_de_90_dias()
        {
            var svc = CriarServico();
            var velha = (await Registrar(svc, _userA))!.Value;   // t0
            _clock.Advance(TimeSpan.FromDays(50));
            var recente = (await Registrar(svc, _userA))!.Value; // t0+50d
            _clock.Advance(TimeSpan.FromDays(41));               // t0+91d: velha expirou (90d), recente não (41d)

            var purgadas = await svc.PurgeExpiredAsync(default);

            Assert.Equal(1, purgadas);
            Assert.DoesNotContain(_store.Analyses, a => a.AnalysisId == velha);
            Assert.Contains(_store.Analyses, a => a.AnalysisId == recente);
            Assert.False(Directory.Exists(Path.Combine(_root, _workspace.ToString(), velha.ToString())));
            Assert.True(Directory.Exists(Path.Combine(_root, _workspace.ToString(), recente.ToString())));
        }

        [Fact]
        public async Task Analise_expirada_some_de_lista_e_detalhe_mesmo_antes_da_purga()
        {
            var svc = CriarServico();
            var id = (await Registrar(svc, _userA))!.Value;
            _clock.Advance(TimeSpan.FromDays(91));

            Assert.Null(await svc.GetAsync(_workspace, _userA, id, default));
            Assert.Equal(0, (await svc.ListAsync(_workspace, _userA, 1, 20, default)).Total);
        }

        [Fact]
        public async Task Purga_varre_diretorio_orfao_antigo_e_preserva_o_recente_e_o_com_linha()
        {
            var svc = CriarServico();
            var comLinha = (await Registrar(svc, _userA))!.Value;

            var orfaoAntigo = Path.Combine(_root, _workspace.ToString(), Guid.NewGuid().ToString());
            var orfaoRecente = Path.Combine(_root, _workspace.ToString(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(orfaoAntigo);
            Directory.CreateDirectory(orfaoRecente);
            Directory.SetLastWriteTimeUtc(orfaoAntigo, DateTime.UtcNow.AddDays(-2));
            Directory.SetLastWriteTimeUtc(Path.Combine(_root, _workspace.ToString(), comLinha.ToString()), DateTime.UtcNow.AddDays(-2));

            await svc.PurgeExpiredAsync(default);

            Assert.False(Directory.Exists(orfaoAntigo));
            Assert.True(Directory.Exists(orfaoRecente));   // carência: pode ser registro em andamento
            Assert.True(Directory.Exists(Path.Combine(_root, _workspace.ToString(), comLinha.ToString())));
        }

        // ───────────────────────────── ParseController ─────────────────────────────

        [Fact]
        public async Task Options_com_retencao_invalida_cai_em_90_dias()
        {
            Assert.Equal(90, new FiscalAnalysisHistoryOptions { RetentionDays = 0 }.EffectiveRetentionDays);
            Assert.Equal(90, new FiscalAnalysisHistoryOptions { RetentionDays = -5 }.EffectiveRetentionDays);
            Assert.Equal(30, new FiscalAnalysisHistoryOptions { RetentionDays = 30 }.EffectiveRetentionDays);
            await Task.CompletedTask;
        }

        // ───────────────────────────── infraestrutura ─────────────────────────────

        private FiscalAnalysesController CriarController(IFiscalAnalysisService svc, Guid? userId)
            => new(svc, new FakeUser { UserId = userId }, NullLogger<FiscalAnalysesController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };

        private sealed class FakeUser : ICurrentUser
        {
            public string? Name => UserId.HasValue ? "u" : null;
            public IReadOnlyList<string> Roles => [];
            public bool IsAuthenticated => UserId.HasValue;
            public Guid? UserId { get; set; }
            public bool IsInRole(string role) => false;
        }

        private sealed class FakeClock(DateTimeOffset start) : TimeProvider
        {
            private DateTimeOffset _now = start;
            public void Advance(TimeSpan by) => _now += by;
            public override DateTimeOffset GetUtcNow() => _now;
        }

        private sealed class FakeWorkspaces : IIdentityWorkspaceStore
        {
            public HashSet<(Guid, Guid)> Members { get; } = new();
            public Task<Guid> ResolveOrCreateUserAsync(string provider, string tenantOrIssuer, string subject, CancellationToken ct) => throw new NotSupportedException();
            public Task<WorkspaceSummary> EnsurePersonalWorkspaceAsync(Guid userId, CancellationToken ct) => throw new NotSupportedException();
            public Task<IReadOnlyList<WorkspaceSummary>> GetMembershipsAsync(Guid userId, CancellationToken ct) => throw new NotSupportedException();
            public Task<WorkspaceSummary?> GetWorkspaceIfMemberAsync(Guid workspaceId, Guid userId, CancellationToken ct)
                => Task.FromResult(Members.Contains((workspaceId, userId))
                    ? new WorkspaceSummary(workspaceId, "ws", "personal", "owner", DateTimeOffset.UtcNow)
                    : null);
        }

        private sealed class FakeStore : IFiscalAnalysisStore
        {
            public List<FiscalAnalysisRecord> Analyses { get; } = new();
            public List<FiscalAnalysisFileRecord> Files { get; } = new();
            public bool ThrowOnCreate { get; set; }
            public bool BlockOnCreate { get; set; }

            public async Task CreateAsync(FiscalAnalysisRecord analysis, IReadOnlyList<FiscalAnalysisFileRecord> files, CancellationToken ct)
            {
                if (BlockOnCreate) await Task.Delay(Timeout.Infinite, ct);
                if (ThrowOnCreate) throw new InvalidOperationException("SQL fora do ar");
                Analyses.Add(analysis);
                Files.AddRange(files);
            }

            public Task<FiscalAnalysisPage> ListAsync(Guid workspaceId, Guid ownerUserId, int page, int pageSize, DateTime nowUtc, CancellationToken ct)
            {
                var mine = Analyses.Where(a => a.WorkspaceId == workspaceId && a.OwnerUserId == ownerUserId && a.ExpiresAtUtc > nowUtc)
                    .OrderByDescending(a => a.CreatedAtUtc).ToList();
                var items = mine.Skip((page - 1) * pageSize).Take(pageSize).Select(a =>
                {
                    var fs = Files.Where(f => f.AnalysisId == a.AnalysisId).ToList();
                    return new FiscalAnalysisSummary(a.AnalysisId, a.CreatedAtUtc, a.ExpiresAtUtc, a.Source, a.LayoutName, a.LayoutGuid, a.DetectedType, fs.Count, fs.Sum(f => f.SizeBytes));
                }).ToList();
                return Task.FromResult(new FiscalAnalysisPage(page, pageSize, mine.Count, items));
            }

            public Task<FiscalAnalysisDetail?> GetAsync(Guid workspaceId, Guid ownerUserId, Guid analysisId, DateTime nowUtc, CancellationToken ct)
            {
                var a = Analyses.FirstOrDefault(x => x.AnalysisId == analysisId && x.WorkspaceId == workspaceId && x.OwnerUserId == ownerUserId && x.ExpiresAtUtc > nowUtc);
                return Task.FromResult(a == null ? null : new FiscalAnalysisDetail(a, Files.Where(f => f.AnalysisId == analysisId).ToList()));
            }

            public Task<bool> DeleteAsync(Guid workspaceId, Guid ownerUserId, Guid analysisId, CancellationToken ct)
            {
                var removed = Analyses.RemoveAll(x => x.AnalysisId == analysisId && x.WorkspaceId == workspaceId && x.OwnerUserId == ownerUserId) > 0;
                if (removed) Files.RemoveAll(f => f.AnalysisId == analysisId);
                return Task.FromResult(removed);
            }

            public Task<IReadOnlyList<FiscalAnalysisExpiredRef>> ListExpiredAsync(DateTime nowUtc, int batchSize, CancellationToken ct)
                => Task.FromResult<IReadOnlyList<FiscalAnalysisExpiredRef>>(
                    Analyses.Where(a => a.ExpiresAtUtc <= nowUtc).Take(batchSize).Select(a => new FiscalAnalysisExpiredRef(a.AnalysisId, a.WorkspaceId)).ToList());

            public Task DeleteByIdAsync(Guid analysisId, CancellationToken ct)
            {
                Analyses.RemoveAll(a => a.AnalysisId == analysisId);
                Files.RemoveAll(f => f.AnalysisId == analysisId);
                return Task.CompletedTask;
            }

            public Task<IReadOnlySet<Guid>> GetExistingIdsAsync(IReadOnlyCollection<Guid> analysisIds, CancellationToken ct)
                => Task.FromResult<IReadOnlySet<Guid>>(Analyses.Select(a => a.AnalysisId).Where(analysisIds.Contains).ToHashSet());
        }
    }
}
