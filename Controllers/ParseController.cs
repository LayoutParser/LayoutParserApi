using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Responses;
using LayoutParserApi.Models.Configuration;
using LayoutParserApi.Services.Filters;
using LayoutParserApi.Services.Parsing.Interfaces;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Learning;

using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ParseController : ControllerBase
    {
        private readonly ILayoutParserService _parserService;
        private readonly ILogger<ParseController> _logger;
        private readonly ILayoutDetector _layoutDetector;
        private readonly FileStorageService _fileStorage;
        private readonly LayoutLearningService _learningService;
        private readonly IConfiguration _configuration;

        public ParseController(
            ILayoutParserService parserService, 
            ILogger<ParseController> logger, 
            ILayoutDetector layoutDetector,
            FileStorageService fileStorage,
            LayoutLearningService learningService,
            IConfiguration configuration)
        {
            _parserService = parserService;
            _logger = logger;
            _layoutDetector = layoutDetector;
            _fileStorage = fileStorage;
            _learningService = learningService;
            _configuration = configuration;
        }

        [ServiceFilter(typeof(AuditActionFilter))]
        [HttpPost("upload")]
        public async Task<IActionResult> Upload(IFormFile layoutFile, IFormFile txtFile, [FromForm] string layoutName = null)
        {
            if (layoutFile == null || txtFile == null)
                return BadRequest("Layout XML e arquivo são obrigatórios.");

            if (Path.GetExtension(layoutFile.FileName).ToLower() != ".xml")
                return BadRequest("O arquivo de layout deve ser XML.");

            try
            {
                // Detectar tipo de arquivo pela extensão e conteúdo
                var fileExtension = Path.GetExtension(txtFile.FileName).ToLower();
                var isXmlFile = fileExtension == ".xml";

                // Ler conteúdo do arquivo para detecção de tipo
                using var txtStreamForDetection = txtFile.OpenReadStream();
                using var reader = new StreamReader(txtStreamForDetection, leaveOpen: true);
                var sample = await reader.ReadToEndAsync();
                var detectedType = _layoutDetector.DetectType(sample);

                // Se for arquivo XML, retornar indicando que deve ser processado no front-end
                if (isXmlFile || detectedType == "xml")
                {
                    _logger.LogInformation("Arquivo XML detectado, deve ser processado no front-end");
                    return Ok(new
                    {
                        success = true,
                        fileType = "xml",
                        detectedType = "xml",
                        message = "Arquivo XML detectado. Processe no front-end com xmltools.js",
                        content = sample // Retornar conteúdo para processamento no front-end
                    });
                }

                // Salvar arquivo para aprendizado de máquina ANTES de processar
                if (!string.IsNullOrEmpty(layoutName))
                {
                    await SaveFileForLearningAsync(layoutName, txtFile, detectedType);
                }

                // Processar arquivo
                using var layoutStream = layoutFile.OpenReadStream();
                using var txtStream = txtFile.OpenReadStream();

                var result = await _parserService.ParseAsync(layoutStream, txtStream);

                var layoutReestruturado = _parserService.ReestruturarLayout(result.Layout);
                var layoutReordenado = _parserService.ReordenarSequences(layoutReestruturado);

                var flattenedLayout = new Layout
                {
                    LayoutGuid = layoutReordenado.LayoutGuid,
                    LayoutType = layoutReordenado.LayoutType,
                    Name = layoutReordenado.Name,
                    Description = layoutReordenado.Description,
                    LimitOfCaracters = layoutReordenado.LimitOfCaracters,
                    Elements = layoutReordenado.Elements
                };

                if (!result.Success)
                    return BadRequest(result.ErrorMessage);

                var documentStructure = _parserService.BuildDocumentStructure(result);

                // Calcular validações e posições das linhas para o front-end (apenas para layouts configurados)
                List<LineValidationInfo>? lineValidations = null;
                var expectedLineLength = LayoutLineSizeConfiguration.GetLineSizeForLayout(flattenedLayout.LayoutGuid);
                
                if (expectedLineLength.HasValue)
                {
                    lineValidations = _parserService.CalculateLineValidations(flattenedLayout, expectedLineLength.Value);
                }

                return Ok(new
                {
                    success = true,
                    detectedType,
                    layout = flattenedLayout,
                    fields = result.ParsedFields,
                    text = result.RawText,
                    summary = result.Summary,
                    documentStructure = documentStructure,
                    lineValidations = lineValidations // Validações e posições calculadas (apenas para layouts configurados)
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro durante o parsing do XML");
                return StatusCode(500, $"Erro interno: {ex.Message}");
            }
        }

        /// <summary>
        /// Salva arquivo na pasta do layout para aprendizado de máquina
        /// </summary>
        private async Task SaveFileForLearningAsync(string layoutName, IFormFile txtFile, string detectedType)
        {
            try
            {
                _logger.LogInformation("Salvando arquivo para aprendizado: Layout={LayoutName}, Tipo={Type}", layoutName, detectedType);

                // Criar diretório baseado no nome do layout
                var basePath = _configuration["TransformationPipeline:ExamplesPath"] ?? @"C:\inetpub\wwwroot\layoutparser\Examples";
                var layoutDirectory = Path.Combine(basePath, layoutName);

                if (!Directory.Exists(layoutDirectory))
                {
                    Directory.CreateDirectory(layoutDirectory);
                    _logger.LogInformation("Diretório criado: {Path}", layoutDirectory);
                }

                // Salvar arquivo com timestamp para evitar sobrescrita
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var fileExtension = Path.GetExtension(txtFile.FileName);
                var fileName = $"{timestamp}_{txtFile.FileName}";
                var filePath = Path.Combine(layoutDirectory, fileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await txtFile.CopyToAsync(stream);
                }

                _logger.LogInformation("Arquivo salvo para aprendizado: {Path}", filePath);

                // Executar aprendizado de máquina em background (não bloquear resposta)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Determinar tipo de arquivo para aprendizado
                        var fileType = detectedType?.ToLower() switch
                        {
                            "xml" => "xml",
                            "idoc" => "txt",
                            "mqseries" => "txt",
                            _ => "txt"
                        };

                        // Aprender estrutura do arquivo
                        var learningResult = await _learningService.LearnFromFileAsync(filePath, fileType);
                        
                        if (learningResult.Success && learningResult.LearnedModel != null)
                        {
                            // Salvar modelo aprendido
                            learningResult.LearnedModel.FilePath = filePath;
                            await _fileStorage.SaveLearnedModelAsync(layoutDirectory, learningResult.LearnedModel);
                            
                            _logger.LogInformation("Aprendizado concluído para {LayoutName}: {Fields} campos detectados", 
                                layoutName, learningResult.LearnedModel.TotalFields);
                        }
                        else
                        {
                            _logger.LogWarning("Aprendizado falhou para {LayoutName}: {Message}", 
                                layoutName, learningResult.Message);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Erro durante aprendizado de máquina para {LayoutName}", layoutName);
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao salvar arquivo para aprendizado");
                // Não falhar o processamento principal se houver erro no aprendizado
            }
        }
    }
}