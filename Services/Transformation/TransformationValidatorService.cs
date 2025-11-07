using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LayoutParserApi.Services.Transformation
{
    /// <summary>
    /// Serviço para validar transformações TCL e XSL
    /// Verifica se a transformação está gerando o resultado esperado
    /// </summary>
    public class TransformationValidatorService
    {
        private readonly ILogger<TransformationValidatorService> _logger;
        private readonly IConfiguration _configuration;
        private readonly TransformationPipelineService _pipelineService;
        private readonly string _expectedOutputsPath;

        public TransformationValidatorService(
            ILogger<TransformationValidatorService> logger,
            IConfiguration configuration,
            TransformationPipelineService pipelineService)
        {
            _logger = logger;
            _configuration = configuration;
            _pipelineService = pipelineService;
            _expectedOutputsPath = configuration["TransformationPipeline:ExpectedOutputsPath"] 
                ?? @"C:\inetpub\wwwroot\layoutparser\ExpectedOutputs";

            Directory.CreateDirectory(_expectedOutputsPath);
        }

        /// <summary>
        /// Valida transformação completa (TXT -> TCL -> XSL -> XML)
        /// </summary>
        public async Task<TransformationValidationResult> ValidateTransformationAsync(
            string inputTxt,
            string layoutName,
            string tclPath,
            string xslPath,
            string expectedOutputXml = null)
        {
            var result = new TransformationValidationResult
            {
                Success = true,
                ValidationSteps = new List<ValidationStep>(),
                Errors = new List<string>(),
                Warnings = new List<string>()
            };

            try
            {
                _logger.LogInformation("Iniciando validação de transformação para layout: {LayoutName}", layoutName);

                // Passo 1: Validar TCL (se fornecido)
                if (!string.IsNullOrEmpty(tclPath) && File.Exists(tclPath))
                {
                    var tclValidation = await ValidateTclAsync(tclPath, inputTxt);
                    result.ValidationSteps.Add(new ValidationStep
                    {
                        Step = "TCL Validation",
                        Success = tclValidation.Success,
                        Message = tclValidation.Message,
                        Details = tclValidation.Details
                    });

                    if (!tclValidation.Success)
                    {
                        result.Errors.AddRange(tclValidation.Errors);
                        result.Success = false;
                    }
                }

                // Passo 2: Executar transformação completa
                var transformationResult = await _pipelineService.TransformTxtToXmlAsync(
                    inputTxt,
                    layoutName,
                    "NFe"); // TODO: Detectar tipo de documento automaticamente

                if (transformationResult.Success)
                {
                    result.TransformedXml = transformationResult.TransformedXml;
                    result.ValidationSteps.Add(new ValidationStep
                    {
                        Step = "Transformation Execution",
                        Success = true,
                        Message = "Transformação executada com sucesso"
                    });
                }
                else
                {
                    result.Errors.AddRange(transformationResult.Errors);
                    result.Success = false;
                    return result;
                }

                // Passo 3: Validar estrutura XML resultante
                var xmlValidation = await ValidateXmlStructureAsync(transformationResult.TransformedXml);
                result.ValidationSteps.Add(new ValidationStep
                {
                    Step = "XML Structure Validation",
                    Success = xmlValidation.Success,
                    Message = xmlValidation.Message,
                    Details = xmlValidation.Details
                });

                if (!xmlValidation.Success)
                {
                    result.Errors.AddRange(xmlValidation.Errors);
                    result.Success = false;
                }

                // Passo 4: Comparar com saída esperada (se fornecida)
                if (!string.IsNullOrEmpty(expectedOutputXml))
                {
                    var comparisonResult = await CompareWithExpectedAsync(
                        transformationResult.TransformedXml,
                        expectedOutputXml);
                    
                    result.ValidationSteps.Add(new ValidationStep
                    {
                        Step = "Expected Output Comparison",
                        Success = comparisonResult.Match,
                        Message = comparisonResult.Message,
                        Details = comparisonResult.Differences
                    });

                    if (!comparisonResult.Match)
                    {
                        result.Warnings.AddRange(comparisonResult.Differences);
                    }
                }
                else
                {
                    // Tentar carregar saída esperada do diretório
                    var expectedPath = Path.Combine(_expectedOutputsPath, $"{layoutName}_expected.xml");
                    if (File.Exists(expectedPath))
                    {
                        var expectedXml = await File.ReadAllTextAsync(expectedPath);
                        var comparisonResult = await CompareWithExpectedAsync(
                            transformationResult.TransformedXml,
                            expectedXml);
                        
                        result.ValidationSteps.Add(new ValidationStep
                        {
                            Step = "Expected Output Comparison (from file)",
                            Success = comparisonResult.Match,
                            Message = comparisonResult.Message,
                            Details = comparisonResult.Differences
                        });

                        if (!comparisonResult.Match)
                        {
                            result.Warnings.AddRange(comparisonResult.Differences);
                        }
                    }
                }

                _logger.LogInformation("Validação concluída. Sucesso: {Success}", result.Success);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro durante validação de transformação");
                result.Success = false;
                result.Errors.Add($"Erro: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Valida estrutura do TCL
        /// </summary>
        private async Task<ValidationResult> ValidateTclAsync(string tclPath, string inputTxt)
        {
            var result = new ValidationResult
            {
                Success = true,
                Errors = new List<string>()
            };

            try
            {
                var tclContent = await File.ReadAllTextAsync(tclPath);

                // Verificar se o TCL é XML válido
                try
                {
                    var tclDoc = XDocument.Parse(tclContent);
                    result.Message = "TCL é um XML válido";
                    result.Details = $"Root element: {tclDoc.Root?.Name.LocalName}";
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Errors.Add($"TCL não é um XML válido: {ex.Message}");
                    result.Message = "TCL inválido";
                }

                // Verificar estrutura básica do TCL (MAP, LINE, FIELD)
                // TODO: Implementar validação mais detalhada
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"Erro ao validar TCL: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Valida estrutura do XML resultante
        /// </summary>
        private async Task<ValidationResult> ValidateXmlStructureAsync(string xmlContent)
        {
            var result = new ValidationResult
            {
                Success = true,
                Errors = new List<string>()
            };

            try
            {
                var xmlDoc = XDocument.Parse(xmlContent);
                
                result.Message = "XML válido estruturalmente";
                result.Details = $"Root element: {xmlDoc.Root?.Name.LocalName}, Elements: {xmlDoc.Descendants().Count()}";

                // Validar elementos obrigatórios (ex: NFe)
                if (xmlDoc.Root?.Name.LocalName != "NFe")
                {
                    result.Warnings.Add($"Elemento raiz esperado: NFe, encontrado: {xmlDoc.Root?.Name.LocalName}");
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"XML inválido: {ex.Message}");
                result.Message = "XML inválido";
            }

            return result;
        }

        /// <summary>
        /// Compara XML gerado com XML esperado
        /// </summary>
        private async Task<ComparisonResult> CompareWithExpectedAsync(string actualXml, string expectedXml)
        {
            var result = new ComparisonResult
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
                    result.Differences.Add(
                        $"Número de elementos diferente: esperado {expectedElements.Count}, encontrado {actualElements.Count}");
                }

                // Comparar estrutura básica
                if (actualDoc.Root?.Name != expectedDoc.Root?.Name)
                {
                    result.Match = false;
                    result.Differences.Add(
                        $"Elemento raiz diferente: esperado {expectedDoc.Root?.Name}, encontrado {actualDoc.Root?.Name}");
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

                result.Message = result.Match 
                    ? "XML gerado corresponde ao esperado" 
                    : $"Encontradas {result.Differences.Count} diferenças";
            }
            catch (Exception ex)
            {
                result.Match = false;
                result.Differences.Add($"Erro ao comparar: {ex.Message}");
                result.Message = "Erro na comparação";
            }

            return result;
        }
    }

    // Modelos de resultado
    public class TransformationValidationResult
    {
        public bool Success { get; set; }
        public string TransformedXml { get; set; }
        public List<ValidationStep> ValidationSteps { get; set; } = new();
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }

    public class ValidationStep
    {
        public string Step { get; set; }
        public bool Success { get; set; }
        public string Message { get; set; }
        public string Details { get; set; }
    }

    public class ValidationResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string Details { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }

    public class ComparisonResult
    {
        public bool Match { get; set; }
        public string Message { get; set; }
        public List<string> Differences { get; set; } = new();
    }
}

