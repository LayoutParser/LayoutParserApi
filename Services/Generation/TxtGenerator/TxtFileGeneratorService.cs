using LayoutParserApi.Models.Generation;
using LayoutParserApi.Services.Generation.TxtGenerator.Enum;
using LayoutParserApi.Services.Generation.TxtGenerator.Generators;
using LayoutParserApi.Services.Generation.TxtGenerator.Generators.Interfaces;
using LayoutParserApi.Services.Generation.TxtGenerator.Models;
using LayoutParserApi.Services.Generation.TxtGenerator.Parsers;
using LayoutParserApi.Services.Generation.TxtGenerator.Validators;

using System.Text;

namespace LayoutParserApi.Services.Generation.TxtGenerator
{
    /// <summary>
    /// Serviço principal para geração de arquivos .txt de teste
    /// </summary>
    public class TxtFileGeneratorService
    {
        private readonly ILogger<TxtFileGeneratorService> _logger;
        private readonly XmlLayoutParser _xmlParser;
        private readonly ExcelRulesParser _excelParser;
        private readonly LayoutValidator _validator;
        private readonly IFieldValueGenerator _generator;
        private readonly IAsyncFieldValueGenerator _asyncGenerator;

        public TxtFileGeneratorService(
            ILogger<TxtFileGeneratorService> logger,
            XmlLayoutParser xmlParser,
            ExcelRulesParser excelParser,
            LayoutValidator validator,
            IServiceProvider serviceProvider,
            GenerationMode mode = GenerationMode.Random)
        {
            _logger = logger;
            _xmlParser = xmlParser;
            _excelParser = excelParser;
            _validator = validator;

            // Escolher gerador baseado no modo
            switch (mode)
            {
                case GenerationMode.Deterministic:
                    _generator = serviceProvider.GetService<DeterministicGenerator>();
                    _asyncGenerator = null;
                    break;
                case GenerationMode.Random:
                    _generator = serviceProvider.GetService<RandomGenerator>();
                    _asyncGenerator = null;
                    break;
                case GenerationMode.SemanticAI:
                    _asyncGenerator = serviceProvider.GetService<SemanticAIGenerator>();
                    _generator = _asyncGenerator;
                    break;
                default:
                    _generator = serviceProvider.GetService<RandomGenerator>();
                    _asyncGenerator = null;
                    break;
            }
        }

        /// <summary>
        /// Gera arquivo .txt completo
        /// </summary>
        public async Task<GenerationResult> GenerateFileAsync(string layoutXml,ExcelDataContext excelContext,int recordCount,string outputPath,GenerationMode mode)
        {
            try
            {
                _logger.LogInformation("Iniciando geração de arquivo .txt - Modo: {Mode}, Registros: {Count}", mode, recordCount);

                // 1. Parse do layout XML
                var fileLayout = _xmlParser.ParseLayout(layoutXml);
                _logger.LogInformation("Layout parseado: {RecordCount} tipos de registro", fileLayout.Records.Count);

                // 2. Aplicar regras do Excel
                if (excelContext != null)
                {
                    _excelParser.ApplyExcelRules(fileLayout, excelContext);
                    _logger.LogInformation("Regras do Excel aplicadas");
                }

                // 3. Gerar linhas
                var lines = new List<string>();
                for (int recordIndex = 0; recordIndex < recordCount; recordIndex++)
                {
                    var recordLines = await GenerateRecordsAsync(fileLayout, recordIndex, excelContext);
                    lines.AddRange(recordLines);
                    _logger.LogDebug("Registro {Index} gerado: {LineCount} linhas", recordIndex + 1, recordLines.Count);
                }

                // 4. Validar arquivo gerado
                var validation = _validator.ValidateFile(lines, fileLayout);
                if (!validation.IsValid)
                {
                    _logger.LogWarning("Arquivo gerado com {ErrorCount} erros e {WarningCount} avisos",
                        validation.Errors.Count, validation.Warnings.Count);

                    foreach (var error in validation.Errors)
                        _logger.LogWarning("Erro de validação: {Error}", error);
                }
                else
                    _logger.LogInformation("Arquivo validado com sucesso: {ValidLines} linhas válidas", validation.ValidLines);

                // 5. Salvar arquivo
                var fileName = $"generated_{DateTime.Now:yyyyMMddHHmmss}.txt";
                var fullPath = Path.Combine(outputPath, fileName);

                Directory.CreateDirectory(outputPath);
                await File.WriteAllLinesAsync(fullPath, lines, Encoding.UTF8);

                _logger.LogInformation("Arquivo gerado com sucesso: {Path}", fullPath);

                return new GenerationResult
                {
                    Success = true,
                    FilePath = fullPath,
                    LineCount = lines.Count,
                    ValidationResult = validation
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao gerar arquivo .txt");
                return new GenerationResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        private async Task<List<string>> GenerateRecordsAsync(FileLayout fileLayout, int recordIndex, ExcelDataContext excelContext)
        {
            var lines = new List<string>();

            foreach (var recordLayout in fileLayout.Records)
            {
                var occurrences = recordLayout.MaximumOccurrence;
                for (int occurrence = 0; occurrence < occurrences; occurrence++)
                {
                    var line = await GenerateLineAsync(recordLayout, recordIndex, occurrence, excelContext);
                    lines.Add(line);
                }
            }

            return lines;
        }

        private async Task<string> GenerateLineAsync(RecordLayout recordLayout, int recordIndex, int occurrence, ExcelDataContext excelContext)
        {
            var lineBuilder = new StringBuilder();

            // Adicionar InitialValue
            if (!string.IsNullOrEmpty(recordLayout.InitialValue))
                lineBuilder.Append(recordLayout.InitialValue);

            // Gerar cada campo
            var context = new Dictionary<string, object>();
            if (excelContext != null)
            {
                // Adicionar exemplos do Excel ao contexto
                foreach (var field in recordLayout.Fields)
                {
                    var samples = GetExcelSamples(field.Name, excelContext);
                    if (samples.Any())
                        context[$"ExcelSamples_{field.Name}"] = samples;
                }
            }

            foreach (var field in recordLayout.Fields.OrderBy(f => f.Sequence))
            {
                // Preparar contexto específico do campo
                var fieldContext = new Dictionary<string, object>(context);
                if (context.ContainsKey($"ExcelSamples_{field.Name}"))
                    fieldContext["ExcelSamples"] = context[$"ExcelSamples_{field.Name}"];

                string fieldValue;
                if (_asyncGenerator != null && _generator is IAsyncFieldValueGenerator)
                    fieldValue = await ((IAsyncFieldValueGenerator)_generator).GenerateValueAsync(field, recordIndex, fieldContext);
                else
                    fieldValue = _generator.GenerateValue(field, recordIndex, fieldContext);

                // Ajustar posição atual se necessário
                var currentPosition = lineBuilder.Length;
                if (currentPosition < field.StartPosition)
                {
                    // Preencher espaços até a posição inicial do campo
                    var padding = field.StartPosition - currentPosition;
                    lineBuilder.Append(new string(' ', padding));
                }

                lineBuilder.Append(fieldValue);
                _logger.LogDebug("Campo {FieldName} gerado com valor: {Value} (posição {Start}-{End})",field.Name, fieldValue.Trim(), field.StartPosition, field.EndPosition);
            }

            // Completar até o tamanho total
            var line = lineBuilder.ToString();
            if (line.Length < recordLayout.TotalLength)
                line = line.PadRight(recordLayout.TotalLength, ' ');
            else if (line.Length > recordLayout.TotalLength)
            {
                line = line.Substring(0, recordLayout.TotalLength);
                _logger.LogWarning("Linha truncada de {Original} para {Target} caracteres",lineBuilder.Length, recordLayout.TotalLength);
            }

            return line;
        }

        private List<string> GetExcelSamples(string fieldName, ExcelDataContext excelContext)
        {
            if (excelContext?.ColumnData == null)
                return new List<string>();

            // Buscar coluna correspondente
            var matchingColumn = excelContext.Headers?.FirstOrDefault(h => h.Equals(fieldName, StringComparison.OrdinalIgnoreCase) || h.ToLowerInvariant().Contains(fieldName.ToLowerInvariant()));

            if (matchingColumn == null || !excelContext.ColumnData.ContainsKey(matchingColumn))
                return new List<string>();

            return excelContext.ColumnData[matchingColumn].Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().Take(10).ToList();
        }
    }
}