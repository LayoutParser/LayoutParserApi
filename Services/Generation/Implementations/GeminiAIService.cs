using System.Text;
using System.Text.Json;

namespace LayoutParserApi.Services.Generation.Implementations
{
    public class GeminiAIService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<GeminiAIService> _logger;
        private readonly RAGService _ragService;
        private readonly string _apiKey;
        private readonly string _modelName;

        public GeminiAIService(
            HttpClient httpClient,
            IConfiguration configuration,
            ILogger<GeminiAIService> logger,
            RAGService ragService)
        {
            _httpClient = httpClient;
            _logger = logger;
            _ragService = ragService;
            _apiKey = configuration["Gemini:ApiKey"] ?? "AIzaSyDwNhK9Hc1nie9lmHJrfLmKBIJzHWNkzD8";
            _modelName = configuration["Gemini:Model"] ?? "gemini-1.5-flash";
            
            _logger.LogInformation("GeminiAIService configurado - Model: {Model}, RAG: {RAGStatus}", 
                _modelName, _ragService != null ? "Ativo" : "Inativo");
        }

        /// <summary>
        /// Gera dados sintéticos baseados no layout e exemplos
        /// </summary>
        public async Task<string> GenerateSyntheticData(
            string layoutXml,
            List<string> examples,
            Dictionary<string, string> excelRules,
            int recordCount = 1)
        {
            try
            {
                if (string.IsNullOrEmpty(_apiKey))
                {
                    throw new Exception("Chave da API do Gemini não configurada");
                }

                // Montar prompt com contexto
                var prompt = BuildPromptWithContext(layoutXml, examples, excelRules, recordCount);
                _logger.LogInformation("Enviando prompt para Gemini (tamanho: {Size} caracteres)", prompt.Length);

                // Chamar Gemini API
                var response = await CallGeminiAPI(prompt);

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao gerar dados sintéticos com Gemini");
                throw;
            }
        }

        /// <summary>
        /// Constrói prompt com contexto usando RAG
        /// </summary>
        private string BuildPromptWithContext(
            string layoutXml,
            List<string> examples,
            Dictionary<string, string> excelRules,
            int recordCount)
        {
            var sb = new StringBuilder();

            // Extrair apenas informações essenciais do layout XML
            var essentialInfo = ExtractEssentialLayoutInfo(layoutXml);
            
            sb.AppendLine($"Generate {recordCount} synthetic data records:");
            sb.AppendLine();
            sb.AppendLine(essentialInfo);
            sb.AppendLine();
            
            // Usar RAG para encontrar exemplos relevantes
            var ragExamples = _ragService?.FindRelevantExamples(layoutXml, 3) ?? new List<string>();
            
            // Combinar exemplos do RAG com exemplos fornecidos
            var allExamples = new List<string>();
            if (ragExamples.Any())
            {
                allExamples.AddRange(ragExamples);
                _logger.LogInformation("Usando {Count} exemplos do RAG", ragExamples.Count);
            }
            
            if (examples != null && examples.Any())
            {
                allExamples.AddRange(examples.Take(2));
                _logger.LogInformation("Usando {Count} exemplos fornecidos", examples.Count);
            }
            
            if (allExamples.Any())
            {
                sb.AppendLine("Relevant Examples:");
                foreach (var example in allExamples.Take(3))
                {
                    sb.AppendLine(example);
                }
                sb.AppendLine();
            }

            sb.AppendLine("Requirements:");
            sb.AppendLine("- Follow exact XML layout positions and sizes");
            sb.AppendLine("- Generate realistic data based on the examples");
            sb.AppendLine("- Return only the data records, one per line");
            sb.AppendLine("- No explanations or comments");
            sb.AppendLine("- Maintain data consistency with examples");

            return sb.ToString();
        }

        private string ExtractEssentialLayoutInfo(string layoutXml)
        {
            try
            {
                // Extrair apenas os nomes das linhas e campos principais
                var lines = new List<string>();
                
                // Buscar por padrões de LineElement
                var lineMatches = System.Text.RegularExpressions.Regex.Matches(layoutXml, @"<Name>([^<]+)</Name>");
                foreach (System.Text.RegularExpressions.Match match in lineMatches)
                {
                    if (lines.Count < 5) // Limitar a 5 linhas principais
                    {
                        lines.Add($"Line: {match.Groups[1].Value}");
                    }
                }
                
                return string.Join("\n", lines);
            }
            catch
            {
                return "Layout structure detected";
            }
        }

        /// <summary>
        /// Chama API do Gemini
        /// </summary>
        private async Task<string> CallGeminiAPI(string prompt)
        {
            var request = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new[]
                        {
                            new
                            {
                                text = prompt
                            }
                        }
                    }
                },
                generationConfig = new
                {
                    temperature = 0.7,
                    topK = 40,
                    topP = 0.95,
                    maxOutputTokens = 1000
                }
            };

            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(
                $"https://generativelanguage.googleapis.com/v1beta/models/{_modelName}:generateContent?key={_apiKey}",
                content);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                throw new Exception($"Erro na API Gemini: {response.StatusCode} - {error}");
            }

            var result = await response.Content.ReadFromJsonAsync<GeminiResponse>();
            
            if (result?.candidates?.FirstOrDefault()?.content?.parts?.FirstOrDefault()?.text == null)
            {
                throw new Exception("Resposta inválida da API Gemini");
            }

            return result.candidates.First().content.parts.First().text;
        }
    }

    // Classes para deserialização da resposta do Gemini
    public class GeminiResponse
    {
        public GeminiCandidate[] candidates { get; set; } = Array.Empty<GeminiCandidate>();
    }

    public class GeminiCandidate
    {
        public GeminiContent content { get; set; } = new();
    }

    public class GeminiContent
    {
        public GeminiPart[] parts { get; set; } = Array.Empty<GeminiPart>();
    }

    public class GeminiPart
    {
        public string text { get; set; } = "";
    }
}
