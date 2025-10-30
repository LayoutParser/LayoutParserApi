using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Generation;
using LayoutParserApi.Services.Generation.Interfaces;
using LayoutParserApi.Services.Generation.Implementations;
using Microsoft.AspNetCore.Mvc;
using System.IO.Compression;

namespace LayoutParserApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DataGenerationController : ControllerBase
    {
        private readonly ISyntheticDataGeneratorService _dataGenerator;
        private readonly IExcelDataProcessor _excelProcessor;
        private readonly ILayoutAnalysisService _layoutAnalysis;
        private readonly ILogger<DataGenerationController> _logger;

        public DataGenerationController(
            ISyntheticDataGeneratorService dataGenerator,
            IExcelDataProcessor excelProcessor,
            ILayoutAnalysisService layoutAnalysis,
            ILogger<DataGenerationController> logger)
        {
            _dataGenerator = dataGenerator;
            _excelProcessor = excelProcessor;
            _layoutAnalysis = layoutAnalysis;
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
                            
                            _logger.LogInformation("Arquivo {FileIndex} gerado com {Lines} linhas", fileIndex, result.GeneratedLines.Count);
                        }
                        else
                        {
                            _logger.LogWarning("Falha ao gerar arquivo {FileIndex}", fileIndex);
                        }
                    }
                }

                zipStream.Position = 0;
                var zipBytes = zipStream.ToArray();
                
                _logger.LogInformation("ZIP gerado com {Size} bytes contendo {Files} arquivos", zipBytes.Length, numberOfFiles);

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
