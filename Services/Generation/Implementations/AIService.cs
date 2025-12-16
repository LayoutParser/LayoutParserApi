using LayoutParserApi.Models;
using LayoutParserApi.Models.Analysis;
using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Generation;
using LayoutParserApi.Services.Generation.Interfaces;
using System.Text;
using System.Text.Json;

namespace LayoutParserApi.Services.Generation.Implementations
{
    public class AIService : IAIService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly ILogger<AIService> _logger;
        private readonly string _apiKey;
        private readonly string _apiUrl;

        public AIService(HttpClient httpClient, IConfiguration configuration, ILogger<AIService> logger)
        {
            _httpClient = httpClient;
            _configuration = configuration;
            _logger = logger;
            _apiKey = _configuration["OpenAI:ApiKey"] ?? "";
            _apiUrl = _configuration["OpenAI:ApiUrl"] ?? "https://api.openai.com/v1/chat/completions";
        }

        public async Task<string> GenerateSyntheticDataAsync(string prompt, int maxTokens = 2000)
        {
            try
            {
                _logger.LogInformation("Enviando prompt para IA: {Length} caracteres", prompt.Length);

                var request = new
                {
                    model = "gpt-3.5-turbo",
                    messages = new[]
                    {
                        new { role = "system", content = "Você é um especialista em geração de dados sintéticos para sistemas legados e integrações SAP." },
                        new { role = "user", content = prompt }
                    },
                    max_tokens = maxTokens,
                    temperature = 0.7
                };

                var json = JsonSerializer.Serialize(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);

                var response = await _httpClient.PostAsync(_apiUrl, content);
                response.EnsureSuccessStatusCode();

                var responseContent = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<OpenAIResponse>(responseContent);

                _logger.LogInformation("Resposta da IA recebida: {Length} caracteres", result.choices[0].message.content.Length);
                return result.choices[0].message.content;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao chamar serviço de IA");
                throw new InvalidOperationException($"Erro na geração de dados com IA: {ex.Message}", ex);
            }
        }

        public async Task<FieldPatternAnalysis> AnalyzeFieldPatternsAsync(string fieldName, List<string> sampleValues)
        {
            try
            {
                var prompt = BuildPatternAnalysisPrompt(fieldName, sampleValues);
                var aiResponse = await GenerateSyntheticDataAsync(prompt, 500);
                
                return ParsePatternAnalysisResponse(aiResponse, fieldName, sampleValues);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao analisar padrões do campo {FieldName}", fieldName);
                
                // Fallback para análise local
                return new FieldPatternAnalysis
                {
                    FieldName = fieldName,
                    DetectedPattern = "unknown",
                    SuggestedGenerationStrategy = "generate_random_text",
                    CommonValues = sampleValues.Take(5).ToList(),
                    ValueFrequency = sampleValues.GroupBy(v => v).ToDictionary(g => g.Key, g => g.Count())
                };
            }
        }

        public async Task<List<FieldMapping>> SuggestFieldMappingsAsync(Layout layout, ExcelDataContext excelData)
        {
            try
            {
                var prompt = BuildFieldMappingPrompt(layout, excelData);
                var aiResponse = await GenerateSyntheticDataAsync(prompt, 1000);
                
                return ParseFieldMappingResponse(aiResponse, layout, excelData);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao sugerir mapeamentos de campos");
                return new List<FieldMapping>();
            }
        }

        public async Task<string> GenerateFieldValueAsync(string fieldName, string dataType, int length, string context, List<string> sampleValues = null)
        {
            try
            {
                var prompt = BuildFieldValuePrompt(fieldName, dataType, length, context, sampleValues);
                var aiResponse = await GenerateSyntheticDataAsync(prompt, 100);
                
                return FormatFieldValue(aiResponse, length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao gerar valor para campo {FieldName}", fieldName);
                return new string(' ', length);
            }
        }

        public async Task<Dictionary<string, object>> AnalyzeDataPatternsAsync(ExcelDataContext excelData)
        {
            try
            {
                var prompt = BuildDataPatternAnalysisPrompt(excelData);
                var aiResponse = await GenerateSyntheticDataAsync(prompt, 800);
                
                return ParseDataPatternAnalysisResponse(aiResponse);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao analisar padrões dos dados do Excel");
                return new Dictionary<string, object>();
            }
        }

        private string BuildPatternAnalysisPrompt(string fieldName, List<string> sampleValues)
        {
            var prompt = new StringBuilder();
            
            prompt.AppendLine($"Analise os padrões do campo '{fieldName}' com base nos valores de exemplo:");
            prompt.AppendLine();
            
            foreach (var value in sampleValues.Take(10))
                prompt.AppendLine($"- {value}");
            
            prompt.AppendLine();
            prompt.AppendLine("Identifique:");
            prompt.AppendLine("1. Tipo de dados (texto, numérico, data, email, etc.)");
            prompt.AppendLine("2. Padrão de formatação");
            prompt.AppendLine("3. Estratégia de geração recomendada");
            prompt.AppendLine("4. Valores mais comuns");
            
            return prompt.ToString();
        }

        private string BuildFieldMappingPrompt(Layout layout, ExcelDataContext excelData)
        {
            var prompt = new StringBuilder();
            
            prompt.AppendLine("Mapeie as colunas do Excel para os campos do layout:");
            prompt.AppendLine();
            
            prompt.AppendLine("Campos do Layout:");
            foreach (var lineElement in layout.Elements)
                prompt.AppendLine($"- {lineElement.Name}: {lineElement.InitialValue}");
            
            prompt.AppendLine();
            prompt.AppendLine("Colunas do Excel:");
            foreach (var header in excelData.Headers)
                prompt.AppendLine($"- {header}");
            
            
            prompt.AppendLine();
            prompt.AppendLine("Sugira mapeamentos baseados na semelhança de nomes e tipos de dados.");
            
            return prompt.ToString();
        }

        private string BuildFieldValuePrompt(string fieldName, string dataType, int length, string context, List<string> sampleValues)
        {
            var prompt = new StringBuilder();
            
            prompt.AppendLine($"Gere um valor para o campo '{fieldName}':");
            prompt.AppendLine($"- Tipo: {dataType}");
            prompt.AppendLine($"- Tamanho: {length} caracteres");
            prompt.AppendLine($"- Contexto: {context}");
            
            if (sampleValues?.Any() == true)
            {
                prompt.AppendLine("Valores de exemplo:");
                foreach (var value in sampleValues.Take(3))
                    prompt.AppendLine($"- {value}");
            }
            
            prompt.AppendLine();
            prompt.AppendLine("Resposta: Apenas o valor gerado, sem explicações.");
            
            return prompt.ToString();
        }

        private string BuildDataPatternAnalysisPrompt(ExcelDataContext excelData)
        {
            var prompt = new StringBuilder();
            
            prompt.AppendLine("Analise os padrões dos dados do Excel:");
            prompt.AppendLine();
            
            foreach (var header in excelData.Headers.Take(10))
            {
                prompt.AppendLine($"Coluna: {header}");
                if (excelData.ColumnData.ContainsKey(header))
                {
                    var samples = excelData.ColumnData[header].Take(3);
                    foreach (var sample in samples)
                        prompt.AppendLine($"- {sample}");
                }
                prompt.AppendLine();
            }
            
            prompt.AppendLine("Identifique padrões, distribuições e características dos dados para melhorar a geração sintética.");
            
            return prompt.ToString();
        }

        private FieldPatternAnalysis ParsePatternAnalysisResponse(string aiResponse, string fieldName, List<string> sampleValues)
        {
            var analysis = new FieldPatternAnalysis
            {
                FieldName = fieldName,
                CommonValues = sampleValues.Take(5).ToList(),
                ValueFrequency = sampleValues.GroupBy(v => v).ToDictionary(g => g.Key, g => g.Count())
            };

            // Parse simples da resposta da IA
            if (aiResponse.Contains("numérico") || aiResponse.Contains("numeric"))
                analysis.DetectedPattern = "numeric";
            else if (aiResponse.Contains("data") || aiResponse.Contains("date"))
                analysis.DetectedPattern = "date";
            else if (aiResponse.Contains("email"))
                analysis.DetectedPattern = "email";
            else
                analysis.DetectedPattern = "text";

            analysis.SuggestedGenerationStrategy = "generate_" + analysis.DetectedPattern;

            return analysis;
        }

        private List<FieldMapping> ParseFieldMappingResponse(string aiResponse, Layout layout, ExcelDataContext excelData)
        {
            var mappings = new List<FieldMapping>();
            
            // Parse simples da resposta da IA
            // Em produção, usar parsing mais robusto ou structured output
            
            return mappings;
        }

        private string FormatFieldValue(string aiResponse, int length)
        {
            var value = aiResponse.Trim().Replace("\"", "");
            
            if (value.Length > length)
                value = value.Substring(0, length);
            else if (value.Length < length)
                value = value.PadRight(length, ' ');
            
            return value;
        }

        private Dictionary<string, object> ParseDataPatternAnalysisResponse(string aiResponse)
        {
            var patterns = new Dictionary<string, object>
            {
                ["ai_analysis"] = aiResponse,
                ["analysis_timestamp"] = DateTime.UtcNow
            };

            return patterns;
        }
    }


}
