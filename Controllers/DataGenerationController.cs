using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Generation;
using LayoutParserApi.Models.Parsing;
using LayoutParserApi.Services.Generation.Interfaces;
using LayoutParserApi.Services.Generation.Implementations;
using LayoutParserApi.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using System.IO.Compression;
using System.Text;

namespace LayoutParserApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DataGenerationController : ControllerBase
    {
        private readonly ISyntheticDataGeneratorService _dataGenerator;
        private readonly IExcelDataProcessor _excelProcessor;
        private readonly ILayoutAnalysisService _layoutAnalysis;
        private readonly ILayoutParserService _parserService;
        private readonly ILogger<DataGenerationController> _logger;

        public DataGenerationController(
            ISyntheticDataGeneratorService dataGenerator,
            IExcelDataProcessor excelProcessor,
            ILayoutAnalysisService layoutAnalysis,
            ILayoutParserService parserService,
            ILogger<DataGenerationController> logger)
        {
            _dataGenerator = dataGenerator;
            _excelProcessor = excelProcessor;
            _layoutAnalysis = layoutAnalysis;
            _parserService = parserService;
            _logger = logger;
        }

        [HttpPost("generate-synthetic")]
        public async Task<IActionResult> GenerateSyntheticData(
            IFormFile layoutFile,
            IFormFile excelFile = null,
            int numberOfRecords = 2,
            bool useAI = true)
        {
            if (layoutFile == null)
                return BadRequest("Arquivo de layout é obrigatório");

            if (Path.GetExtension(layoutFile.FileName).ToLower() != ".xml")
                return BadRequest("O arquivo de layout deve ser XML");

            try
            {
                _logger.LogInformation("Iniciando geração de {Count} registros sintéticos", numberOfRecords);

                // Carregar layout
                var layout = await LoadLayoutFromFile(layoutFile);
                if (layout == null)
                    return BadRequest("Erro ao carregar layout XML");

                // Processar Excel se fornecido
                ExcelDataContext excelContext = null;
                if (excelFile != null)
                {
                    var excelResult = await _excelProcessor.ProcessExcelFileAsync(excelFile.OpenReadStream(), excelFile.FileName);
                    if (excelResult.Success)
                    {
                        excelContext = excelResult.DataContext;
                        _logger.LogInformation("Excel processado: {RowCount} linhas, {ColumnCount} colunas", 
                            excelContext.RowCount, excelContext.Headers.Count);
                    }
                    else
                    {
                        _logger.LogWarning("Erro ao processar Excel: {Error}", excelResult.ErrorMessage);
                    }
                }

                // Criar requisição de geração
                var request = new SyntheticDataRequest
                {
                    Layout = layout,
                    NumberOfRecords = numberOfRecords,
                    ExcelContext = excelContext,
                    UseAI = useAI
                };

                // Gerar dados sintéticos
                var result = await _dataGenerator.GenerateSyntheticDataAsync(request);

                if (!result.Success)
                    return BadRequest($"Erro na geração: {result.ErrorMessage}");

                return Ok(new
                {
                    success = true,
                    totalRecords = result.TotalRecords,
                    generationTime = result.GenerationTime.TotalMilliseconds,
                    generatedLines = result.GeneratedLines,
                    metadata = result.GenerationMetadata,
                    warnings = result.Warnings
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro durante a geração de dados sintéticos");
                return StatusCode(500, $"Erro interno: {ex.Message}");
            }
        }

        [HttpPost("analyze-layout")]
        public async Task<IActionResult> AnalyzeLayout(IFormFile layoutFile)
        {
            if (layoutFile == null)
                return BadRequest("Arquivo de layout é obrigatório");

            try
            {
                var layout = await LoadLayoutFromFile(layoutFile);
                if (layout == null)
                    return BadRequest("Erro ao carregar layout XML");

                var analysis = await _layoutAnalysis.AnalyzeLayoutForAIAsync(layout);

                return Ok(new
                {
                    success = true,
                    layoutType = analysis.LayoutType,
                    layoutName = analysis.LayoutName,
                    totalFields = analysis.TotalFields,
                    totalLines = analysis.TotalLines,
                    fields = analysis.Fields,
                    lineTypes = analysis.LineTypes,
                    metadata = analysis.Metadata
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao analisar layout");
                return StatusCode(500, $"Erro interno: {ex.Message}");
            }
        }

        [HttpPost("process-excel")]
        public async Task<IActionResult> ProcessExcel(IFormFile excelFile)
        {
            if (excelFile == null)
                return BadRequest("Arquivo Excel é obrigatório");

            try
            {
                var result = await _excelProcessor.ProcessExcelFileAsync(excelFile.OpenReadStream(), excelFile.FileName);

                if (!result.Success)
                    return BadRequest($"Erro ao processar Excel: {result.ErrorMessage}");

                return Ok(new
                {
                    success = true,
                    fileName = result.DataContext.FileName,
                    rowCount = result.DataContext.RowCount,
                    headers = result.DataContext.Headers,
                    columnTypes = result.DataContext.ColumnTypes,
                    fieldMappings = result.FieldMappings,
                    warnings = result.Warnings
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao processar Excel");
                return StatusCode(500, $"Erro interno: {ex.Message}");
            }
        }

        [HttpPost("analyze-patterns")]
        public async Task<IActionResult> AnalyzePatterns(
            IFormFile layoutFile,
            IFormFile sampleDataFile)
        {
            if (layoutFile == null || sampleDataFile == null)
                return BadRequest("Arquivos de layout e dados de exemplo são obrigatórios");

            try
            {
                var layout = await LoadLayoutFromFile(layoutFile);
                if (layout == null)
                    return BadRequest("Erro ao carregar layout XML");

                // Processar dados de exemplo
                var sampleData = await ReadSampleData(sampleDataFile);
                if (!sampleData.Any())
                    return BadRequest("Nenhum dado de exemplo encontrado");

                // Analisar padrões dos campos
                var fieldAnalyses = new List<object>();
                var layoutFields = await _layoutAnalysis.ExtractFieldMetadataAsync(layout);

                foreach (var field in layoutFields.Take(10)) // Limitar para performance
                {
                    var sampleValues = sampleData
                        .Where(s => s.FieldName.Equals(field.Name, StringComparison.OrdinalIgnoreCase))
                        .Select(s => s.Value)
                        .ToList();

                    if (sampleValues.Any())
                    {
                        var patternAnalysis = await _layoutAnalysis.AnalyzeFieldPatternsAsync(field.Name, sampleValues);
                        fieldAnalyses.Add(new
                        {
                            fieldName = field.Name,
                            pattern = patternAnalysis.DetectedPattern,
                            strategy = patternAnalysis.SuggestedGenerationStrategy,
                            commonValues = patternAnalysis.CommonValues,
                            sampleCount = sampleValues.Count
                        });
                    }
                }

                return Ok(new
                {
                    success = true,
                    totalFields = layoutFields.Count,
                    analyzedFields = fieldAnalyses.Count,
                    fieldAnalyses = fieldAnalyses
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao analisar padrões");
                return StatusCode(500, $"Erro interno: {ex.Message}");
            }
        }

        [HttpGet("test")]
        public IActionResult Test()
        {
            return Ok("Data Generation API - Funcionando");
        }

        private async Task<Layout> LoadLayoutFromFile(IFormFile layoutFile)
        {
            try
            {
                using var stream = layoutFile.OpenReadStream();
                return await XmlLayoutLoader.LoadLayoutFromXmlAsync(stream);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao carregar layout");
                return null;
            }
        }

        private async Task<List<ParsedField>> ReadSampleData(IFormFile sampleDataFile)
        {
            try
            {
                using var stream = sampleDataFile.OpenReadStream();
                using var reader = new StreamReader(stream);
                var content = await reader.ReadToEndAsync();
                
                // Implementar parsing dos dados de exemplo
                // Por enquanto, retornar lista vazia
                return new List<ParsedField>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao ler dados de exemplo");
                return new List<ParsedField>();
            }
        }

        /// <summary>
        /// Valida um arquivo gerado sinteticamente usando o mesmo método de validação de arquivos reais
        /// </summary>
        private async Task<ValidationSummary> ValidateGeneratedFileAsync(List<string> generatedLines, Layout layout)
        {
            var summary = new ValidationSummary();
            
            try
            {
                // Converter linhas geradas em um texto (como se fosse um arquivo real)
                var generatedText = string.Join(Environment.NewLine, generatedLines);
                
                // Criar streams para validação
                using var layoutStream = new MemoryStream();
                using var txtStream = new MemoryStream();
                
                // Serializar layout para XML
                var layoutXml = SerializeLayoutToXml(layout);
                var layoutBytes = Encoding.UTF8.GetBytes(layoutXml);
                await layoutStream.WriteAsync(layoutBytes, 0, layoutBytes.Length);
                layoutStream.Position = 0;
                
                // Escrever texto gerado
                var txtBytes = Encoding.UTF8.GetBytes(generatedText);
                await txtStream.WriteAsync(txtBytes, 0, txtBytes.Length);
                txtStream.Position = 0;
                
                // Usar o mesmo método de parsing/validação usado para arquivos reais
                var parsingResult = await _parserService.ParseAsync(layoutStream, txtStream);
                
                if (parsingResult.Success && parsingResult.ParsedFields != null)
                {
                    summary.TotalFields = parsingResult.ParsedFields.Count;
                    summary.ValidFields = parsingResult.ParsedFields.Count(f => f.Status == "ok");
                    summary.ErrorCount = parsingResult.ParsedFields.Count(f => f.Status == "error");
                    summary.WarningCount = parsingResult.ParsedFields.Count(f => f.Status == "warning");
                    summary.HasErrors = summary.ErrorCount > 0;
                    summary.HasWarnings = summary.WarningCount > 0;
                    
                    // Coletar detalhes dos erros e warnings
                    summary.Details = parsingResult.ParsedFields
                        .Where(f => f.Status != "ok")
                        .Select(f => new
                        {
                            lineName = f.LineName,
                            fieldName = f.FieldName,
                            sequence = f.Sequence,
                            status = f.Status,
                            value = f.Value?.Substring(0, Math.Min(50, f.Value?.Length ?? 0)),
                            isRequired = f.IsRequired,
                            occurrence = f.Occurrence
                        })
                        .ToList();
                    
                    _logger.LogInformation("Validação concluída: {Total} campos, {Valid} válidos, {Errors} erros, {Warnings} avisos",
                        summary.TotalFields, summary.ValidFields, summary.ErrorCount, summary.WarningCount);
                }
                else
                {
                    summary.HasErrors = true;
                    summary.ErrorCount = 1;
                    summary.Details = new List<object> { new { error = parsingResult.ErrorMessage ?? "Falha na validação" } };
                    _logger.LogWarning("Falha na validação do arquivo gerado: {Error}", parsingResult.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao validar arquivo gerado");
                summary.HasErrors = true;
                summary.ErrorCount = 1;
                summary.Details = new List<object> { new { error = ex.Message } };
            }
            
            return summary;
        }

        private string SerializeLayoutToXml(Layout layout)
        {
            try
            {
                var serializer = new System.Xml.Serialization.XmlSerializer(typeof(Layout));
                using var stringWriter = new StringWriter();
                serializer.Serialize(stringWriter, layout);
                return stringWriter.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao serializar layout para XML");
                return string.Empty;
            }
        }

        private class ValidationSummary
        {
            public int TotalFields { get; set; }
            public int ValidFields { get; set; }
            public int ErrorCount { get; set; }
            public int WarningCount { get; set; }
            public bool HasErrors { get; set; }
            public bool HasWarnings { get; set; }
            public List<object> Details { get; set; } = new List<object>();
        }

        [HttpPost("generate-synthetic-zip")]
        public async Task<IActionResult> GenerateSyntheticDataZip(
            IFormFile layoutFile,
            IFormFile excelFile = null,
            int numberOfRecords = 2,
            int numberOfFiles = 1,
            bool useAI = true)
        {
            if (layoutFile == null)
                return BadRequest("Arquivo de layout é obrigatório");

            if (Path.GetExtension(layoutFile.FileName).ToLower() != ".xml")
                return BadRequest("O arquivo de layout deve ser XML");

            try
            {
                _logger.LogInformation("Iniciando geração de {Files} arquivos com {Records} registros cada", numberOfFiles, numberOfRecords);

                // Carregar layout
                var layout = await LoadLayoutFromFile(layoutFile);
                if (layout == null)
                    return BadRequest("Erro ao carregar layout XML");

                // Processar Excel se fornecido
                ExcelDataContext excelContext = null;
                if (excelFile != null)
                {
                    var excelResult = await _excelProcessor.ProcessExcelFileAsync(excelFile.OpenReadStream());
                    excelContext = excelResult?.DataContext;
                }

                // Gerar dados para cada arquivo
                var zipStream = new MemoryStream();
                var validationResults = new List<object>();
                
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, true))
                {
                    for (int fileIndex = 1; fileIndex <= numberOfFiles; fileIndex++)
                    {
                        var request = new SyntheticDataRequest
                        {
                            Layout = layout,
                            NumberOfRecords = numberOfRecords,
                            UseAI = useAI,
                            ExcelContext = excelContext
                        };

                        var result = await _dataGenerator.GenerateSyntheticDataAsync(request);
                        
                        if (result.Success && result.GeneratedLines.Any())
                        {
                            // Validar o arquivo gerado usando o mesmo método de validação de arquivos reais
                            var validationResult = await ValidateGeneratedFileAsync(result.GeneratedLines, layout);
                            
                            // Se houver erros críticos, registrar mas continuar
                            if (validationResult.HasErrors)
                            {
                                _logger.LogWarning("Arquivo {FileIndex} gerado com {ErrorCount} erros de validação", 
                                    fileIndex, validationResult.ErrorCount);
                            }
                            
                            // Se houver warnings, registrar
                            if (validationResult.HasWarnings)
                            {
                                _logger.LogInformation("Arquivo {FileIndex} gerado com {WarningCount} avisos de validação", 
                                    fileIndex, validationResult.WarningCount);
                            }
                            
                            // Adicionar ao ZIP apenas se passar na validação básica ou se tiver apenas warnings
                            if (!validationResult.HasErrors || validationResult.ErrorCount <= validationResult.WarningCount)
                            {
                                var fileName = $"arquivo_{fileIndex:D3}.txt";
                                var entry = archive.CreateEntry(fileName);
                                
                                using (var entryStream = entry.Open())
                                using (var writer = new StreamWriter(entryStream))
                                {
                                    foreach (var line in result.GeneratedLines)
                                    {
                                        await writer.WriteLineAsync(line);
                                    }
                                }
                                
                                validationResults.Add(new
                                {
                                    fileIndex = fileIndex,
                                    fileName = fileName,
                                    totalLines = result.GeneratedLines.Count,
                                    totalFields = validationResult.TotalFields,
                                    validFields = validationResult.ValidFields,
                                    errorFields = validationResult.ErrorCount,
                                    warningFields = validationResult.WarningCount,
                                    isValid = !validationResult.HasErrors,
                                    validationDetails = validationResult.Details
                                });
                                
                                _logger.LogInformation("Arquivo {FileIndex} gerado e validado: {ValidFields} válidos, {ErrorFields} erros, {WarningFields} avisos", 
                                    fileIndex, validationResult.ValidFields, validationResult.ErrorCount, validationResult.WarningCount);
                            }
                            else
                            {
                                _logger.LogError("Arquivo {FileIndex} rejeitado devido a muitos erros de validação: {ErrorFields} erros", 
                                    fileIndex, validationResult.ErrorCount);
                                
                                validationResults.Add(new
                                {
                                    fileIndex = fileIndex,
                                    fileName = $"arquivo_{fileIndex:D3}.txt",
                                    rejected = true,
                                    reason = "Muitos erros de validação",
                                    errorFields = validationResult.ErrorCount,
                                    warningFields = validationResult.WarningCount,
                                    validationDetails = validationResult.Details
                                });
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Falha ao gerar arquivo {FileIndex}", fileIndex);
                            validationResults.Add(new
                            {
                                fileIndex = fileIndex,
                                rejected = true,
                                reason = result.ErrorMessage ?? "Falha na geração"
                            });
                        }
                    }
                }

                zipStream.Position = 0;
                var zipBytes = zipStream.ToArray();
                
                var acceptedFiles = validationResults.Count(r => 
                {
                    var obj = r as dynamic;
                    return obj?.rejected != true;
                });
                
                _logger.LogInformation("ZIP gerado com {Size} bytes contendo {Accepted}/{Total} arquivos aceitos", 
                    zipBytes.Length, acceptedFiles, numberOfFiles);

                // Retornar ZIP com metadata de validação nos headers
                Response.Headers.Add("X-Validation-Results", System.Text.Json.JsonSerializer.Serialize(validationResults));
                Response.Headers.Add("X-Accepted-Files", acceptedFiles.ToString());
                Response.Headers.Add("X-Total-Files", numberOfFiles.ToString());

                return File(zipBytes, "application/zip", $"dados_sinteticos_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao gerar dados sintéticos em ZIP");
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }
}
