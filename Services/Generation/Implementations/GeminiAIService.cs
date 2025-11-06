using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace LayoutParserApi.Services.Generation.Implementations
{
    public class GeminiAIService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<GeminiAIService> _logger;
        private readonly RAGService _ragService;
        private readonly IConfiguration _configuration;
        private readonly string _apiKey;
        private readonly string _modelName;
        private readonly string _examplesPath;

        public GeminiAIService(
            HttpClient httpClient,
            IConfiguration configuration,
            ILogger<GeminiAIService> logger,
            RAGService ragService)
        {
            _httpClient = httpClient;
            _logger = logger;
            _ragService = ragService;
            _configuration = configuration;
            _apiKey = configuration["Gemini:ApiKey"] ?? "AIzaSyDwNhK9Hc1nie9lmHJrfLmKBIJzHWNkzD8";
            _modelName = configuration["Gemini:Model"] ?? "gemini-1.5-flash";
            _examplesPath = configuration["Examples:Path"] ?? @"C:\inetpub\wwwroot\layoutparser\Exemplo";
            
            _logger.LogInformation("GeminiAIService configurado - Model: {Model}, RAG: {RAGStatus}, ExamplesPath: {Path}", 
                _modelName, _ragService != null ? "Ativo" : "Inativo", _examplesPath);
        }

        /// <summary>
        /// Gera dados sintéticos baseados no layout e exemplos
        /// </summary>
        public async Task<string> GenerateSyntheticData(
            string layoutXml,
            List<string> examples,
            Dictionary<string, string> excelRules,
            int recordCount = 1,
            string layoutName = null,
            string layoutDescription = null,
            Models.Generation.ExcelDataContext excelContext = null)
        {
            try
            {
                if (string.IsNullOrEmpty(_apiKey))
                {
                    throw new Exception("Chave da API do Gemini não configurada");
                }

                // Buscar exemplos do diretório baseado no nome do layout
                var directoryExamples = await LoadExamplesFromDirectory(layoutName);
                
                // Combinar exemplos do diretório com exemplos fornecidos
                var allExamples = new List<string>();
                if (directoryExamples.Any())
                {
                    allExamples.AddRange(directoryExamples);
                    _logger.LogInformation("Carregados {Count} exemplos do diretório para layout {LayoutName}", 
                        directoryExamples.Count, layoutName);
                }
                if (examples != null && examples.Any())
                {
                    allExamples.AddRange(examples);
                }

                // Montar prompt com contexto melhorado
                var prompt = BuildPromptWithContext(layoutXml, allExamples, excelRules, recordCount, 
                    layoutName, layoutDescription, excelContext);
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
        /// Constrói prompt com contexto melhorado e detalhado
        /// </summary>
        private string BuildPromptWithContext(
            string layoutXml,
            List<string> examples,
            Dictionary<string, string> excelRules,
            int recordCount,
            string layoutName,
            string layoutDescription,
            Models.Generation.ExcelDataContext excelContext)
        {
            var sb = new StringBuilder();

            // ===== SEÇÃO 1: CONTEXTO E IDENTIFICAÇÃO =====
            sb.AppendLine("=== TAREFA DE GERAÇÃO DE DADOS SINTÉTICOS ===");
            sb.AppendLine();
            
            if (!string.IsNullOrEmpty(layoutName))
            {
                sb.AppendLine($"LAYOUT: {layoutName}");
                if (!string.IsNullOrEmpty(layoutDescription))
                {
                    sb.AppendLine($"DESCRIÇÃO: {layoutDescription}");
                }
                sb.AppendLine();
            }
            
            sb.AppendLine($"OBJETIVO: Gerar exatamente {recordCount} registro(s) de dados sintéticos que seguem fielmente a estrutura do layout XML fornecido.");
            sb.AppendLine();

            // ===== SEÇÃO 2: ESTRUTURA DETALHADA DO LAYOUT =====
            sb.AppendLine("=== ESTRUTURA DO LAYOUT (CAMPO A CAMPO) ===");
            sb.AppendLine();
            
            var layoutDetails = ExtractDetailedLayoutInfo(layoutXml);
            sb.AppendLine(layoutDetails);
            sb.AppendLine();

            // ===== SEÇÃO 3: EXEMPLOS REAIS (OTIMIZADOS) =====
            if (examples != null && examples.Any())
            {
                sb.AppendLine("=== EXEMPLOS REAIS ===");
                sb.AppendLine("ANALISE: tamanho de linha (600 chars), formato de campos, padrões de dados.");
                sb.AppendLine();
                
                // Mostrar apenas 2 exemplos completos, limitando a 5 linhas por exemplo
                for (int i = 0; i < Math.Min(examples.Count, 2); i++)
                {
                    sb.AppendLine($"EXEMPLO {i + 1}:");
                    var exampleLines = examples[i].Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in exampleLines.Take(5))
                    {
                        var lineLength = line.Length;
                        var preview = line.Length > 100 ? line.Substring(0, 100) + "..." : line;
                        sb.AppendLine($"[{lineLength} chars] {preview}");
                    }
                    sb.AppendLine();
                }
                
                // Mostrar análise de tamanhos se houver mais exemplos
                if (examples.Count > 2)
                {
                    var allLines = examples.SelectMany(e => e.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                                          .Where(l => !string.IsNullOrWhiteSpace(l))
                                          .ToList();
                    var avgLength = allLines.Any() ? (int)allLines.Average(l => l.Length) : 600;
                    var minLength = allLines.Any() ? allLines.Min(l => l.Length) : 600;
                    var maxLength = allLines.Any() ? allLines.Max(l => l.Length) : 600;
                    
                    sb.AppendLine($"ANÁLISE: {allLines.Count} linhas analisadas. Tamanho médio: {avgLength}, min: {minLength}, max: {maxLength}");
                    sb.AppendLine("TODAS as linhas devem ter EXATAMENTE 600 caracteres.");
                    sb.AppendLine();
                }
            }

            // ===== SEÇÃO 4: DADOS DO EXCEL (OTIMIZADOS) =====
            if (excelContext != null && excelContext.Headers.Any())
            {
                sb.AppendLine("=== DADOS DO EXCEL ===");
                sb.AppendLine("USE estes valores como referência para gerar dados realistas:");
                sb.AppendLine();
                
                // Mostrar apenas as colunas mais relevantes (primeiras 15) com exemplos resumidos
                foreach (var header in excelContext.Headers.Take(15))
                {
                    if (excelContext.ColumnData.ContainsKey(header) && excelContext.ColumnData[header].Any())
                    {
                        var samples = excelContext.ColumnData[header]
                            .Where(v => !string.IsNullOrWhiteSpace(v))
                            .Distinct()
                            .Take(5)
                            .ToList();
                        
                        if (samples.Any())
                        {
                            var samplesStr = string.Join(", ", samples.Select(s => s.Length > 30 ? s.Substring(0, 30) + "..." : s));
                            sb.AppendLine($"{header}: {samplesStr}");
                        }
                    }
                }
                sb.AppendLine();
            }

            if (excelRules != null && excelRules.Any())
            {
                sb.AppendLine("=== REGRAS ESPECÍFICAS DO EXCEL ===");
                foreach (var rule in excelRules)
                {
                    sb.AppendLine($"- {rule.Key}: {rule.Value}");
                }
                sb.AppendLine();
            }

            // ===== SEÇÃO 5: INSTRUÇÕES CRÍTICAS (OTIMIZADAS) =====
            sb.AppendLine("=== REGRAS CRÍTICAS ===");
            sb.AppendLine();
            sb.AppendLine("TAMANHO DE LINHA: Cada linha DEVE ter EXATAMENTE 600 caracteres. NUNCA mais, NUNCA menos.");
            sb.AppendLine("Se a linha exceder 600 caracteres, remova espaços ou trunque o final. Se tiver menos, preencha com espaços.");
            sb.AppendLine();
            sb.AppendLine("FORMATO:");
            sb.AppendLine("- Primeira linha: HEADER (se existir) com 600 caracteres");
            sb.AppendLine("- Linhas seguintes: Sequencia (6 chars) + campos conforme layout");
            sb.AppendLine("- Campos obrigatórios: NUNCA vazios");
            sb.AppendLine("- Alinhamento: Left (esquerda), Right (direita), Center (centro)");
            sb.AppendLine();
            sb.AppendLine("DADOS REALISTAS:");
            sb.AppendLine("- Use padrões dos exemplos fornecidos");
            sb.AppendLine("- CNPJ: 14 dígitos, CPF: 11 dígitos");
            sb.AppendLine("- Datas: formato dos exemplos");
            sb.AppendLine("- Prefira dados do Excel quando disponível");
            sb.AppendLine();
            sb.AppendLine("RESPOSTA:");
            sb.AppendLine($"- Retorne APENAS {recordCount} linha(s) de dados");
            sb.AppendLine("- Cada linha com EXATAMENTE 600 caracteres");
            sb.AppendLine("- Sem explicações, comentários ou cabeçalhos");
            sb.AppendLine("- Uma linha por registro");

            return sb.ToString();
        }

        /// <summary>
        /// Carrega exemplos do diretório baseado no nome do layout
        /// </summary>
        private async Task<List<string>> LoadExamplesFromDirectory(string layoutName)
        {
            var examples = new List<string>();
            
            if (string.IsNullOrEmpty(layoutName) || !Directory.Exists(_examplesPath))
            {
                return examples;
            }

            try
            {
                var allFiles = new List<string>();
                
                // Buscar diretórios que contenham o nome do layout
                var matchingDirs = Directory.GetDirectories(_examplesPath, $"*{layoutName}*", SearchOption.TopDirectoryOnly);
                
                foreach (var dir in matchingDirs)
                {
                    // Buscar arquivos .txt e .mq_series no diretório e subdiretórios
                    var txtFiles = Directory.GetFiles(dir, "*.txt", SearchOption.AllDirectories);
                    var mqSeriesFiles = Directory.GetFiles(dir, "*.mq_series", SearchOption.AllDirectories);
                    
                    allFiles.AddRange(txtFiles);
                    allFiles.AddRange(mqSeriesFiles);
                }
                
                // Também buscar arquivos diretamente no diretório raiz que contenham o nome do layout
                var rootTxtFiles = Directory.GetFiles(_examplesPath, $"*{layoutName}*.txt", SearchOption.TopDirectoryOnly);
                var rootMqSeriesFiles = Directory.GetFiles(_examplesPath, $"*{layoutName}*.mq_series", SearchOption.TopDirectoryOnly);
                
                allFiles.AddRange(rootTxtFiles);
                allFiles.AddRange(rootMqSeriesFiles);
                
                // Remover duplicatas e ordenar
                allFiles = allFiles.Distinct().OrderBy(f => f).ToList();
                
                foreach (var file in allFiles.Take(5)) // Limitar a 5 arquivos por layout
                {
                    try
                    {
                        var content = await File.ReadAllTextAsync(file, Encoding.UTF8);
                        if (!string.IsNullOrWhiteSpace(content))
                        {
                            examples.Add(content);
                            _logger.LogDebug("Carregado exemplo do arquivo: {File}", file);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Erro ao ler arquivo de exemplo: {File}", file);
                    }
                }
                
                _logger.LogInformation("Carregados {Count} exemplos do diretório para layout {LayoutName} (arquivos encontrados: {TotalFiles})", 
                    examples.Count, layoutName, allFiles.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao carregar exemplos do diretório {Path}", _examplesPath);
            }
            
            return examples;
        }

        /// <summary>
        /// Extrai informações detalhadas do layout XML (campo a campo)
        /// </summary>
        private string ExtractDetailedLayoutInfo(string layoutXml)
        {
            try
            {
                var sb = new StringBuilder();
                var doc = XDocument.Parse(layoutXml);
                
                // Extrair informações do layout raiz
                var layout = doc.Root;
                var layoutType = layout?.Element("LayoutType")?.Value ?? "Unknown";
                var limitOfChars = layout?.Element("LimitOfCaracters")?.Value ?? "600";
                
                sb.AppendLine($"Tipo de Layout: {layoutType}");
                sb.AppendLine($"Limite de Caracteres por Linha: {limitOfChars}");
                sb.AppendLine();
                
                // Processar cada elemento (linha)
                var elements = layout?.Element("Elements")?.Elements("Element") ?? Enumerable.Empty<XElement>();
                
                foreach (var lineElement in elements)
                {
                    var lineName = lineElement.Element("Name")?.Value ?? "Unknown";
                    var initialValue = lineElement.Element("InitialValue")?.Value ?? "";
                    var maxOccurrence = lineElement.Element("MaximumOccurrence")?.Value ?? "1";
                    
                    sb.AppendLine($"--- LINHA: {lineName} ---");
                    if (!string.IsNullOrEmpty(initialValue))
                    {
                        sb.AppendLine($"Valor Inicial: '{initialValue}' ({initialValue.Length} chars)");
                    }
                    sb.AppendLine($"Ocorrências Máximas: {maxOccurrence}");
                    sb.AppendLine();
                    
                    // Processar campos da linha
                    var fields = lineElement.Element("Elements")?.Elements("Element") ?? Enumerable.Empty<XElement>();
                    var fieldList = new List<(string name, int sequence, int start, int length, string alignment, bool required)>();
                    
                    foreach (var fieldElement in fields)
                    {
                        var fieldName = fieldElement.Element("Name")?.Value;
                        if (fieldName == null || fieldName.Equals("Sequencia", StringComparison.OrdinalIgnoreCase))
                            continue;
                            
                        var sequenceStr = fieldElement.Element("Sequence")?.Value;
                        var startStr = fieldElement.Element("StartValue")?.Value;
                        var lengthStr = fieldElement.Element("LengthField")?.Value;
                        var alignment = fieldElement.Element("AlignmentType")?.Value ?? "Left";
                        var requiredStr = fieldElement.Element("IsRequired")?.Value ?? "false";
                        
                        if (int.TryParse(sequenceStr, out var sequence) &&
                            int.TryParse(startStr, out var start) &&
                            int.TryParse(lengthStr, out var length))
                        {
                            fieldList.Add((fieldName, sequence, start, length, alignment, requiredStr == "true"));
                        }
                    }
                    
                    // Ordenar campos por sequência
                    fieldList = fieldList.OrderBy(f => f.sequence).ToList();
                    
                    // Mostrar campos ordenados de forma mais concisa
                    sb.AppendLine("CAMPOS:");
                    foreach (var field in fieldList.Take(30)) // Limitar a 30 campos por linha para reduzir tamanho
                    {
                        sb.AppendLine($"  [{field.sequence}] {field.name}: pos {field.start + 1}-{field.start + field.length}, tam {field.length}, alinh {field.alignment}, obrigatório: {(field.required ? "SIM" : "NÃO")}");
                    }
                    if (fieldList.Count > 30)
                    {
                        sb.AppendLine($"  ... e mais {fieldList.Count - 30} campos");
                    }
                    sb.AppendLine();
                }
                
                return sb.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao extrair informações detalhadas do layout");
                return "Estrutura do layout detectada. Siga o XML fornecido.";
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
                    temperature = 0.3, // Reduzido para mais precisão
                    topK = 40,
                    topP = 0.95,
                    maxOutputTokens = 8192 // Aumentado para suportar múltiplos registros e prompts maiores
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
