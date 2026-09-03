using Microsoft.AspNetCore.Mvc;

using LayoutParserApi.Services.Security;

namespace LayoutParserApi.Controllers
{
    /// <summary>
    /// Navegação da pasta local <c>Documentos/</c> (Layout/Documento/Excel) do servidor — usado
    /// para inspecionar arquivos de exemplo/fixture já disponíveis no host, fora do fluxo de
    /// upload principal (<c>ParseController</c>).
    /// </summary>
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

        /// <summary>Lista os arquivos XML da pasta <c>Documentos/Layout</c>.</summary>
        /// <response code="200">Lista de layouts (nome, caminho, tamanho, data).</response>
        /// <response code="404">Pasta <c>Layout</c> não existe no servidor.</response>
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

        /// <summary>Lista os arquivos da pasta <c>Documentos/Documento</c> (documentos de exemplo TXT/XML).</summary>
        /// <response code="200">Lista de documentos.</response>
        /// <response code="404">Pasta <c>Documento</c> não existe no servidor.</response>
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

        /// <summary>Lista planilhas (.xlsx/.xls) da pasta <c>Documentos/Excel</c> — specs-fonte machine-parseáveis.</summary>
        /// <response code="200">Lista de planilhas.</response>
        /// <response code="404">Pasta <c>Excel</c> não existe no servidor.</response>
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

        /// <summary>Lê o conteúdo de um layout XML específico dentro de <c>Documentos/Layout</c>.</summary>
        /// <param name="fileName">Nome do arquivo (sem caminho — validado contra path traversal por <see cref="SafePathResolver"/>).</param>
        /// <response code="200">Conteúdo do layout.</response>
        /// <response code="404">Arquivo inexistente ou nome inválido (mesma resposta — não revela qual caso).</response>
        [HttpGet("layout/{fileName}")]
        public IActionResult GetLayout(string fileName)
        {
            try
            {
                // ✅ P0 — path traversal: nome cru do cliente resolvido pelo helper único.
                // null = recusa (nome inválido OU escapa da base) → 404 sem revelar existência.
                var layoutPath = SafePathResolver.Resolve(Path.Combine(_documentsPath, "Layout"), fileName);
                if (layoutPath is null || !System.IO.File.Exists(layoutPath))
                    return NotFound($"Layout {fileName} não encontrado");

                // SCS0018 (issue #88): falso positivo confirmado — o SCS não reconhece
                // SafePathResolver.Resolve (canonicalização + checagem de base) como sanitizador,
                // mas o caminho lido aqui já saiu validado/nulo (null tratado acima) do helper único.
#pragma warning disable SCS0018
                var content = System.IO.File.ReadAllText(layoutPath);
#pragma warning restore SCS0018
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

        /// <summary>Lê o conteúdo de um documento específico dentro de <c>Documentos/Documento</c>.</summary>
        /// <param name="fileName">Nome do arquivo (sem caminho — validado contra path traversal).</param>
        /// <response code="200">Conteúdo do documento.</response>
        /// <response code="404">Arquivo inexistente ou nome inválido.</response>
        [HttpGet("document/{fileName}")]
        public IActionResult GetDocument(string fileName)
        {
            try
            {
                // ✅ P0 — path traversal (ver GetLayout).
                var documentPath = SafePathResolver.Resolve(Path.Combine(_documentsPath, "Documento"), fileName);
                if (documentPath is null || !System.IO.File.Exists(documentPath))
                    return NotFound($"Documento {fileName} não encontrado");

                // SCS0018 (issue #88): mesmo falso positivo de GetLayout — path já saiu de
                // SafePathResolver.Resolve, o SCS não reconhece o sanitizador custom.
#pragma warning disable SCS0018
                var content = System.IO.File.ReadAllText(documentPath);
#pragma warning restore SCS0018
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

        /// <summary>Baixa uma planilha específica de <c>Documentos/Excel</c> (binário, não JSON).</summary>
        /// <param name="fileName">Nome do arquivo (sem caminho — validado contra path traversal).</param>
        /// <returns>Arquivo binário com content-type de planilha do Office.</returns>
        /// <response code="200">Bytes da planilha.</response>
        /// <response code="404">Arquivo inexistente ou nome inválido.</response>
        [HttpGet("excel/{fileName}")]
        public IActionResult GetExcelFile(string fileName)
        {
            try
            {
                // ✅ P0 — path traversal (ver GetLayout).
                var excelPath = SafePathResolver.Resolve(Path.Combine(_documentsPath, "Excel"), fileName);
                if (excelPath is null || !System.IO.File.Exists(excelPath))
                    return NotFound($"Arquivo Excel {fileName} não encontrado");

                // SCS0018 (issue #88): mesmo falso positivo de GetLayout/GetDocument.
#pragma warning disable SCS0018
                var fileBytes = System.IO.File.ReadAllBytes(excelPath);
#pragma warning restore SCS0018
                return File(fileBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao ler arquivo Excel {FileName}", fileName);
                return StatusCode(500, $"Erro interno: {ex.Message}");
            }
        }

        /// <summary>Resumo da árvore <c>Documentos/</c> (existência e contagem de arquivos por subpasta) — diagnóstico rápido, sem listar cada arquivo.</summary>
        /// <response code="200">Estrutura resumida.</response>
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
