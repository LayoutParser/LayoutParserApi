using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Generation;
using LayoutParserApi.Services.Generation.Interfaces;

using System.Text;

namespace LayoutParserApi.Services.Generation.Implementations
{
    public class SyntheticDataGeneratorService : ISyntheticDataGeneratorService
    {
        private readonly ILogger<SyntheticDataGeneratorService> _logger;
        private readonly ITypedValueGenerator _valueGenerator;

        public SyntheticDataGeneratorService(
            ILogger<SyntheticDataGeneratorService> logger,
            ITypedValueGenerator valueGenerator)
        {
            _logger = logger;
            _valueGenerator = valueGenerator;
        }

        public async Task<GeneratedDataResult> GenerateSyntheticDataAsync(SyntheticDataRequest request)
        {
            var result = new GeneratedDataResult
            {
                UsedLayout = request.Layout,
                TotalRecords = request.NumberOfRecords
            };

            var startTime = DateTime.Now;

            try
            {
                _logger.LogInformation("Iniciando geração de {Count} registros sintéticos", request.NumberOfRecords);

                // ✅ Geração por REGRAS, sempre. O caminho de IA saiu em 2026-08-10 com o
                // decommission de Gemini/OpenAI: era o único que mandava layout e amostras de
                // documento para fora. O fallback por regras já existia e cobria a falha do
                // Gemini — agora é o caminho único, não o plano B.
                //
                // `request.UseAI` permanece no contrato e é deliberadamente IGNORADO aqui: remover
                // o campo quebraria o payload de quem já chama o endpoint, e responder 400 a um
                // request que antes funcionava seria pior que atendê-lo por regras. Quando a
                // geração semântica voltar sobre Ollama local, é este flag que a religa.
                //
                // ✅ issue #341 (F2 — ponto de religamento preparado, NÃO ativado aqui): quando esse
                // caminho voltar a chamar um ILlmProvider, se o prompt embutir uma amostra/planilha
                // REAL do analista como referência de formato (ex.: um artefato anexado ao draft com
                // ArtifactProvenance ausente/RealCustomerSample), a chamada deve declarar
                // DataSensitivity.RealFiscalDocument e usar LlmProviderResolver — que recusa em
                // runtime qualquer provider ProviderLocality.Cloud para essa sensibilidade (ver
                // LlmProviderResolver.Resolve). Isso vale MESMO que a SAÍDA final seja sintética: é o
                // conteúdo do PROMPT que importa, não o do resultado. Nunca decida a sensibilidade
                // aqui a partir de "a saída é sintética" — resolva a partir da proveniência real de
                // cada artefato usado como referência (ArtifactProvenance.ResolveSensitivity).
                if (request.UseAI)
                    _logger.LogInformation("UseAI ignorado: geração por IA em nuvem foi decomissionada; usando regras.");

                result = await GenerateWithRulesAsync(request);

                result.GenerationTime = DateTime.Now - startTime;
                result.Success = true;

                _logger.LogInformation("Geração concluída em {Duration}ms", result.GenerationTime.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro na geração de dados sintéticos");
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.GenerationTime = DateTime.Now - startTime;
            }

            return result;
        }

        public async Task<string> GenerateFieldValueAsync(FieldElement field, string context, string dataType, ExcelDataContext excelContext = null)
        {
            try
            {
                // ✅ Geração de valor por tipo agora vive no componente compartilhado
                // ITypedValueGenerator (issue #356) — consumido também pelo caminho Xml,
                // sem duplicar CPF/CNPJ/data/decimal.
                var fieldType = _valueGenerator.InferType(field.Name, dataType);
                return _valueGenerator.Generate(fieldType, field.LengthField, context);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao gerar valor para campo {FieldName}", field.Name);
                return new string(' ', field.LengthField);
            }
        }

        public async Task<List<string>> GenerateMultipleFieldValuesAsync(FieldElement field, int count, ExcelDataContext excelContext = null)
        {
            var values = new List<string>();

            for (int i = 0; i < count; i++)
            {
                var value = await GenerateFieldValueAsync(field, $"Record {i + 1}", null, excelContext);
                values.Add(value);
            }

            return values;
        }

        public async Task<Dictionary<string, object>> AnalyzeFieldGenerationRequirements(FieldElement field, ExcelDataContext excelContext = null)
        {
            var requirements = new Dictionary<string, object>
            {
                ["fieldName"] = field.Name,
                ["fieldType"] = _valueGenerator.InferType(field.Name),
                ["length"] = field.LengthField,
                ["isSequential"] = field.IsSequential,
                ["isRequired"] = field.IsRequired,
                ["alignment"] = field.AlignmentType.ToString()
            };

            if (excelContext != null)
            {
                // Analisar dados do Excel para melhorar geração
                var sampleValues = await ExtractSampleValuesFromExcel(field, excelContext);
                if (sampleValues.Any())
                {
                    requirements["sampleValues"] = sampleValues;
                    requirements["hasExcelData"] = true;
                }
            }

            return requirements;
        }

        private async Task<GeneratedDataResult> GenerateWithRulesAsync(SyntheticDataRequest request)
        {
            var generatedLines = new List<string>();
            var fieldStats = new List<FieldGenerationStats>();

            // Extrair todos os campos do layout
            var allFields = ExtractAllFields(request.Layout);

            for (int recordIndex = 0; recordIndex < request.NumberOfRecords; recordIndex++)
            {
                var lineBuilder = new StringBuilder();

                foreach (var lineElement in request.Layout.Elements)
                {
                    var lineContent = await GenerateLineContent(lineElement, recordIndex, request);
                    lineBuilder.Append(lineContent);
                }

                generatedLines.Add(lineBuilder.ToString());
            }

            return new GeneratedDataResult
            {
                Success = true,
                GeneratedLines = generatedLines,
                TotalRecords = generatedLines.Count,
                UsedLayout = request.Layout,
                FieldStats = fieldStats,
                GenerationMetadata = new Dictionary<string, object>
                {
                    ["generationMethod"] = "Rules",
                    ["totalFields"] = allFields.Count
                }
            };
        }

        private async Task<string> GenerateLineContent(LineElement lineElement, int recordIndex, SyntheticDataRequest request)
        {
            var lineBuilder = new StringBuilder();

            // Adicionar InitialValue se existir
            if (!string.IsNullOrEmpty(lineElement.InitialValue))
                lineBuilder.Append(lineElement.InitialValue);

            // Gerar campos da linha
            if (lineElement.Elements != null)
            {
                foreach (var elementJson in lineElement.Elements)
                {
                    try
                    {
                        var field = Newtonsoft.Json.JsonConvert.DeserializeObject<FieldElement>(elementJson);
                        if (field != null)
                        {
                            var fieldValue = await GenerateFieldValueAsync(field, $"Record {recordIndex}", null, request.ExcelContext);
                            lineBuilder.Append(fieldValue);
                        }
                    }
                    catch { }
                }
            }

            return lineBuilder.ToString();
        }

        private List<FieldElement> ExtractAllFields(Layout layout)
        {
            var fields = new List<FieldElement>();

            foreach (var lineElement in layout.Elements)
                ExtractFieldsFromLineElement(lineElement, fields);

            return fields;
        }

        private void ExtractFieldsFromLineElement(LineElement lineElement, List<FieldElement> fields)
        {
            if (lineElement?.Elements == null) return;

            foreach (var elementJson in lineElement.Elements)
            {
                try
                {
                    var field = Newtonsoft.Json.JsonConvert.DeserializeObject<FieldElement>(elementJson);
                    if (field != null && !string.IsNullOrEmpty(field.Name))
                        fields.Add(field);

                }
                catch { }
            }
        }

        private async Task<List<string>> ExtractSampleValuesFromExcel(FieldElement field, ExcelDataContext excelContext)
        {
            // Implementar lógica para extrair valores de exemplo do Excel
            // baseado no mapeamento de campos
            return new List<string>();
        }

    }
}