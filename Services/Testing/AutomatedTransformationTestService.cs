using LayoutParserApi.Models.Database;
using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Testing.Models;
using LayoutParserApi.Services.Transformation;
using LayoutParserApi.Services.XmlAnalysis;

using System.Xml.Linq;

namespace LayoutParserApi.Services.Testing
{
    /// <summary>
    /// Serviço para executar testes automatizados de transformação usando exemplos MQSeries
    /// </summary>
    public class AutomatedTransformationTestService
    {
        private readonly ILogger<AutomatedTransformationTestService> _logger;
        private readonly ICachedLayoutService _cachedLayoutService;
        private readonly MapperDatabaseService _mapperDatabaseService;
        private readonly TransformationPipelineService _pipelineService;
        private readonly TransformationValidatorService _validatorService;
        private readonly string _examplesBasePath;
        private readonly string _expectedOutputsPath;

        public AutomatedTransformationTestService(
            ILogger<AutomatedTransformationTestService> logger,
            ICachedLayoutService cachedLayoutService,
            MapperDatabaseService mapperDatabaseService,
            TransformationPipelineService pipelineService,
            TransformationValidatorService validatorService,
            IConfiguration configuration)
        {
            _logger = logger;
            _cachedLayoutService = cachedLayoutService;
            _mapperDatabaseService = mapperDatabaseService;
            _pipelineService = pipelineService;
            _validatorService = validatorService;
            _examplesBasePath = configuration["Examples:Path"] ?? @"C:\inetpub\wwwroot\layoutparser\Exemplo";
            _expectedOutputsPath = configuration["TransformationPipeline:ExpectedOutputsPath"] ?? @"C:\inetpub\wwwroot\layoutparser\ExpectedOutputs";

            Directory.CreateDirectory(_expectedOutputsPath);
        }

        /// <summary>
        /// Executa testes automatizados para todos os layouts que têm exemplos
        /// </summary>
        public async Task<TestSuiteResult> RunAllTestsAsync()
        {
            var result = new TestSuiteResult
            {
                Success = true,
                TestResults = new List<TestResult>(),
                StartTime = DateTime.UtcNow
            };

            try
            {
                _logger.LogInformation("Iniciando execução de testes automatizados");

                // Buscar todos os diretórios de exemplos
                var exampleDirectories = Directory.GetDirectories(_examplesBasePath, "LAY_*", SearchOption.TopDirectoryOnly);

                foreach (var exampleDir in exampleDirectories)
                {
                    var layoutName = Path.GetFileName(exampleDir);
                    _logger.LogInformation("Processando testes para layout: {LayoutName}", layoutName);

                    // Executar testes para este layout
                    var testResult = await RunTestsForLayoutAsync(layoutName, exampleDir);
                    result.TestResults.Add(testResult);

                    if (!testResult.Success)
                        result.Success = false;
                }

                result.EndTime = DateTime.UtcNow;
                result.Duration = result.EndTime - result.StartTime;
                result.TotalTests = result.TestResults.Count;
                result.PassedTests = result.TestResults.Count(t => t.Success);
                result.FailedTests = result.TestResults.Count(t => !t.Success);

                _logger.LogInformation("Testes concluídos. Total: {Total}, Passou: {Passed}, Falhou: {Failed}", result.TotalTests, result.PassedTests, result.FailedTests);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao executar testes automatizados");
                result.Success = false;
                result.Errors.Add($"Erro geral: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Executa testes para um layout específico
        /// </summary>
        public async Task<TestResult> RunTestsForLayoutAsync(string layoutName, string examplesDirectory = null)
        {
            var result = new TestResult
            {
                LayoutName = layoutName,
                Success = true,
                TestCases = new List<TestCaseResult>(),
                StartTime = DateTime.UtcNow
            };

            try
            {
                if (string.IsNullOrEmpty(examplesDirectory))
                    examplesDirectory = Path.Combine(_examplesBasePath, layoutName);

                if (!Directory.Exists(examplesDirectory))
                {
                    result.Success = false;
                    result.Errors.Add($"Diretório de exemplos não encontrado: {examplesDirectory}");
                    return result;
                }

                // Buscar layout no Redis
                var layout = await FindLayoutByNameAsync(layoutName);
                if (layout == null)
                {
                    result.Success = false;
                    result.Errors.Add($"Layout não encontrado no Redis: {layoutName}");
                    return result;
                }

                result.LayoutGuid = layout.LayoutGuid != Guid.Empty ? layout.LayoutGuid.ToString() : layout.Id.ToString();

                // Buscar mapeador para este layout (InputLayoutGuid)
                var mapper = await _mapperDatabaseService.GetMapperByInputLayoutGuidAsync(result.LayoutGuid);
                if (mapper == null)
                    result.Warnings.Add($"Nenhum mapeador encontrado para o layout {layoutName}");

                // Buscar arquivos de exemplo TXT
                var exampleFiles = Directory.GetFiles(examplesDirectory, "*.txt", SearchOption.AllDirectories).Concat(Directory.GetFiles(examplesDirectory, "*.mqseries", SearchOption.AllDirectories)).ToList();

                if (!exampleFiles.Any())
                {
                    result.Warnings.Add($"Nenhum arquivo de exemplo encontrado em {examplesDirectory}");
                    return result;
                }

                // Buscar arquivo XML esperado (se existir)
                var expectedXmlPath = Path.Combine(examplesDirectory, "expected_output.xml");
                var expectedXml = File.Exists(expectedXmlPath) ? await File.ReadAllTextAsync(expectedXmlPath) : null;

                // Executar teste para cada arquivo de exemplo
                foreach (var exampleFile in exampleFiles)
                {
                    var testCase = await RunTestCaseAsync(layoutName, exampleFile, expectedXml);
                    result.TestCases.Add(testCase);

                    if (!testCase.Success)
                        result.Success = false;
                }

                result.EndTime = DateTime.UtcNow;
                result.Duration = result.EndTime - result.StartTime;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao executar testes para layout: {LayoutName}", layoutName);
                result.Success = false;
                result.Errors.Add($"Erro: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Executa um caso de teste individual
        /// </summary>
        private async Task<TestCaseResult> RunTestCaseAsync(string layoutName, string exampleFilePath, string expectedXml = null)
        {
            var result = new TestCaseResult
            {
                TestCaseName = Path.GetFileName(exampleFilePath),
                Success = true,
                StartTime = DateTime.UtcNow
            };

            try
            {
                // Ler conteúdo do arquivo de exemplo
                var inputTxt = await File.ReadAllTextAsync(exampleFilePath);

                // Executar transformação
                var transformationResult = await _pipelineService.TransformTxtToXmlAsync(inputTxt,layoutName,"NFe"); // TODO: Detectar tipo de documento automaticamente

                if (!transformationResult.Success)
                {
                    result.Success = false;
                    result.Errors.AddRange(transformationResult.Errors);
                    result.EndTime = DateTime.UtcNow;
                    result.Duration = result.EndTime - result.StartTime;
                    return result;
                }

                result.TransformedXml = transformationResult.TransformedXml;
                result.TclPath = transformationResult.TclPath;
                result.XslPath = transformationResult.XslPath;

                // Validar transformação
                var validationResult = await _validatorService.ValidateTransformationAsync(inputTxt,layoutName,transformationResult.TclPath,transformationResult.XslPath,expectedXml);

                result.ValidationResult = validationResult;
                result.Success = validationResult.Success && validationResult.ValidationSteps.All(s => s.Success);

                // Comparar com saída esperada se disponível
                if (!string.IsNullOrEmpty(expectedXml))
                {
                    var comparisonResult = await CompareWithExpectedAsync(transformationResult.TransformedXml, expectedXml);
                    result.ComparisonResult = comparisonResult;

                    if (!comparisonResult.Match)
                        result.Warnings.AddRange(comparisonResult.Differences);
                }

                result.EndTime = DateTime.UtcNow;
                result.Duration = result.EndTime - result.StartTime;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao executar caso de teste: {TestCaseName}", result.TestCaseName);
                result.Success = false;
                result.Errors.Add($"Erro: {ex.Message}");
                result.EndTime = DateTime.UtcNow;
                result.Duration = result.EndTime - result.StartTime;
            }

            return result;
        }

        /// <summary>
        /// Compara XML gerado com XML esperado
        /// </summary>
        private async Task<Models.ComparisonResult> CompareWithExpectedAsync(string actualXml, string expectedXml)
        {
            var result = new Models.ComparisonResult
            {
                Match = true,
                Differences = new List<string>()
            };

            try
            {
                var actualDoc = XDocument.Parse(actualXml);
                var expectedDoc = XDocument.Parse(expectedXml);

                // Comparar elementos principais
                var actualElements = actualDoc.Descendants().ToList();
                var expectedElements = expectedDoc.Descendants().ToList();

                if (actualElements.Count != expectedElements.Count)
                {
                    result.Match = false;
                    result.Differences.Add($"Número de elementos diferente: esperado {expectedElements.Count}, encontrado {actualElements.Count}");
                }

                // Comparar estrutura básica
                if (actualDoc.Root?.Name != expectedDoc.Root?.Name)
                {
                    result.Match = false;
                    result.Differences.Add($"Elemento raiz diferente: esperado {expectedDoc.Root?.Name}, encontrado {actualDoc.Root?.Name}");
                }

                // Comparar elementos críticos
                var criticalElements = new[] { "infNFe", "ide", "emit", "dest" };
                foreach (var elementName in criticalElements)
                {
                    var actualElement = actualDoc.Descendants().FirstOrDefault(e => e.Name.LocalName == elementName);
                    var expectedElement = expectedDoc.Descendants().FirstOrDefault(e => e.Name.LocalName == elementName);

                    if (expectedElement != null && actualElement == null)
                    {
                        result.Match = false;
                        result.Differences.Add($"Elemento crítico ausente: {elementName}");
                    }
                }

                result.Message = result.Match ? "XML gerado corresponde ao esperado": $"Encontradas {result.Differences.Count} diferenças";
            }
            catch (Exception ex)
            {
                result.Match = false;
                result.Differences.Add($"Erro ao comparar: {ex.Message}");
                result.Message = "Erro na comparação";
            }

            return result;
        }

        /// <summary>
        /// Busca layout no Redis pelo nome
        /// </summary>
        private async Task<LayoutRecord> FindLayoutByNameAsync(string layoutName)
        {
            try
            {
                var searchRequest = new LayoutSearchRequest
                {
                    SearchTerm = layoutName,
                    MaxResults = 100
                };

                var searchResponse = await _cachedLayoutService.SearchLayoutsAsync(searchRequest);
                return searchResponse?.Layouts?.FirstOrDefault(l => l.Name == layoutName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao buscar layout: {LayoutName}", layoutName);
                return null;
            }
        }
    }
}