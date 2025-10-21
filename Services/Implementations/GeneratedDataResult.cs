using LayoutParserApi.Models.Entities;
using LayoutParserApi.Services.Interfaces;

using System.Text;
using System.Text.Json;

namespace LayoutParserApi.Services.Implementations
{
    public class GeneratedDataResult
    {
        public bool Success { get; set; }
        public List<string> GeneratedLines { get; set; } = new();
        public string ErrorMessage { get; set; }
        public TimeSpan GenerationTime { get; set; }
        public int TotalRecords { get; set; }
    }

    public class OpenAIDataGeneratorService : IIADataGeneratorService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly ILogger<OpenAIDataGeneratorService> _logger;

        public OpenAIDataGeneratorService(HttpClient httpClient, IConfiguration config, ILogger<OpenAIDataGeneratorService> logger)
        {
            _httpClient = httpClient;
            _apiKey = config["OpenAI:ApiKey"];
            _logger = logger;
        }

        public async Task<GeneratedDataResult> GenerateSyntheticDataAsync(Layout layout, int numberOfRecords, string sampleRealData = null)
        {
            var result = new GeneratedDataResult();
            var startTime = DateTime.Now;

            try
            {
                var layoutAnalysis = AnalyzeLayoutForGeneration(layout);

                var prompt = BuildGenerationPrompt(layoutAnalysis, numberOfRecords, sampleRealData);

                var generatedContent = await CallOpenAIAsync(prompt);

                result.GeneratedLines = ParseGeneratedContent(generatedContent, layout);
                result.Success = true;
                result.TotalRecords = result.GeneratedLines.Count;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                _logger.LogError(ex, "Erro na geração de dados com IA");
            }

            result.GenerationTime = DateTime.Now - startTime;
            return result;
        }

        public async Task<string> GenerateFieldValueAsync(FieldElement field, string context, string dataType)
        {
            var prompt = $"""
                Gere um valor sintético para o campo: {field.Name}
                Tipo: {dataType}
                Tamanho: {field.LengthField} caracteres
                Contexto: {context}
                Regras: {field.Description}
                Formato: Apenas o valor, sem explicações
                Exemplo de valor real: {GetExampleForDataType(dataType)}
                """;

            var response = await CallOpenAIAsync(prompt, maxTokens: 50);
            return response.Trim().Replace("\"", "").PadRight(field.LengthField).Substring(0, field.LengthField);
        }

        private LayoutAnalysis AnalyzeLayoutForGeneration(Layout layout)
        {
            var analysis = new LayoutAnalysis
            {
                LayoutName = layout.Name,
                TotalFields = layout.Elements.Sum(e => e.Elements.Count),
                LineTypes = new List<LineTypeAnalysis>()
            };

            foreach (var line in layout.Elements)
            {
                var lineAnalysis = new LineTypeAnalysis
                {
                    Name = line.Name,
                    Identifier = line.InitialValue,
                    Fields = line.Elements.Select(f => new FieldAnalysis
                    {
                        Name = f,
                        Length = 0,
                        IsRequired = false,
                        Description = "",
                        StartPosition = 0
                    }).ToList()
                };
                analysis.LineTypes.Add(lineAnalysis);
            }

            return analysis;
        }

        private string BuildGenerationPrompt(LayoutAnalysis analysis, int numberOfRecords, string sampleData)
        {
            var prompt = new StringBuilder();

            prompt.AppendLine($"Gere {numberOfRecords} registros sintéticos baseados no layout abaixo.");
            prompt.AppendLine($"Layout: {analysis.LayoutName}");
            prompt.AppendLine($"Formato: TXT Posicional");
            prompt.AppendLine();

            if (!string.IsNullOrEmpty(sampleData))
            {
                prompt.AppendLine($"Dados reais de exemplo:");
                prompt.AppendLine($"```");
                prompt.AppendLine(sampleData);
                prompt.AppendLine($"```");
                prompt.AppendLine();
            }

            prompt.AppendLine($"Estrutura do Layout:");
            foreach (var lineType in analysis.LineTypes)
            {
                prompt.AppendLine($"- {lineType.Name}: {lineType.Identifier}");
                foreach (var field in lineType.Fields)
                {
                    prompt.AppendLine($"  * {field.Name}: {field.DataType} ({field.Length} chars) - {field.Description}");
                }
            }

            prompt.AppendLine();
            prompt.AppendLine($"Regras:");
            prompt.AppendLine($"- Manter formato posicional exato");
            prompt.AppendLine($"- Campos numéricos: preencher com zeros à esquerda");
            prompt.AppendLine($"- Campos texto: alinhar à esquerda, completar com espaços");
            prompt.AppendLine($"- Sequências: incrementar automaticamente");
            prompt.AppendLine($"- CNPJ/CPF: gerar números válidos sinteticamente");
            prompt.AppendLine($"- Datas: usar formato AAAAMMDD");
            prompt.AppendLine($"- Valores monetários: formato sem separadores");
            prompt.AppendLine();
            prompt.AppendLine($"Resposta: Apenas os dados gerados, um registro por linha, sem marcações.");

            return prompt.ToString();
        }

        private async Task<string> CallOpenAIAsync(string prompt, int maxTokens = 2000)
        {
            var request = new
            {
                model = "gpt-3.5-turbo",
                messages = new[]
                {
                    new { role = "system", content = "Você é um especialista em geração de dados sintéticos para sistemas legados." },
                    new { role = "user", content = prompt }
                },
                max_tokens = maxTokens,
                temperature = 0.7
            };

            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);

            var response = await _httpClient.PostAsync("https://api.openai.com/v1/chat/completions", content);
            response.EnsureSuccessStatusCode();

            var responseContent = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<OpenAIResponse>(responseContent);

            return result.choices[0].message.content;
        }

        private List<string> ParseGeneratedContent(string content, Layout layout)
        {
            var lines = content.Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => line.Trim()).ToList();

            var formattedLines = new List<string>();
            foreach (var line in lines)
            {
                formattedLines.Add(FormatLineForLayout(line, layout));
            }

            return formattedLines;
        }

        private string FormatLineForLayout(string line, Layout layout)
        {
            var formattedLine = new StringBuilder();

            foreach (var lineConfig in layout.Elements)
            {
            }

            return formattedLine.ToString();
        }

        private string GetExampleForDataType(string dataType)
        {
            return dataType?.ToLower() switch
            {
                "date" => "20231225",
                "cnpj" => "12345678000195",
                "cpf" => "12345678901",
                "money" => "0000000019990",
                "text" => "EXEMPLO DE TEXTO",
                _ => "123456"
            };
        }

        public class LayoutAnalysis
        {
            public string LayoutName { get; set; }
            public int TotalFields { get; set; }
            public List<LineTypeAnalysis> LineTypes { get; set; }
        }

        public class LineTypeAnalysis
        {
            public string Name { get; set; }
            public string Identifier { get; set; }
            public List<FieldAnalysis> Fields { get; set; }
        }

        public class FieldAnalysis
        {
            public string Name { get; set; }
            public string DataType { get; set; }
            public int Length { get; set; }
            public bool IsRequired { get; set; }
            public string Description { get; set; }
            public int StartPosition { get; set; }
        }

        public class OpenAIResponse
        {
            public Choice[] choices { get; set; }
        }

        public class Choice
        {
            public Message message { get; set; }
        }

        public class Message
        {
            public string content { get; set; }
        }
    }
}
