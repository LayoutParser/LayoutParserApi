using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using LayoutParserApi.Models.Learning;

namespace LayoutParserApi.Services.Learning
{
    /// <summary>
    /// Serviço de aprendizado de layout usando análise de padrões (ML leve local)
    /// </summary>
    public class LayoutLearningService
    {
        private readonly ILogger<LayoutLearningService> _logger;

        public LayoutLearningService(ILogger<LayoutLearningService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Aprende estrutura de um arquivo de texto posicional
        /// </summary>
        public async Task<LearningResult> LearnFromFileAsync(string filePath, string fileType)
        {
            var startTime = DateTime.Now;
            var result = new LearningResult();

            try
            {
                _logger.LogInformation("Iniciando aprendizado de layout para arquivo: {Path}", filePath);

                var content = await File.ReadAllTextAsync(filePath, Encoding.UTF8);
                var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .ToList();

                if (!lines.Any())
                {
                    result.Success = false;
                    result.Message = "Arquivo vazio ou sem linhas válidas";
                    return result;
                }

                LayoutModel model;

                if (fileType.ToLower() == "xml")
                {
                    model = await LearnXmlStructureAsync(lines, filePath);
                }
                else
                {
                    model = await LearnTextPositionalStructureAsync(lines, filePath);
                }

                result.Success = true;
                result.LearnedModel = model;
                result.Message = $"Layout aprendido com sucesso: {model.TotalFields} campos detectados";
                result.ProcessingTime = DateTime.Now - startTime;

                _logger.LogInformation("Aprendizado concluído: {Fields} campos, {Time}ms", 
                    model.TotalFields, result.ProcessingTime.TotalMilliseconds);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao aprender layout");
                result.Success = false;
                result.Message = $"Erro: {ex.Message}";
                result.ProcessingTime = DateTime.Now - startTime;
                return result;
            }
        }

        /// <summary>
        /// Aprende estrutura de arquivo de texto posicional
        /// </summary>
        private async Task<LayoutModel> LearnTextPositionalStructureAsync(List<string> lines, string filePath)
        {
            var model = new LayoutModel
            {
                FileName = Path.GetFileName(filePath),
                FilePath = filePath,
                FileType = "txt",
                TotalLines = lines.Count,
                LearnedAt = DateTime.Now
            };

            // Analisar primeira linha para detectar tamanho padrão
            var firstLine = lines.First();
            model.LineLength = firstLine.Length;

            // Detectar padrões de quebra de linha (HEADER, LINHA000, etc.)
            var linePatterns = DetectLinePatterns(lines);
            
            // Para cada tipo de linha, detectar campos
            foreach (var linePattern in linePatterns)
            {
                var lineSamples = lines.Where(l => linePattern.IsMatch(l)).Take(100).ToList();
                var fields = DetectFieldsInLine(lineSamples, linePattern.Name);
                
                model.Fields.AddRange(fields);
            }

            model.TotalFields = model.Fields.Count;
            model.Statistics = CalculateStatistics(model, lines);

            return model;
        }

        /// <summary>
        /// Aprende estrutura de arquivo XML
        /// </summary>
        private async Task<LayoutModel> LearnXmlStructureAsync(List<string> lines, string filePath)
        {
            var model = new LayoutModel
            {
                FileName = Path.GetFileName(filePath),
                FilePath = filePath,
                FileType = "xml",
                TotalLines = lines.Count,
                LearnedAt = DateTime.Now
            };

            var xmlContent = string.Join("\n", lines);
            
            // Parse XML básico
            var doc = XDocument.Parse(xmlContent);
            
            // Extrair elementos e atributos
            var fields = ExtractXmlFields(doc.Root, "");
            model.Fields = fields;
            model.TotalFields = fields.Count;
            model.Statistics = CalculateStatistics(model, lines);

            return model;
        }

        /// <summary>
        /// Detecta padrões de linhas (HEADER, LINHA000, etc.)
        /// </summary>
        private List<LinePattern> DetectLinePatterns(List<string> lines)
        {
            var patterns = new List<LinePattern>();
            var lineGroups = new Dictionary<string, List<string>>();

            // Agrupar linhas por prefixo comum
            foreach (var line in lines.Take(1000)) // Limitar para performance
            {
                var prefix = line.Length > 10 ? line.Substring(0, 10) : line;
                
                if (!lineGroups.ContainsKey(prefix))
                    lineGroups[prefix] = new List<string>();
                
                lineGroups[prefix].Add(line);
            }

            // Criar padrões para grupos significativos
            foreach (var group in lineGroups.Where(g => g.Value.Count >= 3))
            {
                var pattern = new LinePattern
                {
                    Name = DetectLineName(group.Key),
                    Prefix = group.Key,
                    SampleCount = group.Value.Count
                };
                patterns.Add(pattern);
            }

            return patterns;
        }

        private string DetectLineName(string prefix)
        {
            if (prefix.StartsWith("HEADER", StringComparison.OrdinalIgnoreCase))
                return "HEADER";
            if (prefix.StartsWith("LINHA", StringComparison.OrdinalIgnoreCase))
                return prefix.Substring(0, Math.Min(10, prefix.Length));
            if (prefix.StartsWith("TRAILER", StringComparison.OrdinalIgnoreCase))
                return "TRAILER";
            
            return "UNKNOWN";
        }

        /// <summary>
        /// Detecta campos em uma linha usando análise de padrões
        /// </summary>
        private List<FieldDefinition> DetectFieldsInLine(List<string> lineSamples, string lineName)
        {
            var fields = new List<FieldDefinition>();
            
            if (!lineSamples.Any())
                return fields;

            var lineLength = lineSamples.First().Length;
            var fieldCandidates = new List<FieldCandidate>();

            // Analisar cada posição para detectar campos
            for (int pos = 0; pos < lineLength; pos++)
            {
                var columnValues = lineSamples.Select(l => pos < l.Length ? l[pos] : ' ').ToList();
                
                // Detectar transições (mudança de padrão)
                if (DetectFieldTransition(columnValues, pos, lineSamples))
                {
                    // Campo potencial encontrado
                    var field = AnalyzeFieldAtPosition(lineSamples, pos, lineName, fields.Count + 1);
                    if (field != null)
                    {
                        fields.Add(field);
                        pos = field.EndPosition; // Pular para o final do campo
                    }
                }
            }

            return fields;
        }

        private bool DetectFieldTransition(List<char> columnValues, int position, List<string> samples)
        {
            // Detectar mudança de padrão (espaço para não-espaço, número para letra, etc.)
            if (position == 0)
                return true;

            var currentChars = columnValues;
            var prevChars = samples.Select(l => position > 0 && position - 1 < l.Length ? l[position - 1] : ' ').ToList();

            // Se há mudança significativa no padrão
            var currentIsSpace = currentChars.All(c => char.IsWhiteSpace(c));
            var prevIsSpace = prevChars.All(c => char.IsWhiteSpace(c));

            if (currentIsSpace != prevIsSpace)
                return true;

            // Detectar mudança de tipo (número para letra, etc.)
            var currentIsDigit = currentChars.All(c => char.IsDigit(c));
            var prevIsDigit = prevChars.All(c => char.IsDigit(c));

            if (currentIsDigit != prevIsDigit)
                return true;

            return false;
        }

        private FieldDefinition AnalyzeFieldAtPosition(List<string> samples, int startPos, string lineName, int sequence)
        {
            // Encontrar fim do campo (próxima transição ou fim da linha)
            int endPos = startPos;
            var fieldValues = new List<string>();

            for (int i = startPos; i < samples.First().Length; i++)
            {
                var columnValues = samples.Select(s => i < s.Length ? s[i] : ' ').ToList();
                
                // Se todas são espaços, pode ser fim do campo
                if (columnValues.All(c => char.IsWhiteSpace(c)) && i > startPos)
                {
                    endPos = i - 1;
                    break;
                }

                endPos = i;
            }

            if (endPos <= startPos)
                return null;

            // Extrair valores do campo
            foreach (var sample in samples)
            {
                if (startPos < sample.Length && endPos < sample.Length)
                {
                    var value = sample.Substring(startPos, Math.Min(endPos - startPos + 1, sample.Length - startPos)).Trim();
                    if (!string.IsNullOrEmpty(value))
                        fieldValues.Add(value);
                }
            }

            if (!fieldValues.Any())
                return null;

            // Detectar tipo de dado
            var dataType = DetectDataType(fieldValues);
            var pattern = DetectPattern(fieldValues);

            return new FieldDefinition
            {
                Name = $"Field_{sequence}",
                StartPosition = startPos,
                EndPosition = endPos,
                DataType = dataType,
                Alignment = DetectAlignment(fieldValues),
                IsRequired = fieldValues.All(v => !string.IsNullOrWhiteSpace(v)),
                SampleValues = fieldValues.Distinct().Take(10).ToList(),
                Pattern = pattern,
                Confidence = CalculateConfidence(fieldValues, dataType),
                LineName = lineName,
                Sequence = sequence
            };
        }

        private string DetectDataType(List<string> values)
        {
            var samples = values.Take(20).ToList();

            // CNPJ
            if (samples.All(v => v.Length == 14 && v.All(char.IsDigit)))
                return "cnpj";

            // CPF
            if (samples.All(v => v.Length == 11 && v.All(char.IsDigit)))
                return "cpf";

            // Data (yyyyMMdd)
            if (samples.All(v => v.Length == 8 && Regex.IsMatch(v, @"^\d{8}$")))
                return "date";

            // Decimal
            if (samples.All(v => decimal.TryParse(v.Replace(",", "."), out _)))
                return "decimal";

            // Int
            if (samples.All(v => int.TryParse(v, out _)))
                return "int";

            // Email
            if (samples.Any(v => v.Contains("@") && v.Contains(".")))
                return "email";

            return "string";
        }

        private string DetectPattern(List<string> values)
        {
            if (!values.Any())
                return "";

            var first = values.First();
            var pattern = new StringBuilder();

            foreach (var c in first)
            {
                if (char.IsDigit(c))
                    pattern.Append("\\d");
                else if (char.IsLetter(c))
                    pattern.Append("\\w");
                else
                    pattern.Append(Regex.Escape(c.ToString()));
            }

            return pattern.ToString();
        }

        private string DetectAlignment(List<string> values)
        {
            // Se valores têm espaços à esquerda, provavelmente alinhado à direita
            var leftPadded = values.Count(v => v.Length > 0 && char.IsWhiteSpace(v[0]));
            var rightPadded = values.Count(v => v.Length > 0 && char.IsWhiteSpace(v[v.Length - 1]));

            if (leftPadded > rightPadded)
                return "Right";
            if (rightPadded > leftPadded)
                return "Left";
            
            return "Left";
        }

        private double CalculateConfidence(List<string> values, string dataType)
        {
            if (!values.Any())
                return 0.0;

            var validCount = 0;
            var totalCount = values.Count;

            switch (dataType)
            {
                case "cnpj":
                case "cpf":
                case "date":
                case "int":
                case "decimal":
                    validCount = values.Count(v => !string.IsNullOrWhiteSpace(v));
                    break;
                default:
                    validCount = totalCount;
                    break;
            }

            return (double)validCount / totalCount;
        }

        private List<FieldDefinition> ExtractXmlFields(XElement element, string parentPath)
        {
            var fields = new List<FieldDefinition>();
            var currentPath = string.IsNullOrEmpty(parentPath) ? element.Name.LocalName : $"{parentPath}.{element.Name.LocalName}";

            // Adicionar atributos como campos
            foreach (var attr in element.Attributes())
            {
                fields.Add(new FieldDefinition
                {
                    Name = $"{currentPath}.{attr.Name.LocalName}",
                    StartPosition = 0,
                    EndPosition = attr.Value.Length - 1,
                    DataType = DetectDataType(new List<string> { attr.Value }),
                    SampleValues = new List<string> { attr.Value },
                    Confidence = 1.0,
                    LineName = currentPath
                });
            }

            // Adicionar valor do elemento se não tiver filhos
            if (!element.Elements().Any() && !string.IsNullOrWhiteSpace(element.Value))
            {
                fields.Add(new FieldDefinition
                {
                    Name = currentPath,
                    StartPosition = 0,
                    EndPosition = element.Value.Length - 1,
                    DataType = DetectDataType(new List<string> { element.Value }),
                    SampleValues = new List<string> { element.Value },
                    Confidence = 1.0,
                    LineName = currentPath
                });
            }

            // Processar elementos filhos
            foreach (var child in element.Elements())
            {
                fields.AddRange(ExtractXmlFields(child, currentPath));
            }

            return fields;
        }

        private LearningStatistics CalculateStatistics(LayoutModel model, List<string> lines)
        {
            var stats = new LearningStatistics
            {
                TotalSamples = lines.Count,
                ValidSamples = lines.Count(l => l.Length == model.LineLength || model.FileType == "xml"),
                InvalidSamples = lines.Count(l => l.Length != model.LineLength && model.FileType != "xml")
            };

            stats.Accuracy = stats.TotalSamples > 0 ? (double)stats.ValidSamples / stats.TotalSamples : 0;

            // Distribuição de tipos de dados
            foreach (var field in model.Fields)
            {
                if (!stats.DataTypeDistribution.ContainsKey(field.DataType))
                    stats.DataTypeDistribution[field.DataType] = 0;
                stats.DataTypeDistribution[field.DataType]++;

                stats.FieldConfidence[field.Name] = field.Confidence;
            }

            // Padrões detectados
            stats.DetectedPatterns = model.Fields
                .Where(f => !string.IsNullOrEmpty(f.Pattern))
                .Select(f => f.Pattern)
                .Distinct()
                .ToList();

            return stats;
        }
    }

    internal class LinePattern
    {
        public string Name { get; set; }
        public string Prefix { get; set; }
        public int SampleCount { get; set; }
        public bool IsMatch(string line) => line.StartsWith(Prefix);
    }

    internal class FieldCandidate
    {
        public int StartPosition { get; set; }
        public int EndPosition { get; set; }
        public List<string> Values { get; set; } = new();
    }
}

