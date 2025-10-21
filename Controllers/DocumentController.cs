using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DocumentController : ControllerBase
    {
        private readonly ILogger<DocumentController> _logger;
        private readonly string _documentsPath;

        public DocumentController(ILogger<DocumentController> logger, IConfiguration configuration)
        {
            _logger = logger;
            _documentsPath = Path.Combine(Directory.GetCurrentDirectory(), "Documentos");
        }

        [HttpGet("layouts")]
        public IActionResult GetLayouts()
        {
            try
            {
                var layoutPath = Path.Combine(_documentsPath, "Layout");
                if (!Directory.Exists(layoutPath))
                    return NotFound("Pasta de layouts não encontrada");

                var layoutFiles = Directory.GetFiles(layoutPath, "*.xml")
                    .Select(file => new
                    {
                        fileName = Path.GetFileName(file),
                        filePath = file,
                        lastModified = System.IO.File.GetLastWriteTime(file),
                        size = new FileInfo(file).Length
                    })
                    .OrderBy(f => f.fileName)
                    .ToList();

                return Ok(new
                {
                    success = true,
                    count = layoutFiles.Count,
                    layouts = layoutFiles
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao listar layouts");
                return StatusCode(500, $"Erro interno: {ex.Message}");
            }
        }

        [HttpGet("documents")]
        public IActionResult GetDocuments()
        {
            try
            {
                var documentPath = Path.Combine(_documentsPath, "Documento");
                if (!Directory.Exists(documentPath))
                    return NotFound("Pasta de documentos não encontrada");

                var documentFiles = Directory.GetFiles(documentPath)
                    .Select(file => new
                    {
                        fileName = Path.GetFileName(file),
                        filePath = file,
                        lastModified = System.IO.File.GetLastWriteTime(file),
                        size = new FileInfo(file).Length,
                        type = Path.GetExtension(file).ToLower()
                    })
                    .OrderBy(f => f.fileName)
                    .ToList();

                return Ok(new
                {
                    success = true,
                    count = documentFiles.Count,
                    documents = documentFiles
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao listar documentos");
                return StatusCode(500, $"Erro interno: {ex.Message}");
            }
        }

        [HttpGet("excel-files")]
        public IActionResult GetExcelFiles()
        {
            try
            {
                var excelPath = Path.Combine(_documentsPath, "Excel");
                if (!Directory.Exists(excelPath))
                    return NotFound("Pasta de Excel não encontrada");

                var excelFiles = Directory.GetFiles(excelPath, "*.xlsx")
                    .Concat(Directory.GetFiles(excelPath, "*.xls"))
                    .Select(file => new
                    {
                        fileName = Path.GetFileName(file),
                        filePath = file,
                        lastModified = System.IO.File.GetLastWriteTime(file),
                        size = new FileInfo(file).Length
                    })
                    .OrderBy(f => f.fileName)
                    .ToList();

                return Ok(new
                {
                    success = true,
                    count = excelFiles.Count,
                    excelFiles = excelFiles
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao listar arquivos Excel");
                return StatusCode(500, $"Erro interno: {ex.Message}");
            }
        }

        [HttpGet("layout/{fileName}")]
        public IActionResult GetLayout(string fileName)
        {
            try
            {
                var layoutPath = Path.Combine(_documentsPath, "Layout", fileName);
                if (!System.IO.File.Exists(layoutPath))
                    return NotFound($"Layout {fileName} não encontrado");

                var content = System.IO.File.ReadAllText(layoutPath);
                return Ok(new
                {
                    success = true,
                    fileName = fileName,
                    content = content,
                    size = content.Length
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao ler layout {FileName}", fileName);
                return StatusCode(500, $"Erro interno: {ex.Message}");
            }
        }

        [HttpGet("document/{fileName}")]
        public IActionResult GetDocument(string fileName)
        {
            try
            {
                var documentPath = Path.Combine(_documentsPath, "Documento", fileName);
                if (!System.IO.File.Exists(documentPath))
                    return NotFound($"Documento {fileName} não encontrado");

                var content = System.IO.File.ReadAllText(documentPath);
                return Ok(new
                {
                    success = true,
                    fileName = fileName,
                    content = content,
                    size = content.Length
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao ler documento {FileName}", fileName);
                return StatusCode(500, $"Erro interno: {ex.Message}");
            }
        }

        [HttpGet("excel/{fileName}")]
        public IActionResult GetExcelFile(string fileName)
        {
            try
            {
                var excelPath = Path.Combine(_documentsPath, "Excel", fileName);
                if (!System.IO.File.Exists(excelPath))
                    return NotFound($"Arquivo Excel {fileName} não encontrado");

                var fileBytes = System.IO.File.ReadAllBytes(excelPath);
                return File(fileBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao ler arquivo Excel {FileName}", fileName);
                return StatusCode(500, $"Erro interno: {ex.Message}");
            }
        }

        [HttpGet("structure")]
        public IActionResult GetDocumentStructure()
        {
            try
            {
                var structure = new
                {
                    documentsPath = _documentsPath,
                    layout = new
                    {
                        path = Path.Combine(_documentsPath, "Layout"),
                        exists = Directory.Exists(Path.Combine(_documentsPath, "Layout")),
                        files = Directory.Exists(Path.Combine(_documentsPath, "Layout")) 
                            ? Directory.GetFiles(Path.Combine(_documentsPath, "Layout")).Length 
                            : 0
                    },
                    document = new
                    {
                        path = Path.Combine(_documentsPath, "Documento"),
                        exists = Directory.Exists(Path.Combine(_documentsPath, "Documento")),
                        files = Directory.Exists(Path.Combine(_documentsPath, "Documento")) 
                            ? Directory.GetFiles(Path.Combine(_documentsPath, "Documento")).Length 
                            : 0
                    },
                    excel = new
                    {
                        path = Path.Combine(_documentsPath, "Excel"),
                        exists = Directory.Exists(Path.Combine(_documentsPath, "Excel")),
                        files = Directory.Exists(Path.Combine(_documentsPath, "Excel")) 
                            ? Directory.GetFiles(Path.Combine(_documentsPath, "Excel")).Length 
                            : 0
                    }
                };

                return Ok(new
                {
                    success = true,
                    structure = structure
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao obter estrutura de documentos");
                return StatusCode(500, $"Erro interno: {ex.Message}");
            }
        }
    }
}
