using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace LayoutParserApi.Services.Learning
{
    /// <summary>
    /// Serviço para gerenciar armazenamento de arquivos e diretórios
    /// </summary>
    public class FileStorageService
    {
        private readonly ILogger<FileStorageService> _logger;
        private readonly string _basePath;

        public FileStorageService(IConfiguration configuration, ILogger<FileStorageService> logger)
        {
            _logger = logger;
            _basePath = configuration["Learning:BasePath"] ?? @"C:\inetpub\wwwroot\layoutparser\Exemplo";
            
            // Garantir que o diretório base existe
            if (!Directory.Exists(_basePath))
            {
                Directory.CreateDirectory(_basePath);
                _logger.LogInformation("Diretório base criado: {Path}", _basePath);
            }
        }

        /// <summary>
        /// Salva arquivo enviado e cria estrutura de diretórios
        /// </summary>
        public async Task<FileStorageResult> SaveUploadedFileAsync(IFormFile file)
        {
            try
            {
                if (file == null || file.Length == 0)
                {
                    return new FileStorageResult
                    {
                        Success = false,
                        ErrorMessage = "Arquivo vazio ou inválido"
                    };
                }

                // Criar diretório para o arquivo
                var fileName = Path.GetFileNameWithoutExtension(file.FileName);
                var fileExtension = Path.GetExtension(file.FileName);
                var fileDirectory = Path.Combine(_basePath, fileName);
                
                if (!Directory.Exists(fileDirectory))
                {
                    Directory.CreateDirectory(fileDirectory);
                    _logger.LogInformation("Diretório criado para arquivo: {Path}", fileDirectory);
                }

                // Salvar arquivo original
                var originalFilePath = Path.Combine(fileDirectory, file.FileName);
                using (var stream = new FileStream(originalFilePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                _logger.LogInformation("Arquivo salvo: {Path}", originalFilePath);

                return new FileStorageResult
                {
                    Success = true,
                    FilePath = originalFilePath,
                    FileDirectory = fileDirectory,
                    FileName = file.FileName,
                    FileSize = file.Length
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao salvar arquivo");
                return new FileStorageResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        /// <summary>
        /// Salva modelo aprendido em JSON
        /// </summary>
        public async Task<bool> SaveLearnedModelAsync(string fileDirectory, Models.Learning.LayoutModel model)
        {
            try
            {
                var modelPath = Path.Combine(fileDirectory, "layout_learned.json");
                var json = System.Text.Json.JsonSerializer.Serialize(model, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });

                await File.WriteAllTextAsync(modelPath, json);
                _logger.LogInformation("Modelo aprendido salvo: {Path}", modelPath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao salvar modelo aprendido");
                return false;
            }
        }

        /// <summary>
        /// Carrega modelo aprendido de JSON
        /// </summary>
        public async Task<Models.Learning.LayoutModel> LoadLearnedModelAsync(string fileDirectory)
        {
            try
            {
                var modelPath = Path.Combine(fileDirectory, "layout_learned.json");
                if (!File.Exists(modelPath))
                    return null;

                var json = await File.ReadAllTextAsync(modelPath);
                var model = System.Text.Json.JsonSerializer.Deserialize<Models.Learning.LayoutModel>(json);
                return model;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao carregar modelo aprendido");
                return null;
            }
        }

        /// <summary>
        /// Salva log de processamento
        /// </summary>
        public async Task SaveProcessingLogAsync(string fileDirectory, string logContent)
        {
            try
            {
                var logPath = Path.Combine(fileDirectory, $"processing_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                await File.WriteAllTextAsync(logPath, logContent);
                _logger.LogInformation("Log de processamento salvo: {Path}", logPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao salvar log de processamento");
            }
        }

        /// <summary>
        /// Lista todos os arquivos processados
        /// </summary>
        public List<ProcessedFileInfo> ListProcessedFiles()
        {
            var files = new List<ProcessedFileInfo>();

            if (!Directory.Exists(_basePath))
                return files;

            foreach (var directory in Directory.GetDirectories(_basePath))
            {
                var dirInfo = new DirectoryInfo(directory);
                var jsonFiles = Directory.GetFiles(directory, "layout_learned.json");
                
                if (jsonFiles.Any())
                {
                    var model = LoadLearnedModelAsync(directory).GetAwaiter().GetResult();
                    files.Add(new ProcessedFileInfo
                    {
                        FileName = dirInfo.Name,
                        DirectoryPath = directory,
                        HasModel = model != null,
                        LearnedAt = model?.LearnedAt ?? DateTime.MinValue,
                        FileType = model?.FileType ?? "unknown"
                    });
                }
            }

            return files.OrderByDescending(f => f.LearnedAt).ToList();
        }
    }

    public class FileStorageResult
    {
        public bool Success { get; set; }
        public string FilePath { get; set; }
        public string FileDirectory { get; set; }
        public string FileName { get; set; }
        public long FileSize { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class ProcessedFileInfo
    {
        public string FileName { get; set; }
        public string DirectoryPath { get; set; }
        public bool HasModel { get; set; }
        public DateTime LearnedAt { get; set; }
        public string FileType { get; set; }
    }
}

