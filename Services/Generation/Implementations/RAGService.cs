using System.Text;
using System.Text.Json;

namespace LayoutParserApi.Services.Generation.Implementations
{
    public class RAGService
    {
        private readonly ILogger<RAGService> _logger;
        private readonly string _examplesPath;
        private readonly Dictionary<string, List<string>> _exampleCache = new();

        public RAGService(IConfiguration configuration, ILogger<RAGService> logger)
        {
            _logger = logger;
            _examplesPath = configuration["RAG:ExamplesPath"] ?? "Exemplos";
            
            // Criar diretório se não existir
            if (!Directory.Exists(_examplesPath))
            {
                Directory.CreateDirectory(_examplesPath);
                _logger.LogInformation("Diretório de exemplos criado: {Path}", _examplesPath);
            }
            
            LoadExamples();
        }

        /// <summary>
        /// Carrega todos os exemplos da pasta
        /// </summary>
        private void LoadExamples()
        {
            try
            {
                if (!Directory.Exists(_examplesPath))
                {
                    _logger.LogWarning("Diretório de exemplos não encontrado: {Path}", _examplesPath);
                    return;
                }

                var files = Directory.GetFiles(_examplesPath, "*.txt", SearchOption.AllDirectories);
                _logger.LogInformation("Carregando {Count} arquivos de exemplo de {Path}", files.Length, _examplesPath);

                foreach (var file in files)
                {
                    try
                    {
                        var content = File.ReadAllText(file, Encoding.UTF8);
                        var fileName = Path.GetFileNameWithoutExtension(file);
                        
                        if (!_exampleCache.ContainsKey(fileName))
                        {
                            _exampleCache[fileName] = new List<string>();
                        }
                        
                        // Dividir conteúdo em linhas e adicionar ao cache
                        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                          .Where(line => !string.IsNullOrWhiteSpace(line))
                                          .ToList();
                        
                        _exampleCache[fileName].AddRange(lines);
                        
                        _logger.LogDebug("Carregado {Lines} linhas do arquivo {File}", lines.Count, fileName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Erro ao carregar arquivo de exemplo: {File}", file);
                    }
                }

                _logger.LogInformation("RAG Service inicializado com {Files} arquivos e {TotalLines} linhas de exemplo", 
                    _exampleCache.Count, _exampleCache.Values.Sum(list => list.Count));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao inicializar RAG Service");
            }
        }

        /// <summary>
        /// Busca exemplos relevantes baseados no layout
        /// </summary>
        public List<string> FindRelevantExamples(string layoutXml, int maxExamples = 5)
        {
            try
            {
                var relevantExamples = new List<string>();
                
                // Extrair informações do layout para busca
                var layoutInfo = ExtractLayoutInfo(layoutXml);
                
                // Buscar exemplos que correspondem ao tipo de layout
                foreach (var (fileName, examples) in _exampleCache)
                {
                    var score = CalculateRelevanceScore(layoutInfo, examples);
                    
                    if (score > 0.3) // Threshold de relevância
                    {
                        var topExamples = examples.Take(3).ToList();
                        relevantExamples.AddRange(topExamples);
                        
                        if (relevantExamples.Count >= maxExamples)
                            break;
                    }
                }

                // Se não encontrou exemplos específicos, usar exemplos gerais
                if (relevantExamples.Count == 0)
                {
                    var allExamples = _exampleCache.Values.SelectMany(list => list).Take(maxExamples).ToList();
                    relevantExamples.AddRange(allExamples);
                }

                _logger.LogInformation("Encontrados {Count} exemplos relevantes para o layout", relevantExamples.Count);
                return relevantExamples.Take(maxExamples).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar exemplos relevantes");
                return new List<string>();
            }
        }

        /// <summary>
        /// Extrai informações do layout para busca
        /// </summary>
        private Dictionary<string, string> ExtractLayoutInfo(string layoutXml)
        {
            var info = new Dictionary<string, string>();
            
            try
            {
                // Extrair tipo de layout
                if (layoutXml.Contains("IDOC") || layoutXml.Contains("EDI_DC40"))
                    info["type"] = "IDOC";
                else if (layoutXml.Contains("MQSeries"))
                    info["type"] = "MQSeries";
                else if (layoutXml.Contains("XML"))
                    info["type"] = "XML";
                else
                    info["type"] = "Unknown";

                // Extrair nomes de campos
                var fieldMatches = System.Text.RegularExpressions.Regex.Matches(layoutXml, @"<Name>([^<]+)</Name>");
                var fieldNames = fieldMatches.Cast<System.Text.RegularExpressions.Match>()
                                           .Select(m => m.Groups[1].Value)
                                           .Take(10)
                                           .ToList();
                
                info["fields"] = string.Join(",", fieldNames);
                
                // Extrair tamanhos de campos
                var sizeMatches = System.Text.RegularExpressions.Regex.Matches(layoutXml, @"Size=""(\d+)""");
                var sizes = sizeMatches.Cast<System.Text.RegularExpressions.Match>()
                                     .Select(m => m.Groups[1].Value)
                                     .Take(5)
                                     .ToList();
                
                info["sizes"] = string.Join(",", sizes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao extrair informações do layout");
            }
            
            return info;
        }

        /// <summary>
        /// Calcula score de relevância entre layout e exemplos
        /// </summary>
        private double CalculateRelevanceScore(Dictionary<string, string> layoutInfo, List<string> examples)
        {
            double score = 0.0;
            
            try
            {
                var layoutType = layoutInfo.GetValueOrDefault("type", "").ToLower();
                var layoutFields = layoutInfo.GetValueOrDefault("fields", "").ToLower();
                
                foreach (var example in examples.Take(5)) // Analisar apenas os primeiros 5 exemplos
                {
                    var exampleLower = example.ToLower();
                    
                    // Score por tipo de layout
                    if (layoutType == "idoc" && (exampleLower.Contains("edi_dc40") || exampleLower.Contains("zrsdm_")))
                        score += 0.4;
                    else if (layoutType == "mqseries" && exampleLower.Contains("mqseries"))
                        score += 0.4;
                    else if (layoutType == "xml" && exampleLower.Contains("<"))
                        score += 0.4;
                    
                    // Score por campos similares
                    if (!string.IsNullOrEmpty(layoutFields))
                    {
                        var fieldMatches = layoutFields.Split(',')
                                                     .Count(field => exampleLower.Contains(field.Trim()));
                        score += fieldMatches * 0.1;
                    }
                    
                    // Score por padrões de dados
                    if (exampleLower.Contains("000000000") || exampleLower.Contains("999999999"))
                        score += 0.1; // Padrões numéricos
                    
                    if (exampleLower.Contains("test") || exampleLower.Contains("sample"))
                        score += 0.05; // Dados de teste
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao calcular score de relevância");
            }
            
            return Math.Min(score, 1.0); // Normalizar para máximo 1.0
        }

        /// <summary>
        /// Adiciona novos exemplos ao cache
        /// </summary>
        public void AddExample(string fileName, string content)
        {
            try
            {
                if (!_exampleCache.ContainsKey(fileName))
                {
                    _exampleCache[fileName] = new List<string>();
                }
                
                var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                  .Where(line => !string.IsNullOrWhiteSpace(line))
                                  .ToList();
                
                _exampleCache[fileName].AddRange(lines);
                
                _logger.LogInformation("Adicionados {Count} novos exemplos para {FileName}", lines.Count, fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao adicionar exemplo: {FileName}", fileName);
            }
        }

        /// <summary>
        /// Recarrega exemplos do disco
        /// </summary>
        public void ReloadExamples()
        {
            _exampleCache.Clear();
            LoadExamples();
        }

        /// <summary>
        /// Retorna estatísticas do RAG
        /// </summary>
        public Dictionary<string, object> GetStats()
        {
            return new Dictionary<string, object>
            {
                ["totalFiles"] = _exampleCache.Count,
                ["totalLines"] = _exampleCache.Values.Sum(list => list.Count),
                ["examplesPath"] = _examplesPath,
                ["cacheStatus"] = "Loaded"
            };
        }
    }
}