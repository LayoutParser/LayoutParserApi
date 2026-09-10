using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Generation;
using LayoutParserApi.Services.Generation.Interfaces;

using System.Text;

namespace LayoutParserApi.Services.Generation.Implementations
{
    public class SyntheticDataGeneratorService : ISyntheticDataGeneratorService
    {
        private readonly ILogger<SyntheticDataGeneratorService> _logger;
        private readonly Random _random = new();

        public SyntheticDataGeneratorService(ILogger<SyntheticDataGeneratorService> logger)
        {
            _logger = logger;
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
                var fieldType = InferFieldType(field, dataType);

                switch (fieldType)
                {
                    case "cnpj":
                        return GenerateCnpj();
                    case "cpf":
                        return GenerateCpf();
                    case "date":
                        return GenerateDate();
                    case "datetime":
                        return GenerateDateTime();
                    case "decimal":
                        return GenerateDecimal(field.LengthField);
                    case "integer":
                        return GenerateInteger(field.LengthField);
                    case "email":
                        return GenerateEmail();
                    case "phone":
                        return GeneratePhone();
                    case "text":
                    default:
                        return GenerateText(field.LengthField, context, excelContext);
                }
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
                ["fieldType"] = InferFieldType(field),
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

        private string InferFieldType(FieldElement field, string dataType = null)
        {
            if (!string.IsNullOrEmpty(dataType))
                return dataType.ToLower();

            var name = field.Name.ToLower();

            if (name.Contains("cnpj")) return "cnpj";
            if (name.Contains("cpf")) return "cpf";
            if (name.Contains("data") || name.Contains("date")) return "date";
            if (name.Contains("hora") || name.Contains("time")) return "datetime";
            if (name.Contains("valor") || name.Contains("preco") || name.Contains("amount")) return "decimal";
            if (name.Contains("quantidade") || name.Contains("qtd")) return "integer";
            if (name.Contains("email")) return "email";
            if (name.Contains("telefone") || name.Contains("phone")) return "phone";

            return "text";
        }

        private string GenerateCnpj()
        {
            // Gera CNPJ sinteticamente válido: 12 dígitos base (8 aleatórios + "0001" de
            // filial matriz) seguidos dos 2 dígitos verificadores calculados por módulo 11.
            var baseDigits = _random.Next(10000000, 99999999).ToString() + "0001";
            var dv1 = CalculateCnpjCheckDigit(baseDigits);
            var dv2 = CalculateCnpjCheckDigit(baseDigits + dv1);
            return baseDigits + dv1 + dv2;
        }

        private string GenerateCpf()
        {
            // Gera CPF sinteticamente válido: 9 dígitos base aleatórios seguidos dos 2
            // dígitos verificadores calculados por módulo 11.
            var baseDigits = _random.Next(100000000, 999999999).ToString().PadLeft(9, '0');
            var dv1 = CalculateCpfCheckDigit(baseDigits);
            var dv2 = CalculateCpfCheckDigit(baseDigits + dv1);
            return baseDigits + dv1 + dv2;
        }

        /// <summary>
        /// Calcula um dígito verificador de CPF pelo algoritmo padrão de módulo 11.
        /// Os pesos começam em (tamanho do trecho + 1) e decrescem até 2.
        /// </summary>
        private static int CalculateCpfCheckDigit(string digits)
        {
            var sum = 0;
            var weight = digits.Length + 1;

            foreach (var c in digits)
            {
                sum += (c - '0') * weight;
                weight--;
            }

            var remainder = sum % 11;
            return remainder < 2 ? 0 : 11 - remainder;
        }

        /// <summary>
        /// Calcula um dígito verificador de CNPJ pelo algoritmo padrão de módulo 11.
        /// Os pesos seguem a sequência fixa 2..9 repetida da direita para a esquerda.
        /// </summary>
        private static int CalculateCnpjCheckDigit(string digits)
        {
            var sum = 0;
            var weight = 2;

            for (var i = digits.Length - 1; i >= 0; i--)
            {
                sum += (digits[i] - '0') * weight;
                weight = weight == 9 ? 2 : weight + 1;
            }

            var remainder = sum % 11;
            return remainder < 2 ? 0 : 11 - remainder;
        }

        private string GenerateDate()
        {
            var startDate = DateTime.Now.AddYears(-5);
            var endDate = DateTime.Now;
            var randomDate = startDate.AddDays(_random.Next(0, (int)(endDate - startDate).TotalDays));
            return randomDate.ToString("yyyyMMdd");
        }

        private string GenerateDateTime()
        {
            var startDate = DateTime.Now.AddYears(-1);
            var endDate = DateTime.Now;
            var randomDate = startDate.AddDays(_random.Next(0, (int)(endDate - startDate).TotalDays));
            return randomDate.ToString("yyyy-MM-ddTHH:mm:ss");
        }

        private string GenerateDecimal(int length)
        {
            var value = (decimal)_random.NextDouble() * 10000;
            var formatted = value.ToString("F2").Replace(".", "").Replace(",", "");
            return formatted.PadLeft(length, '0');
        }

        private string GenerateInteger(int length)
        {
            var value = _random.Next(1, 999999);
            return value.ToString().PadLeft(length, '0');
        }

        private string GenerateEmail()
        {
            var domains = new[] { "gmail.com", "hotmail.com", "outlook.com", "empresa.com.br" };
            var names = new[] { "joao", "maria", "pedro", "ana", "carlos", "lucia" };
            var domain = domains[_random.Next(domains.Length)];
            var name = names[_random.Next(names.Length)];
            return $"{name}{_random.Next(100, 999)}@{domain}";
        }

        private string GeneratePhone()
        {
            var ddd = _random.Next(11, 99);
            var number = _random.Next(10000000, 99999999);
            return $"{ddd}{number}";
        }

        private string GenerateText(int length, string context, ExcelDataContext excelContext)
        {
            var words = new[] { "exemplo", "teste", "dados", "sinteticos", "gerado", "automaticamente" };
            var word = words[_random.Next(words.Length)];
            return word.PadRight(length, ' ');
        }

    }
}