using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using LayoutParserApi.Models.Fiscal;

namespace LayoutParserApi.Services.Fiscal
{
    /// <summary>
    /// Lê o corpus de exemplos reais de transformação TCL/XSL da Neogrid (referência/oráculo, não
    /// releases compilados por este pipeline) de um diretório configurável (<c>ReferenceExamples:BasePath</c>).
    /// Estrutura esperada, espelhada entre as duas subpastas:
    /// <c>{BasePath}/tcl/{DocType}/{Versao}/{Arquivo}.tcl</c> e <c>{BasePath}/xsl/{DocType}/{Versao}/{Arquivo}.xsl</c>.
    /// Degrada graciosamente (lista vazia) se o path não existir — ver [[dotnet-standards]] resiliência.
    /// </summary>
    public sealed class ReferenceExampleCatalogService : IReferenceExampleCatalogService
    {
        private static readonly string[] TclExtensions = [".tcl"];
        private static readonly string[] XslExtensions = [".xsl", ".xslt"];

        // Sufixo de sentido de transformação reconhecido nos nomes de arquivo do corpus
        // (ex.: "..._NeoGridToSefaz.tcl", "..._SefazToNeoGridPipeline.xsl").
        private static readonly Regex DirectionSuffixRegex = new(
            @"_(?<direction>[A-Za-z]+To[A-Za-z]+)$",
            RegexOptions.Compiled);

        private readonly string _basePath;
        private readonly ILogger<ReferenceExampleCatalogService> _logger;

        public ReferenceExampleCatalogService(IConfiguration configuration, ILogger<ReferenceExampleCatalogService> logger)
        {
            _logger = logger;
            // Sem fallback para path de produção "chutado" — corpus é opcional; ausência de config = catálogo vazio.
            _basePath = configuration["ReferenceExamples:BasePath"] ?? string.Empty;
        }

        public Task<IReadOnlyList<ReferenceExample>> ListAsync(string? docType, CancellationToken cancellationToken)
        {
            var catalog = BuildCatalog();

            IEnumerable<CatalogEntry> filtered = catalog.Values;
            if (!string.IsNullOrWhiteSpace(docType))
                filtered = filtered.Where(e => string.Equals(e.DocType, docType, StringComparison.OrdinalIgnoreCase));

            var result = filtered
                .Select(e => e.ToDto())
                .OrderBy(e => e.DocType, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Version, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Scenario, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Task.FromResult<IReadOnlyList<ReferenceExample>>(result);
        }

        public async Task<ReferenceExampleContent?> GetContentAsync(string id, CancellationToken cancellationToken)
        {
            var catalog = BuildCatalog();
            if (!catalog.TryGetValue(id, out var entry))
                return null;

            string? tclContent = null;
            string? xslContent = null;

            try
            {
                if (entry.TclPath != null)
                    tclContent = await File.ReadAllTextAsync(entry.TclPath, cancellationToken);
                if (entry.XslPath != null)
                    xslContent = await File.ReadAllTextAsync(entry.XslPath, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Corpus é referência somente-leitura; falha de I/O não pode derrubar o request.
                _logger.LogWarning(ex, "Falha ao ler conteúdo do exemplo de referência {Id}", id);
                return null;
            }

            return new ReferenceExampleContent
            {
                Id = id,
                TclContent = tclContent,
                XslContent = xslContent,
            };
        }

        /// <summary>Reconstrói o catálogo a partir do disco a cada chamada — corpus é pequeno e somente-leitura, não justifica cache/invalidação.</summary>
        private Dictionary<string, CatalogEntry> BuildCatalog()
        {
            var catalog = new Dictionary<string, CatalogEntry>(StringComparer.Ordinal);

            if (string.IsNullOrWhiteSpace(_basePath))
            {
                _logger.LogDebug("ReferenceExamples:BasePath não configurado — catálogo de exemplos de referência vazio.");
                return catalog;
            }

            var tclRoot = Path.Combine(_basePath, "tcl");
            var xslRoot = Path.Combine(_basePath, "xsl");

            if (!Directory.Exists(tclRoot) && !Directory.Exists(xslRoot))
            {
                _logger.LogWarning("Corpus de exemplos de referência não encontrado em {BasePath} — catálogo vazio.", _basePath);
                return catalog;
            }

            IndexTree(tclRoot, TclExtensions, isTcl: true, catalog);
            IndexTree(xslRoot, XslExtensions, isTcl: false, catalog);

            return catalog;
        }

        private static void IndexTree(string root, string[] extensions, bool isTcl, Dictionary<string, CatalogEntry> catalog)
        {
            if (!Directory.Exists(root))
                return;

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file);
                if (!extensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                    continue;

                var relative = Path.GetRelativePath(root, file);
                var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (segments.Length < 2)
                    continue; // arquivo direto na raiz, sem DocType — fora do padrão esperado

                var fileName = segments[^1];
                var baseName = Path.GetFileNameWithoutExtension(fileName);
                var docType = segments[0];
                string? version = segments.Length >= 3 ? segments[1] : null;

                // Chave de pareamento entre tcl/xsl: mesmo DocType/Versão/nome-base (case-insensitive).
                var key = BuildEntryId(docType, version, baseName);

                if (!catalog.TryGetValue(key, out var entry))
                {
                    entry = new CatalogEntry
                    {
                        Id = key,
                        DocType = docType,
                        Version = version,
                        BaseName = baseName,
                    };
                    catalog[key] = entry;
                }

                if (isTcl)
                {
                    entry.TclPath = file;
                    entry.TclFileName = fileName;
                }
                else
                {
                    entry.XslPath = file;
                    entry.XslFileName = fileName;
                }
            }
        }

        private static string BuildEntryId(string docType, string? version, string baseName)
        {
            var raw = $"{docType}/{version}/{baseName}".ToLowerInvariant();
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(hash)[..16].ToLowerInvariant();
        }

        private sealed class CatalogEntry
        {
            public required string Id { get; set; }
            public required string DocType { get; set; }
            public string? Version { get; set; }
            public required string BaseName { get; set; }
            public string? TclPath { get; set; }
            public string? TclFileName { get; set; }
            public string? XslPath { get; set; }
            public string? XslFileName { get; set; }

            public ReferenceExample ToDto()
            {
                var match = DirectionSuffixRegex.Match(BaseName);
                string scenario;
                string? direction = null;
                if (match.Success)
                {
                    direction = match.Groups["direction"].Value;
                    scenario = BaseName[..match.Index];
                }
                else
                {
                    scenario = BaseName;
                }

                return new ReferenceExample
                {
                    Id = Id,
                    DocType = DocType,
                    Version = Version,
                    Scenario = scenario,
                    Direction = direction,
                    TclFileName = TclFileName,
                    XslFileName = XslFileName,
                };
            }
        }
    }
}
