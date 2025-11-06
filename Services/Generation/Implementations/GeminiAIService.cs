using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Newtonsoft.Json;
using LayoutParserApi.Models.Enums;

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
        private readonly List<Interfaces.IFieldGenerator> _fieldGenerators;
        private readonly Interfaces.ILayoutTypeDetector _layoutTypeDetector;
        private readonly Interfaces.ILineValidator _lineValidator;

        public GeminiAIService(
            HttpClient httpClient,
            IConfiguration configuration,
            ILogger<GeminiAIService> logger,
            RAGService ragService,
            Interfaces.ILayoutTypeDetector layoutTypeDetector,
            Interfaces.ILineValidator lineValidator,
            IEnumerable<Interfaces.IFieldGenerator> fieldGenerators = null)
        {
            _httpClient = httpClient;
            _logger = logger;
            _ragService = ragService;
            _configuration = configuration;
            _layoutTypeDetector = layoutTypeDetector;
            _lineValidator = lineValidator;
            _apiKey = configuration["Gemini:ApiKey"] ?? "AIzaSyDwNhK9Hc1nie9lmHJrfLmKBIJzHWNkzD8";
            _modelName = configuration["Gemini:Model"] ?? "gemini-1.5-flash";
            _examplesPath = configuration["Examples:Path"] ?? @"C:\inetpub\wwwroot\layoutparser\Exemplo";
            _fieldGenerators = fieldGenerators?.ToList() ?? new List<Interfaces.IFieldGenerator>();
            
            _logger.LogInformation("GeminiAIService configurado - Model: {Model}, RAG: {RAGStatus}, ExamplesPath: {Path}, FieldGenerators: {Count}", 
                _modelName, _ragService != null ? "Ativo" : "Inativo", _examplesPath, _fieldGenerators.Count);
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

                // Detectar tipo de layout
                var detectedLayoutType = _layoutTypeDetector.DetectLayoutType(layoutXml);
                if (string.IsNullOrEmpty(detectedLayoutType) || detectedLayoutType == "Unknown")
                {
                    detectedLayoutType = _layoutTypeDetector.DetectLayoutTypeByName(layoutName);
                }
                _logger.LogInformation("Tipo de layout detectado: {LayoutType} para layout {LayoutName}", detectedLayoutType, layoutName);

                // Identificar campos que podem ser gerados por geradores específicos
                var fieldMapping = IdentifyFieldGenerators(layoutXml, detectedLayoutType);
                
                // Para layouts TextPositional (mqseries, idoc), gerar incrementalmente linha por linha
                if (detectedLayoutType == "TextPositional")
                {
                    return await GenerateIncremental(layoutXml, allExamples, excelRules, recordCount,
                        layoutName, layoutDescription, excelContext, fieldMapping, detectedLayoutType);
                }
                
                // Para outros tipos (XML, etc.), usar geração tradicional
                var prompt = BuildPromptWithContext(layoutXml, allExamples, excelRules, recordCount, 
                    layoutName, layoutDescription, excelContext, fieldMapping, detectedLayoutType);
                _logger.LogInformation("Enviando prompt para Gemini (tamanho: {Size} caracteres). Campos pré-gerados: {PreGeneratedCount}, Tipo: {LayoutType}", 
                    prompt.Length, fieldMapping.Count, detectedLayoutType);

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
        /// Identifica quais campos podem ser gerados por geradores específicos
        /// </summary>
        private Dictionary<string, (string fieldName, int start, int length, string alignment, string generatedValue)> IdentifyFieldGenerators(string layoutXml, string layoutType)
        {
            var fieldMapping = new Dictionary<string, (string, int, int, string, string)>();
            
            try
            {
                var doc = XDocument.Parse(layoutXml);
                var elements = doc.Root?.Element("Elements")?.Elements("Element") ?? Enumerable.Empty<XElement>();
                
                foreach (var lineElement in elements)
                {
                    var lineName = lineElement.Element("Name")?.Value ?? "Unknown";
                    var fields = lineElement.Element("Elements")?.Elements("Element") ?? Enumerable.Empty<XElement>();
                    
                    foreach (var fieldElement in fields)
                    {
                        var fieldName = fieldElement.Element("Name")?.Value;
                        if (string.IsNullOrWhiteSpace(fieldName) || fieldName.Equals("Sequencia", StringComparison.OrdinalIgnoreCase))
                            continue;
                        
                        var startStr = fieldElement.Element("StartValue")?.Value;
                        var lengthStr = fieldElement.Element("LengthField")?.Value;
                        var alignment = fieldElement.Element("AlignmentType")?.Value ?? "Left";
                        
                        if (int.TryParse(startStr, out var start) && int.TryParse(lengthStr, out var length))
                        {
                            // Verificar se algum gerador pode gerar este campo
                            foreach (var generator in _fieldGenerators)
                            {
                                if (generator.CanGenerate(fieldName, length))
                                {
                                    // Gerar valor de exemplo (usando índice 0 como referência)
                                    var context = new Dictionary<string, object>
                                    {
                                        ["RecordIndex"] = 0,
                                        ["InitialValue"] = lineElement.Element("InitialValue")?.Value ?? ""
                                    };
                                    
                                    var generatedValue = generator.Generate(fieldName, length, alignment, 0, context);
                                    var key = $"{lineName}.{fieldName}";
                                    fieldMapping[key] = (fieldName, start, length, alignment, generatedValue);
                                    _logger.LogDebug("Campo {FieldName} será pré-gerado com valor: {Value}", fieldName, generatedValue);
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao identificar geradores de campos");
            }
            
            return fieldMapping;
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
            Models.Generation.ExcelDataContext excelContext,
            Dictionary<string, (string fieldName, int start, int length, string alignment, string generatedValue)> fieldMapping = null,
            string layoutType = "TextPositional")
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
            
            sb.AppendLine($"TIPO DE LAYOUT: {layoutType}");
            sb.AppendLine();
            
            // Instruções específicas por tipo de layout
            if (layoutType == "Xml")
            {
                sb.AppendLine("FORMATO ESPERADO: XML bem formado com tags, atributos e valores.");
                sb.AppendLine("IMPORTANTE: Gere XML válido com tags de abertura e fechamento corretas.");
            }
            else if (layoutType == "TextPositional")
            {
                sb.AppendLine("FORMATO ESPERADO: Texto posicional com campos em posições fixas.");
                sb.AppendLine("IMPORTANTE: Cada linha deve ter exatamente o tamanho especificado (geralmente 600 caracteres).");
            }
            else if (layoutType.Contains("IDOC") || layoutName?.ToUpperInvariant().Contains("IDOC") == true)
            {
                sb.AppendLine("FORMATO ESPERADO: IDOC (SAP) com segmentos como EDI_DC40, ZRSDM_NFE_400, etc.");
                sb.AppendLine("IMPORTANTE: Mantenha a estrutura de segmentos IDOC com prefixos corretos.");
            }
            sb.AppendLine();
            
            sb.AppendLine($"OBJETIVO: Gerar exatamente {recordCount} registro(s) de dados sintéticos que seguem fielmente a estrutura do layout XML fornecido.");
            sb.AppendLine();

            // ===== SEÇÃO 2: CAMPOS PRÉ-GERADOS (GERADORES ESPECÍFICOS) =====
            if (fieldMapping != null && fieldMapping.Any())
            {
                sb.AppendLine("=== CAMPOS PRÉ-GERADOS (NÃO ALTERE ESTES) ===");
                sb.AppendLine();
                sb.AppendLine("IMPORTANTE: Os seguintes campos já foram gerados automaticamente por geradores específicos.");
                sb.AppendLine("MANTENHA estes valores exatamente como estão, apenas ajuste a posição se necessário.");
                sb.AppendLine();
                
                foreach (var field in fieldMapping.OrderBy(f => f.Value.start))
                {
                    var (fieldName, start, length, alignment, generatedValue) = field.Value;
                    sb.AppendLine($"Campo: {fieldName}");
                    sb.AppendLine($"  Posição: {start + 1}-{start + length}");
                    sb.AppendLine($"  Tamanho: {length} caracteres");
                    sb.AppendLine($"  Alinhamento: {alignment}");
                    sb.AppendLine($"  Valor gerado: '{generatedValue}'");
                    sb.AppendLine();
                }
                
                sb.AppendLine("Estes campos devem ser mantidos EXATAMENTE como mostrado acima.");
                sb.AppendLine("A IA deve gerar APENAS os campos que NÃO estão nesta lista.");
                sb.AppendLine();
            }

            // ===== SEÇÃO 3: ESTRUTURA DETALHADA DO LAYOUT =====
            sb.AppendLine("=== ESTRUTURA DO LAYOUT (CAMPO A CAMPO) ===");
            sb.AppendLine();
            
            var layoutDetails = ExtractDetailedLayoutInfo(layoutXml, fieldMapping);
            sb.AppendLine(layoutDetails);
            sb.AppendLine();

            // ===== SEÇÃO 4: EXEMPLOS REAIS COMPLETOS (FEW-SHOT LEARNING) =====
            if (examples != null && examples.Any())
            {
                sb.AppendLine("=== EXEMPLOS REAIS COMPLETOS (COPIE ESTES PADRÕES) ===");
                sb.AppendLine();
                sb.AppendLine("IMPORTANTE: Estes são exemplos REAIS de documentos. Analise cuidadosamente:");
                sb.AppendLine("1. O formato exato de cada campo");
                sb.AppendLine("2. Os valores típicos usados");
                sb.AppendLine("3. A sequência e ordem das linhas");
                sb.AppendLine("4. O tamanho exato de cada linha (600 caracteres)");
                sb.AppendLine("5. O preenchimento com espaços quando necessário");
                sb.AppendLine();
                
                // Analisar todos os exemplos para extrair padrões
                var allLines = examples.SelectMany(e => e.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                                      .Where(l => !string.IsNullOrWhiteSpace(l))
                                      .ToList();
                
                // Mostrar exemplos completos (até 3 arquivos completos, ou 10 linhas por arquivo)
                int exampleCount = 0;
                foreach (var example in examples.Take(3))
                {
                    var exampleLines = example.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                             .Where(l => !string.IsNullOrWhiteSpace(l))
                                             .ToList();
                    
                    if (exampleLines.Any())
                    {
                        exampleCount++;
                        sb.AppendLine($"--- EXEMPLO REAL {exampleCount} (COMPLETO) ---");
                        // Mostrar todas as linhas do exemplo (até 15 linhas para não exceder muito o prompt)
                        foreach (var line in exampleLines.Take(15))
                        {
                            // Mostrar linha completa, não truncada
                            sb.AppendLine(line);
                        }
                        if (exampleLines.Count > 15)
                        {
                            sb.AppendLine($"... ({exampleLines.Count - 15} linhas adicionais neste exemplo)");
                        }
                        sb.AppendLine();
                    }
                }
                
                // Análise de padrões dos exemplos
                if (allLines.Any())
                {
                    sb.AppendLine("=== ANÁLISE DE PADRÕES DOS EXEMPLOS ===");
                    var avgLength = (int)allLines.Average(l => l.Length);
                    var minLength = allLines.Min(l => l.Length);
                    var maxLength = allLines.Max(l => l.Length);
                    
                    sb.AppendLine($"Total de linhas analisadas: {allLines.Count}");
                    sb.AppendLine($"Tamanho médio: {avgLength} caracteres");
                    sb.AppendLine($"Tamanho mínimo: {minLength} caracteres");
                    sb.AppendLine($"Tamanho máximo: {maxLength} caracteres");
                    sb.AppendLine();
                    sb.AppendLine("CRÍTICO: TODAS as linhas geradas devem ter EXATAMENTE 600 caracteres.");
                    sb.AppendLine();
                    
                    // Extrair padrões de valores comuns (primeiros caracteres, últimos caracteres, etc)
                    var firstChars = allLines.Select(l => l.Length > 0 ? l.Substring(0, Math.Min(10, l.Length)) : "").Distinct().Take(10);
                    sb.AppendLine($"Padrões iniciais comuns: {string.Join(", ", firstChars)}");
                    sb.AppendLine();
                }
            }

            // ===== SEÇÃO 5: DADOS DO EXCEL (MAPEAMENTO INTELIGENTE) =====
            if (excelContext != null && excelContext.Headers.Any())
            {
                sb.AppendLine("=== DADOS DO EXCEL (USE ESTES VALORES REAIS) ===");
                sb.AppendLine();
                sb.AppendLine("IMPORTANTE: Estes são dados REAIS do Excel. Use-os diretamente ou como base para variações realistas.");
                sb.AppendLine();
                
                // Mostrar mais colunas e mais exemplos por coluna
                foreach (var header in excelContext.Headers.Take(20))
                {
                    if (excelContext.ColumnData.ContainsKey(header) && excelContext.ColumnData[header].Any())
                    {
                        var allValues = excelContext.ColumnData[header]
                            .Where(v => !string.IsNullOrWhiteSpace(v))
                            .Distinct()
                            .ToList();
                        
                        if (allValues.Any())
                        {
                            // Mostrar mais exemplos (até 10)
                            var samples = allValues.Take(10).ToList();
                            var samplesStr = string.Join(", ", samples.Select(s => 
                            {
                                // Mostrar valores completos se forem curtos, truncar se forem longos
                                if (s.Length <= 50) return s;
                                return s.Substring(0, 47) + "...";
                            }));
                            
                            sb.AppendLine($"{header} ({allValues.Count} valores únicos):");
                            sb.AppendLine($"  Exemplos: {samplesStr}");
                            
                            // Detectar tipo de dado
                            if (excelContext.ColumnTypes.ContainsKey(header))
                            {
                                sb.AppendLine($"  Tipo detectado: {excelContext.ColumnTypes[header]}");
                            }
                            sb.AppendLine();
                        }
                    }
                }
                sb.AppendLine("INSTRUÇÃO: Quando um campo do layout corresponder a uma coluna do Excel, use valores similares aos exemplos acima.");
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

            // ===== SEÇÃO 6: INSTRUÇÕES CRÍTICAS E METODOLOGIA =====
            sb.AppendLine("=== METODOLOGIA DE GERAÇÃO (SIGA RIGOROSAMENTE) ===");
            sb.AppendLine();
            
            sb.AppendLine("1. TAMANHO DE LINHA (CRÍTICO):");
            sb.AppendLine("   - Cada linha DEVE ter EXATAMENTE 600 caracteres");
            sb.AppendLine("   - NUNCA mais, NUNCA menos");
            sb.AppendLine("   - Se exceder: remova espaços extras ou trunque campos não críticos");
            sb.AppendLine("   - Se faltar: preencha com espaços à direita (alinhamento Left) ou esquerda (Right)");
            sb.AppendLine();
            
            sb.AppendLine("2. COPIE OS EXEMPLOS REAIS:");
            sb.AppendLine("   - Use os EXEMPLOS REAIS acima como modelo exato");
            sb.AppendLine("   - Mantenha o mesmo formato, estrutura e padrões");
            sb.AppendLine("   - Varie apenas os valores, mantendo o formato idêntico");
            sb.AppendLine();
            
            sb.AppendLine("3. SEQUÊNCIA E ORDEM:");
            sb.AppendLine("   - Primeira linha: HEADER (se existir no layout)");
            sb.AppendLine("   - Linhas seguintes: Sequencia (6 chars) + campos conforme layout");
            sb.AppendLine("   - Siga a ordem exata dos campos conforme especificado");
            sb.AppendLine();
            
            sb.AppendLine("4. FORMATAÇÃO DE CAMPOS:");
            sb.AppendLine("   - Campos numéricos: apenas dígitos, sem espaços ou caracteres especiais");
            sb.AppendLine("   - CNPJ: 14 dígitos consecutivos");
            sb.AppendLine("   - CPF: 11 dígitos consecutivos");
            sb.AppendLine("   - Datas: formato exato dos exemplos (geralmente DDMMYYYY ou similar)");
            sb.AppendLine("   - Valores monetários: sem separadores, apenas dígitos");
            sb.AppendLine("   - Texto: preencher completamente até o tamanho do campo");
            sb.AppendLine("   - Alinhamento: Left (esquerda), Right (direita), Center (centro) - respeitar exatamente");
            sb.AppendLine();
            
            sb.AppendLine("5. DADOS REALISTAS:");
            sb.AppendLine("   - PREFIRA valores do Excel quando disponível");
            sb.AppendLine("   - Use variações dos exemplos reais (não invente valores completamente diferentes)");
            sb.AppendLine("   - Mantenha consistência: se um exemplo mostra CNPJ começando com 12, use CNPJs similares");
            sb.AppendLine("   - Campos obrigatórios: NUNCA deixar vazios");
            sb.AppendLine();
            
            sb.AppendLine("6. VALIDAÇÃO:");
            sb.AppendLine("   - Cada linha será validada campo a campo");
            sb.AppendLine("   - Garanta que TODOS os campos estejam corretos antes de finalizar");
            sb.AppendLine("   - Verifique tamanhos, alinhamentos e formatos");
            sb.AppendLine();
            
            sb.AppendLine("=== RESPOSTA ESPERADA ===");
            sb.AppendLine($"Gere exatamente {recordCount} linha(s) seguindo TODAS as regras acima.");
            sb.AppendLine("Retorne APENAS as linhas de dados, sem explicações, comentários ou cabeçalhos.");
            sb.AppendLine("Cada linha deve ter EXATAMENTE 600 caracteres.");
            sb.AppendLine("Uma linha por registro, separadas por quebra de linha.");

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
                
                // Aumentar para 10 arquivos para ter mais exemplos completos
                foreach (var file in allFiles.Take(10))
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
        private string ExtractDetailedLayoutInfo(string layoutXml, Dictionary<string, (string fieldName, int start, int length, string alignment, string generatedValue)> fieldMapping = null, string layoutType = "TextPositional")
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
                        var fieldKey = $"{lineName}.{field.name}";
                        var isPreGenerated = fieldMapping != null && fieldMapping.ContainsKey(fieldKey);
                        var preGenMarker = isPreGenerated ? " [PRÉ-GERADO - NÃO ALTERAR]" : "";
                        
                        sb.AppendLine($"  [{field.sequence}] {field.name}: pos {field.start + 1}-{field.start + field.length}, tam {field.length}, alinh {field.alignment}, obrigatório: {(field.required ? "SIM" : "NÃO")}{preGenMarker}");
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
                    temperature = 0.1, // Muito baixo para máxima fidelidade aos exemplos
                    topK = 20, // Reduzido para focar nos exemplos mais prováveis
                    topP = 0.8, // Reduzido para menos variação
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

        /// <summary>
        /// Gera dados sintéticos incrementalmente, linha por linha, com validação após cada etapa
        /// </summary>
        private async Task<string> GenerateIncremental(
            string layoutXml,
            List<string> examples,
            Dictionary<string, string> excelRules,
            int recordCount,
            string layoutName,
            string layoutDescription,
            Models.Generation.ExcelDataContext excelContext,
            Dictionary<string, (string fieldName, int start, int length, string alignment, string generatedValue)> fieldMapping,
            string layoutType)
        {
            var generatedLines = new List<string>();
            var doc = XDocument.Parse(layoutXml);
            var layout = doc.Root;
            var limitOfChars = int.TryParse(layout?.Element("LimitOfCaracters")?.Value, out var limit) ? limit : 600;
            
            // Extrair linhas do layout em ordem
            var lineElements = layout?.Element("Elements")?.Elements("Element")
                .Where(e => e.Attribute(XName.Get("type", "http://www.w3.org/2001/XMLSchema-instance"))?.Value == "LineElementVO")
                .OrderBy(e => int.TryParse(e.Element("Sequence")?.Value, out var seq) ? seq : int.MaxValue)
                .ToList() ?? new List<XElement>();

            _logger.LogInformation("Iniciando geração incremental: {LineCount} linhas para {RecordCount} registro(s)", 
                lineElements.Count, recordCount);

            // Gerar cada registro
            for (int recordIndex = 0; recordIndex < recordCount; recordIndex++)
            {
                var recordLines = new List<string>();

                // Gerar cada linha do layout em ordem
                foreach (var lineElementXml in lineElements)
                {
                    var lineName = lineElementXml.Element("Name")?.Value ?? "Unknown";
                    var maxOccurrence = int.TryParse(lineElementXml.Element("MaximumOccurrence")?.Value, out var max) ? max : 1;
                    
                    // Gerar ocorrências da linha (se necessário)
                    for (int occurrence = 0; occurrence < maxOccurrence; occurrence++)
                    {
                        var lineGenerated = await GenerateSingleLineIncremental(
                            lineElementXml, 
                            lineName,
                            recordIndex,
                            occurrence,
                            fieldMapping,
                            examples,
                            excelContext,
                            limitOfChars);

                        // Validar linha gerada
                        var lineConfig = ParseLineElement(lineElementXml);
                        var validation = _lineValidator.ValidateLine(lineGenerated, lineConfig, limitOfChars);

                        // Se inválida, tentar corrigir (até 3 tentativas)
                        int retryCount = 0;
                        const int maxRetries = 3;
                        while (!validation.IsValid && retryCount < maxRetries)
                        {
                            retryCount++;
                            _logger.LogWarning("Linha {LineName} inválida (tentativa {Retry}/{MaxRetries}): {Errors}", 
                                lineName, retryCount, maxRetries, string.Join("; ", validation.Errors));

                            // Gerar correção com feedback dos erros
                            lineGenerated = await GenerateSingleLineIncremental(
                                lineElementXml,
                                lineName,
                                recordIndex,
                                occurrence,
                                fieldMapping,
                                examples,
                                excelContext,
                                limitOfChars,
                                previousAttempt: lineGenerated,
                                validationErrors: validation.Errors);

                            validation = _lineValidator.ValidateLine(lineGenerated, lineConfig, limitOfChars);
                        }

                        if (validation.IsValid)
                        {
                            recordLines.Add(lineGenerated);
                            _logger.LogDebug("Linha {LineName} gerada e validada com sucesso ({Length} chars)", 
                                lineName, lineGenerated.Length);
                        }
                        else
                        {
                            _logger.LogError("Linha {LineName} ainda inválida após {MaxRetries} tentativas. Erros: {Errors}", 
                                lineName, maxRetries, string.Join("; ", validation.Errors));
                            // Adicionar mesmo assim (com normalização) para não bloquear
                            recordLines.Add(NormalizeLineLength(lineGenerated, limitOfChars));
                        }
                    }
                }

                generatedLines.AddRange(recordLines);
            }

            return string.Join("\n", generatedLines);
        }

        /// <summary>
        /// Gera uma única linha incrementalmente
        /// </summary>
        private async Task<string> GenerateSingleLineIncremental(
            XElement lineElementXml,
            string lineName,
            int recordIndex,
            int occurrence,
            Dictionary<string, (string fieldName, int start, int length, string alignment, string generatedValue)> fieldMapping,
            List<string> allExamples,
            Models.Generation.ExcelDataContext excelContext,
            int limitOfChars,
            string previousAttempt = null,
            List<string> validationErrors = null)
        {
            // Construir prompt específico para esta linha
            var prompt = BuildLineSpecificPrompt(
                lineElementXml,
                lineName,
                recordIndex,
                occurrence,
                fieldMapping,
                allExamples,
                excelContext,
                limitOfChars,
                previousAttempt,
                validationErrors);

            // Chamar Gemini API para gerar apenas esta linha
            var response = await CallGeminiAPI(prompt);

            // Normalizar tamanho
            var lines = response.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                               .Where(l => !string.IsNullOrWhiteSpace(l))
                               .ToList();

            var generatedLine = lines.FirstOrDefault() ?? "";
            return NormalizeLineLength(generatedLine, limitOfChars);
        }

        /// <summary>
        /// Constrói prompt específico para uma linha
        /// </summary>
        private string BuildLineSpecificPrompt(
            XElement lineElementXml,
            string lineName,
            int recordIndex,
            int occurrence,
            Dictionary<string, (string fieldName, int start, int length, string alignment, string generatedValue)> fieldMapping,
            List<string> allExamples,
            Models.Generation.ExcelDataContext excelContext,
            int limitOfChars,
            string previousAttempt,
            List<string> validationErrors)
        {
            var sb = new StringBuilder();
            
            sb.AppendLine("=== GERAÇÃO INCREMENTAL DE LINHA ===");
            sb.AppendLine();
            sb.AppendLine($"LINHA: {lineName}");
            sb.AppendLine($"REGISTRO: {recordIndex + 1}");
            sb.AppendLine($"OCORRÊNCIA: {occurrence + 1}");
            sb.AppendLine($"TAMANHO EXATO: {limitOfChars} caracteres");
            sb.AppendLine();

            // Se houve tentativa anterior com erros, incluir feedback
            if (!string.IsNullOrEmpty(previousAttempt) && validationErrors != null && validationErrors.Any())
            {
                sb.AppendLine("=== CORREÇÃO NECESSÁRIA ===");
                sb.AppendLine("A tentativa anterior falhou na validação:");
                sb.AppendLine($"Tentativa anterior: '{previousAttempt}' ({previousAttempt.Length} chars)");
                sb.AppendLine("ERROS ENCONTRADOS:");
                foreach (var error in validationErrors)
                {
                    sb.AppendLine($"  - {error}");
                }
                sb.AppendLine();
                sb.AppendLine("CORRIJA os erros acima e gere uma nova linha válida.");
                sb.AppendLine();
            }

            // Estrutura da linha
            var initialValue = lineElementXml.Element("InitialValue")?.Value ?? "";
            if (!string.IsNullOrEmpty(initialValue))
            {
                sb.AppendLine($"VALOR INICIAL: '{initialValue}' ({initialValue.Length} chars)");
                sb.AppendLine();
            }

            // Campos da linha
            var fields = lineElementXml.Element("Elements")?.Elements("Element") ?? Enumerable.Empty<XElement>();
            var fieldList = new List<(string name, int sequence, int start, int length, string alignment, bool required)>();
            
            int currentPos = string.IsNullOrEmpty(initialValue) ? 0 : initialValue.Length;
            bool isHeader = lineName.Equals("HEADER", StringComparison.OrdinalIgnoreCase);
            if (!isHeader)
            {
                currentPos = 6; // Sequencia da linha anterior
            }

            foreach (var fieldElement in fields)
            {
                var fieldName = fieldElement.Element("Name")?.Value;
                if (fieldName == null || fieldName.Equals("Sequencia", StringComparison.OrdinalIgnoreCase))
                    continue;

                var startStr = fieldElement.Element("StartValue")?.Value;
                var lengthStr = fieldElement.Element("LengthField")?.Value;
                var alignment = fieldElement.Element("AlignmentType")?.Value ?? "Left";
                var requiredStr = fieldElement.Element("IsRequired")?.Value ?? "false";

                if (int.TryParse(lengthStr, out var length))
                {
                    fieldList.Add((fieldName, 
                        int.TryParse(fieldElement.Element("Sequence")?.Value, out var seq) ? seq : 0,
                        currentPos, length, alignment, requiredStr == "true"));
                    currentPos += length;
                }
            }

            fieldList = fieldList.OrderBy(f => f.sequence).ToList();

            sb.AppendLine("=== CAMPOS DA LINHA ===");
            foreach (var field in fieldList)
            {
                var fieldKey = $"{lineName}.{field.name}";
                var isPreGenerated = fieldMapping.ContainsKey(fieldKey);
                var preGenMarker = isPreGenerated ? " [PRÉ-GERADO]" : "";
                
                sb.AppendLine($"  [{field.sequence}] {field.name}:");
                sb.AppendLine($"    Posição: {field.start + 1}-{field.start + field.length}");
                sb.AppendLine($"    Tamanho: {field.length} caracteres");
                sb.AppendLine($"    Alinhamento: {field.alignment}");
                sb.AppendLine($"    Obrigatório: {(field.required ? "SIM" : "NÃO")}{preGenMarker}");
                
                if (isPreGenerated)
                {
                    var (_, _, _, _, generatedValue) = fieldMapping[fieldKey];
                    sb.AppendLine($"    Valor pré-gerado: '{generatedValue}'");
                }
                sb.AppendLine();
            }

            // Exemplos relevantes para esta linha
            if (allExamples != null && allExamples.Any())
            {
                sb.AppendLine("=== EXEMPLOS REAIS DESTA LINHA ===");
                foreach (var example in allExamples.Take(2))
                {
                    var exampleLines = example.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    // Procurar linhas que começam com o InitialValue ou têm o mesmo padrão
                    var relevantLines = exampleLines.Where(l => 
                        (!string.IsNullOrEmpty(initialValue) && l.StartsWith(initialValue)) ||
                        (string.IsNullOrEmpty(initialValue) && l.Length == limitOfChars))
                        .Take(2);
                    
                    foreach (var line in relevantLines)
                    {
                        sb.AppendLine($"Exemplo: {line}");
                    }
                }
                sb.AppendLine();
            }

            sb.AppendLine("=== INSTRUÇÕES ===");
            sb.AppendLine($"Gere APENAS UMA linha com EXATAMENTE {limitOfChars} caracteres.");
            sb.AppendLine("A linha deve conter:");
            if (!string.IsNullOrEmpty(initialValue))
            {
                sb.AppendLine($"  1. '{initialValue}' no início");
            }
            sb.AppendLine("  2. Todos os campos na ordem e posição corretas");
            sb.AppendLine("  3. Preenchimento com espaços até completar 600 caracteres");
            sb.AppendLine();
            sb.AppendLine("Retorne APENAS a linha gerada, sem explicações.");

            return sb.ToString();
        }

        /// <summary>
        /// Normaliza o tamanho de uma linha
        /// </summary>
        private string NormalizeLineLength(string line, int targetLength)
        {
            if (string.IsNullOrEmpty(line))
                return new string(' ', targetLength);

            if (line.Length == targetLength)
                return line;

            if (line.Length > targetLength)
                return line.Substring(0, targetLength);

            return line.PadRight(targetLength, ' ');
        }

        /// <summary>
        /// Converte XElement para LineElement (simplificado)
        /// </summary>
        private Models.Entities.LineElement ParseLineElement(XElement lineElementXml)
        {
            // Criar LineElement básico para validação
            var lineElement = new Models.Entities.LineElement
            {
                Name = lineElementXml.Element("Name")?.Value ?? "Unknown",
                InitialValue = lineElementXml.Element("InitialValue")?.Value ?? "",
                Elements = new List<string>()
            };

            // Adicionar campos como JSON strings
            var fields = lineElementXml.Element("Elements")?.Elements("Element") ?? Enumerable.Empty<XElement>();
            foreach (var fieldXml in fields)
            {
                var field = new Models.Entities.FieldElement
                {
                    Name = fieldXml.Element("Name")?.Value,
                    Sequence = int.TryParse(fieldXml.Element("Sequence")?.Value, out var seq) ? seq : 0,
                    StartValue = int.TryParse(fieldXml.Element("StartValue")?.Value, out var start) ? start : 0,
                    LengthField = int.TryParse(fieldXml.Element("LengthField")?.Value, out var length) ? length : 0,
                    AlignmentType = Enum.TryParse<AlignmentType>(fieldXml.Element("AlignmentType")?.Value, out var align) ? align : AlignmentType.Left,
                    IsRequired = bool.TryParse(fieldXml.Element("IsRequired")?.Value, out var req) && req
                };

                lineElement.Elements.Add(JsonConvert.SerializeObject(field));
            }

            return lineElement;
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
