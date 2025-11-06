using LayoutParserApi.Models.Learning;
using LayoutParserApi.Services.Learning;
using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class LearningController : ControllerBase
    {
        private readonly FileStorageService _fileStorage;
        private readonly LayoutLearningService _learningService;
        private readonly XmlFormatterService _xmlFormatter;
        private readonly ILogger<LearningController> _logger;

        public LearningController(
            FileStorageService fileStorage,
            LayoutLearningService learningService,
            XmlFormatterService xmlFormatter,
            ILogger<LearningController> logger)
        {
            _fileStorage = fileStorage;
            _learningService = learningService;
            _xmlFormatter = xmlFormatter;
            _logger = logger;
        }

        /// <summary>
        /// Upload e aprendizado de arquivo
        /// </summary>
        [HttpPost("upload-and-learn")]
        public async Task<IActionResult> UploadAndLearn(IFormFile file)
        {
            if (file == null)
                return BadRequest("Arquivo não fornecido");

            var allowedExtensions = new[] { ".txt", ".xml", ".mqseries" };
            var extension = Path.GetExtension(file.FileName).ToLower();
            
            if (!allowedExtensions.Contains(extension))
                return BadRequest($"Tipo de arquivo não suportado. Use: {string.Join(", ", allowedExtensions)}");

            try
            {
                // Salvar arquivo
                var storageResult = await _fileStorage.SaveUploadedFileAsync(file);
                if (!storageResult.Success)
                    return BadRequest($"Erro ao salvar arquivo: {storageResult.ErrorMessage}");

                // Determinar tipo de arquivo
                var fileType = extension == ".xml" ? "xml" : "txt";

                // Aprender estrutura
                var learningResult = await _learningService.LearnFromFileAsync(storageResult.FilePath, fileType);
                
                if (!learningResult.Success)
                    return BadRequest($"Erro no aprendizado: {learningResult.Message}");

                // Salvar modelo aprendido
                learningResult.LearnedModel.FilePath = storageResult.FilePath;
                await _fileStorage.SaveLearnedModelAsync(storageResult.FileDirectory, learningResult.LearnedModel);

                // Salvar log
                var logContent = $"Processamento: {DateTime.Now}\n" +
                               $"Arquivo: {file.FileName}\n" +
                               $"Tipo: {fileType}\n" +
                               $"Campos detectados: {learningResult.LearnedModel.TotalFields}\n" +
                               $"Tempo: {learningResult.ProcessingTime.TotalMilliseconds}ms\n" +
                               $"Mensagem: {learningResult.Message}";
                await _fileStorage.SaveProcessingLogAsync(storageResult.FileDirectory, logContent);

                return Ok(new
                {
                    success = true,
                    filePath = storageResult.FilePath,
                    fileDirectory = storageResult.FileDirectory,
                    model = learningResult.LearnedModel,
                    processingTime = learningResult.ProcessingTime.TotalMilliseconds
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao processar upload e aprendizado");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Analisa arquivo e retorna conteúdo formatado
        /// </summary>
        [HttpPost("analyze")]
        [HttpGet("analyze")]
        public async Task<IActionResult> AnalyzeFile(IFormFile file = null, [FromQuery] string filePath = null)
        {
            string content;
            string fileName;
            string extension;
            long fileSize;

            // Se filePath foi fornecido, ler do sistema de arquivos
            if (!string.IsNullOrEmpty(filePath) && System.IO.File.Exists(filePath))
            {
                content = await System.IO.File.ReadAllTextAsync(filePath);
                fileName = Path.GetFileName(filePath);
                extension = Path.GetExtension(filePath).ToLower();
                var fileInfo = new FileInfo(filePath);
                fileSize = fileInfo.Length;
            }
            else if (file != null)
            {
                using var reader = new StreamReader(file.OpenReadStream());
                content = await reader.ReadToEndAsync();
                fileName = file.FileName;
                extension = Path.GetExtension(file.FileName).ToLower();
                fileSize = file.Length;
            }
            else
            {
                return BadRequest("Arquivo não fornecido");
            }

            try
            {
                var result = new FileAnalysisResult
                {
                    FileName = fileName,
                    FilePath = filePath ?? "",
                    FileSize = fileSize,
                    FileType = extension == ".xml" ? "xml" : "txt",
                    Content = content
                };

                // Formatar se for XML
                if (result.FileType == "xml")
                {
                    result.FormattedContent = _xmlFormatter.FormatXml(content);
                    var validation = _xmlFormatter.ValidateXml(content);
                    result.AnalysisData["isValid"] = validation.IsValid;
                    result.AnalysisData["validationErrors"] = validation.Errors;
                    result.AnalysisData["structure"] = _xmlFormatter.GenerateStructure(content);
                }
                else
                {
                    var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    result.LineCount = lines.Length;
                    result.AnalysisData["lineLength"] = lines.FirstOrDefault()?.Length ?? 0;
                    
                    // Tentar carregar modelo aprendido se filePath foi fornecido
                    if (!string.IsNullOrEmpty(filePath))
                    {
                        var fileDirectory = Path.GetDirectoryName(filePath);
                        var model = await _fileStorage.LoadLearnedModelAsync(fileDirectory);
                        if (model != null)
                        {
                            result.LearnedModel = model;
                            result.AnalysisData["hasModel"] = true;
                        }
                    }
                }

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao analisar arquivo");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Lista arquivos processados
        /// </summary>
        [HttpGet("processed-files")]
        public IActionResult ListProcessedFiles()
        {
            try
            {
                var files = _fileStorage.ListProcessedFiles();
                return Ok(files);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao listar arquivos processados");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Carrega modelo aprendido de um arquivo
        /// </summary>
        [HttpGet("model/{fileName}")]
        public async Task<IActionResult> GetLearnedModel(string fileName)
        {
            try
            {
                var basePath = @"C:\inetpub\wwwroot\layoutparser\Exemplo";
                var fileDirectory = Path.Combine(basePath, fileName);
                
                if (!Directory.Exists(fileDirectory))
                    return NotFound($"Diretório não encontrado: {fileName}");

                var model = await _fileStorage.LoadLearnedModelAsync(fileDirectory);
                if (model == null)
                    return NotFound("Modelo não encontrado");

                return Ok(model);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao carregar modelo");
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }
}

