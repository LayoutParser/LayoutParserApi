using System.Security.Cryptography;
using System.Text;
using LayoutParserApi.Services.Database;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Services.Transformation.LowCode
{
    /// <summary>
    /// Executa transformação low-code automaticamente (sem necessidade de chamada manual)
    /// usando o layoutGuid selecionado no front para identificar o MapperGuid no banco.
    /// </summary>
    public class LowCodeAutoTransformationService
    {
        private readonly ILogger<LowCodeAutoTransformationService> _logger;
        private readonly MapperDatabaseService _mapperDb;
        private readonly LowCodeTransformationService _lowCode;
        private readonly LowCodeRunnerOptions _opt;
        private readonly string _storePath;

        public LowCodeAutoTransformationService(
            ILogger<LowCodeAutoTransformationService> logger,
            MapperDatabaseService mapperDb,
            LowCodeTransformationService lowCode,
            IConfiguration configuration,
            IOptions<LowCodeRunnerOptions> options)
        {
            _logger = logger;
            _mapperDb = mapperDb;
            _lowCode = lowCode;
            _opt = options.Value;
            _storePath = configuration["ML:LowCodeTransformationsPath"]
                ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MLData", "LowCodeTransformations");
            Directory.CreateDirectory(_storePath);
        }

        public Task RunInBackgroundAsync(string layoutGuid, string layoutName, string txtContent, string detectedType, string originalFileName)
        {
            // fire-and-forget
            return Task.Run(async () =>
            {
                try
                {
                    await TransformAndPersistAsync(layoutGuid, layoutName, txtContent, detectedType, originalFileName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Falha no auto-transform low-code");
                }
            });
        }

        private async Task TransformAndPersistAsync(string layoutGuid, string layoutName, string txtContent, string detectedType, string originalFileName)
        {
            if (string.IsNullOrWhiteSpace(layoutGuid) || string.IsNullOrWhiteSpace(txtContent))
                return;

            // Selecionar MapperGuid (filtrado por ProjectId e AllowedPackageGuids)
            var mapper = await _mapperDb.GetBestMapperForLayoutGuidAsync(
                layoutGuid,
                _opt.ProjectId,
                _opt.AllowedPackageGuids);

            if (mapper == null || string.IsNullOrWhiteSpace(mapper.MapperGuid))
            {
                _logger.LogWarning("Nenhum mapper encontrado para layoutGuid={LayoutGuid} nos pacotes permitidos", layoutGuid);
                return;
            }

            _logger.LogInformation("AutoTransform low-code: layout={LayoutName} ({LayoutGuid}) mapper={MapperName} ({MapperGuid})",
                layoutName, layoutGuid, mapper.Name, mapper.MapperGuid);

            // Executar low-code
            var lowCodeXml = await _lowCode.TransformAsync(
                txtContent,
                mapperId: mapper.MapperGuid,
                mapperName: null,
                fileName: originalFileName);

            // Persistir para aprendizado contínuo
            var sha = ComputeSha256(txtContent);
            var dateFolder = DateTime.UtcNow.ToString("yyyyMMdd");
            var folder = Path.Combine(_storePath, dateFolder);
            Directory.CreateDirectory(folder);

            var baseName = $"{sha}_{DateTime.UtcNow:HHmmss}";
            var metaPath = Path.Combine(folder, $"{baseName}.meta.json");
            var inPath = Path.Combine(folder, $"{baseName}.input.txt");
            var outPath = Path.Combine(folder, $"{baseName}.lowcode.xml");

            await File.WriteAllTextAsync(inPath, txtContent, Encoding.UTF8);
            await File.WriteAllTextAsync(outPath, lowCodeXml ?? "", Encoding.UTF8);

            var meta = new
            {
                createdAtUtc = DateTime.UtcNow,
                layoutGuid,
                layoutName,
                detectedType,
                originalFileName,
                mapperGuid = mapper.MapperGuid,
                mapperName = mapper.Name,
                packageGuid = mapper.PackageGuid,
                projectId = mapper.ProjectId,
                sha256 = sha,
                inputLength = txtContent.Length,
                outputLength = (lowCodeXml ?? "").Length
            };
            var json = System.Text.Json.JsonSerializer.Serialize(meta, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(metaPath, json, Encoding.UTF8);
        }

        private static string ComputeSha256(string input)
        {
            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(input ?? "");
            var hash = sha.ComputeHash(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}


