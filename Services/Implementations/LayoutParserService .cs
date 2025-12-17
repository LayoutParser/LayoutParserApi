using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Enums;
using LayoutParserApi.Models.Logging;
using LayoutParserApi.Models.Parsing;
using LayoutParserApi.Models.Responses;
using LayoutParserApi.Models.Structure;
using LayoutParserApi.Models.Summaries;
using LayoutParserApi.Models.Validation;
using LayoutParserApi.Models.Configuration;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Parsing.Interfaces;
using LayoutParserApi.Services.Validation;

using Newtonsoft.Json;

using System.Text;
using System.Xml.Linq;

namespace LayoutParserApi.Services.Implementations
{
    public class LayoutParserService : ILayoutParserService
    {
        private readonly IAuditLogger _auditLogger;
        private readonly ITechLogger _techLogger;
        private readonly ILineSplitter _lineSplitter;
        private readonly ILayoutValidator _layoutValidator;
        private readonly ILayoutNormalizer _layoutNormalizer;
        private readonly DocumentValidationService _documentValidationService;
        private readonly DocumentMLValidationService _mlValidationService;
        private readonly ILogger<LayoutParserService> _logger;

        public LayoutParserService(
            ITechLogger techLogger, 
            IAuditLogger auditLogger, 
            ILineSplitter lineSplitter, 
            ILayoutValidator layoutValidator, 
            ILayoutNormalizer layoutNormalizer,
            DocumentValidationService documentValidationService,
            DocumentMLValidationService mlValidationService,
            ILogger<LayoutParserService> logger)
        {
            _techLogger = techLogger;
            _auditLogger = auditLogger;
            _lineSplitter = lineSplitter;
            _layoutValidator = layoutValidator;
            _layoutNormalizer = layoutNormalizer;
            _documentValidationService = documentValidationService;
            _mlValidationService = mlValidationService;
            _logger = logger;
        }

        public async Task<ParsingResult> ParseAsync(Stream layoutStream, Stream txtStream)
        {
            var result = new ParsingResult();

            try
            {
                result.Layout = await LoadLayoutAsync(layoutStream);
                result.RawText = await ReadTextFileWithEncoding(txtStream);

                _layoutValidator.ValidateCompleteLayout(result.Layout);

                // ✅ VALIDAÇÃO AUTOMÁTICA DO DOCUMENTO - Valida se todas as linhas têm 600 posições
                var documentValidation = _documentValidationService.ValidateDocument(result.RawText);
                
                if (!documentValidation.IsValid)
                {
                    _logger.LogWarning("Documento TXT inválido: {ErrorMessage}. {ErrorCount} erro(s) encontrado(s).", 
                        documentValidation.ErrorMessage, documentValidation.LineErrors.Count);

                    // Registrar para aprendizado ML (em background, não bloqueia)
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _mlValidationService.LearnFromDocumentAsync(
                                result.RawText,
                                result.Layout?.LayoutGuid ?? "",
                                documentValidation.LineErrors);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Erro ao registrar documento para aprendizado ML");
                        }
                    });

                    // Retornar erro detalhado
                    result.Success = false;
                    result.ErrorMessage = $"Documento inválido: {documentValidation.ErrorMessage}";
                    
                    // Adicionar informações de validação ao resultado para o front-end
                    result.ValidationErrors = documentValidation.LineErrors.Select(e => new DocumentValidationErrorInfo
                    {
                        LineIndex = e.LineIndex,
                        Sequence = e.Sequence,
                        ExpectedLength = e.ExpectedLength,
                        ActualLength = e.ActualLength,
                        ErrorMessage = e.ErrorMessage,
                        StartPosition = e.StartPosition,
                        EndPosition = e.EndPosition
                    }).ToList();

                    return result;
                }

                // Se documento é válido, processar normalmente
                result.ParsedFields = ParseTextWithSequenceValidation(result.RawText, result.Layout);
                result.Summary = CalculateSummary(result);
                result.Success = true;

                // Registrar documento válido para aprendizado ML (em background)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _mlValidationService.LearnFromDocumentAsync(
                            result.RawText,
                            result.Layout?.LayoutGuid ?? "",
                            null); // Sem erros
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Erro ao registrar documento válido para aprendizado ML");
                    }
                });
            }
            catch (Exception ex)
            {
                _auditLogger.LogAudit(new AuditLogEntry
                {
                    UserId = "sistema",
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "LayoutParserService.ParseAsync",
                    Action = "ParseAsync",
                    Details = $"Erro no parsing: {ex.Message}"
                });

                result.Success = false;
                result.ErrorMessage = $"Erro no parsing: {ex.Message}";
            }

            return result;
        }

        private List<ParsedField> ParseTextWithSequenceValidation(string text, Layout layout)
        {
            Layout completedLayout = layout;

            var parsedFields = new List<ParsedField>();

            if (string.IsNullOrEmpty(text) || layout?.Elements == null)
                return parsedFields;

            // Para IDOC, usar quebras de linha ao invés de tamanho fixo
            // Verificar se o texto parece ser IDOC (começa com EDI_DC40 ou ZRSDM_NFE)
            var detectedType = "TextPositional"; // Padrão
            if (!string.IsNullOrEmpty(text))
            {
                var trimmedText = text.TrimStart();
                if (trimmedText.StartsWith("EDI_DC40", StringComparison.OrdinalIgnoreCase) ||
                    trimmedText.StartsWith("ZRSDM_NFE", StringComparison.OrdinalIgnoreCase))
                {
                    detectedType = "idoc";
                }
            }
            
            // Usar detectedType para IDOC, senão usar layoutType do banco
            var splitType = detectedType == "idoc" ? "idoc" : layout.LayoutType;
            var lines = _lineSplitter.SplitTextIntoLines(text, splitType);

            _techLogger.LogTechnical(new TechLogEntry
            {
                RequestId = Guid.NewGuid().ToString(),
                Endpoint = "ParseTextWithSequenceValidation",
                Level = "Info",
                Message = $"Total de linhas no arquivo: {lines.Length}"
            });

            List<string> unidentifiedLines = new List<string>();

            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                int expectedLength = 600;
                LineElement matchingLineConfig = null;

                if (layout?.Elements != null)
                {

                    var allLineElements = new List<LineElement>();
                    var allFieldElements = new List<FieldElement>();

                    foreach (var element in layout.Elements)
                    {
                        CollectAllElements(element, allLineElements, allFieldElements);
                    }

                    // completedLayout agora contém todos os LineElement e FieldElement do layout, inclusive os aninhados
                    // Você pode usar allLineElements para busca de configuração de linha e allFieldElements para busca de campos

                    // Exemplo de uso para busca mais precisa:
                    completedLayout = new Layout
                    {
                        LayoutGuid = layout.LayoutGuid,
                        LayoutType = layout.LayoutType,
                        Name = layout.Name,
                        Description = layout.Description,
                        LimitOfCaracters = layout.LimitOfCaracters,
                        Elements = allLineElements
                    };

                    matchingLineConfig = FindMatchingLineConfigRecursive(lines[lineIndex], layout.Elements);
                    if (matchingLineConfig != null)
                    {
                        expectedLength = SumLengthFieldFromFieldElements(matchingLineConfig);
                        if (expectedLength <= 0)
                            expectedLength = 600;
                    }
                }

                string currentLine = lines[lineIndex].Length > expectedLength ? lines[lineIndex].Substring(0, expectedLength) : lines[lineIndex].PadRight(expectedLength);

                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ParseTextWithSequenceValidation",
                    Level = "Info",
                    Message = $"=== Processando Linha {lineIndex + 1} ==="
                });

                // Identificar qual configuração de linha corresponde a esta linha

                if (matchingLineConfig != null)
                {
                    _techLogger.LogTechnical(new TechLogEntry
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        Endpoint = "ParseTextWithSequenceValidation",
                        Level = "Info",
                        Message = $"Linha corresponde a: {matchingLineConfig.Name}"
                    });

                    int currentOccurrence = parsedFields.Count(f => f.LineName == matchingLineConfig.Name);
                    if (currentOccurrence < matchingLineConfig.MaximumOccurrence)
                    {
                        ParseLineFields(currentLine, matchingLineConfig, parsedFields, currentOccurrence);
                    }
                    else
                    {
                        _techLogger.LogTechnical(new TechLogEntry
                        {
                            RequestId = Guid.NewGuid().ToString(),
                            Endpoint = "ParseTextWithSequenceValidation",
                            Level = "Warn",
                            Message = $"Limite de {matchingLineConfig.MaximumOccurrence} ocorrências atingido para {matchingLineConfig.Name}"
                        });
                    }
                }
                else
                {
                    // CORREÇÃO: Registrar linha não identificada
                    string linePreview = currentLine.Substring(0, Math.Min(20, currentLine.Length));
                    unidentifiedLines.Add($"Linha {lineIndex + 1}: {linePreview}...");

                    _techLogger.LogTechnical(new TechLogEntry
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        Endpoint = "ParseTextWithSequenceValidation",
                        Level = "Warn",
                        Message = $"Linha {lineIndex + 1} não identificada: {linePreview}..."
                    });
                }

            }

            // CORREÇÃO: Log de linhas não identificadas
            if (unidentifiedLines.Any())
            {
                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ParseTextWithSequenceValidation",
                    Level = "Warn",
                    Message = $"LINHAS NÃO IDENTIFICADAS ({unidentifiedLines.Count}): {string.Join("; ", unidentifiedLines)}"
                });
            }

            // Verificar ocorrências mínimas
            foreach (var lineConfig in layout.Elements)
            {
                int actualOccurrences = parsedFields.Count(f => f.LineName == lineConfig.Name);
                if (actualOccurrences < lineConfig.MinimalOccurrence)
                {
                    _techLogger.LogTechnical(new TechLogEntry
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        Endpoint = "ParseTextWithSequenceValidation",
                        Level = "Warn",
                        Message = $"AVISO: {lineConfig.Name} tem apenas {actualOccurrences} ocorrências (mínimo: {lineConfig.MinimalOccurrence})"
                    });
                }
            }

            return parsedFields;
        }

        // split moved to ILineSplitter

        // NOVO MÉTODO: Dividir texto em linhas de tamanho fixo
        // MÉTODO CORRIGIDO: Dividir texto em linhas de 600 caracteres, mas processando apenas 594 + 6 da sequência
        // fixed-length split moved to ILineSplitter

        LineElement RecursiveSearch(string currentLine, List<LineElement> configs)
        {
            if (configs == null || !configs.Any())
                return null;


            // Verifica se o LineElement possui apenas FieldElement
            bool onlyFields = true;
            bool hasLineElement = false;

            foreach (var config in configs)
            {
                // Filtra apenas elementos do tipo LineElement
                var nestedLines = config.Elements
                    .Where(e =>
                    {
                        try
                        {
                            var type = JsonConvert.DeserializeObject<dynamic>(e)?.Type;
                            return type != null && type.ToString().Contains("LineElement");
                        }
                        catch
                        {
                            return false;
                        }
                    })
                    .Select(e =>
                    {
                        try
                        {
                            return JsonConvert.DeserializeObject<LineElement>(e);
                        }
                        catch
                        {
                            return null;
                        }
                    })
                    .Where(l => l != null && !string.IsNullOrEmpty(l.Name)).ToList();

                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "RecursiveSearch",
                    Level = "Debug",
                    Message = $"nestedLines count: {nestedLines.Count}, currentLine: '{currentLine.Substring(0, Math.Min(20, currentLine.Length))}...'"
                });

                if (nestedLines.Any())
                {
                    var found = RecursiveSearch(currentLine, nestedLines);
                    if (found != null)
                        return found;
                }

                // Se nenhum filho bateu, valida o pai
                if (IsLineValidForConfig(currentLine, config))
                    return config;

            }
            return null;
        }

        private bool IsLineValidForConfig(string line, LineElement lineConfig)
        {
            //DebugLineParsing(line, lineConfig);

            // ✅ CASO ESPECIAL: LINHA999999 (não tem InitialValue, identifica pela sequência)
            if (lineConfig.Name?.Equals("LINHA999999", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (line.Length >= 6 && line.StartsWith("999999"))
                {
                    _techLogger.LogTechnical(new TechLogEntry
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        Endpoint = "IsLineValidForConfig",
                        Level = "Info",
                        Message = $"✅ Match por sequência especial: LINHA999999 (sequência '999999')"
                    });
                    return true;
                }
            }

            // Verificar por InitialValue (HEADER, LINHA000, LINHA001, etc.)
            if (!string.IsNullOrEmpty(lineConfig.InitialValue))
            {
                // CORREÇÃO: Para HEADER, verificar no início absoluto
                if (lineConfig.InitialValue == "HEADER")
                {
                    bool matches = line.StartsWith(lineConfig.InitialValue);
                    if (matches)
                    {
                        _techLogger.LogTechnical(new TechLogEntry
                        {
                            RequestId = Guid.NewGuid().ToString(),
                            Endpoint = "IsLineValidForConfig",
                            Level = "Info",
                            Message = $"Match por InitialValue: '{lineConfig.InitialValue}'"
                        });
                    }
                    return matches;
                }
                // CORREÇÃO: Para IDOC (EDI_DC40, ZRSDM_NFE_400_*), verificar no início absoluto
                else if (lineConfig.InitialValue.StartsWith("EDI_", StringComparison.OrdinalIgnoreCase) ||
                         lineConfig.InitialValue.StartsWith("ZRSDM_", StringComparison.OrdinalIgnoreCase))
                {
                    // Para IDOC, verificar se a linha começa exatamente com o InitialValue
                    bool matches = line.StartsWith(lineConfig.InitialValue, StringComparison.OrdinalIgnoreCase);
                    
                    if (matches)
                    {
                        _techLogger.LogTechnical(new TechLogEntry
                        {
                            RequestId = Guid.NewGuid().ToString(),
                            Endpoint = "IsLineValidForConfig",
                            Level = "Info",
                            Message = $"Match por InitialValue IDOC: '{lineConfig.InitialValue}' para {lineConfig.Name}"
                        });
                    }
                    else
                    {
                        // Para IDOC com ZRSDM_NFE_400_*, verificar se a linha começa com o prefixo e contém o segmento
                        // Exemplo: InitialValue="ZRSDM_NFE_400_IDE000" -> linha "ZRSDM_NFE_400_IDE000          6100000000..."
                        if (lineConfig.InitialValue.StartsWith("ZRSDM_NFE_400_", StringComparison.OrdinalIgnoreCase))
                        {
                            // Extrair o segmento esperado (ex: "IDE000")
                            var expectedSegment = lineConfig.InitialValue.Substring("ZRSDM_NFE_400_".Length);
                            // Remover zeros finais para comparação flexível (ex: "IDE000" -> "IDE")
                            var expectedSegmentBase = expectedSegment.TrimEnd('0');
                            if (string.IsNullOrEmpty(expectedSegmentBase))
                                expectedSegmentBase = expectedSegment;
                            
                            // Verificar se a linha começa com ZRSDM_NFE_400_ seguido do segmento esperado
                            if (line.StartsWith("ZRSDM_NFE_400_", StringComparison.OrdinalIgnoreCase))
                            {
                                var afterPrefix = line.Substring("ZRSDM_NFE_400_".Length);
                                var actualSegment = afterPrefix.IndexOfAny(new[] { ' ', '\t' }) > 0
                                    ? afterPrefix.Substring(0, afterPrefix.IndexOfAny(new[] { ' ', '\t' }))
                                    : afterPrefix;
                                
                                var actualSegmentBase = actualSegment.TrimEnd('0');
                                if (string.IsNullOrEmpty(actualSegmentBase))
                                    actualSegmentBase = actualSegment;
                                
                                matches = actualSegmentBase.Equals(expectedSegmentBase, StringComparison.OrdinalIgnoreCase) ||
                                         actualSegment.StartsWith(expectedSegmentBase, StringComparison.OrdinalIgnoreCase);
                                
                                if (matches)
                                {
                                    _techLogger.LogTechnical(new TechLogEntry
                                    {
                                        RequestId = Guid.NewGuid().ToString(),
                                        Endpoint = "IsLineValidForConfig",
                                        Level = "Info",
                                        Message = $"Match por InitialValue IDOC (segmento flexível): esperado '{expectedSegmentBase}', encontrado '{actualSegmentBase}' para {lineConfig.Name}"
                                    });
                                }
                            }
                        }
                    }
                    
                    return matches;
                }
                else
                {
                    // CORREÇÃO: Para LINHA000, LINHA001, etc., verificar após a sequência
                    // Estrutura: Sequencia(6) + InitialValue(3) + campos
                    if (line.Length >= 6 && IsNumericSequence(line.Substring(0, 6)))
                    {
                        // Verificar se após a sequência (6 caracteres) temos o InitialValue
                        int checkPosition = 6;
                        if (checkPosition + lineConfig.InitialValue.Length <= line.Length)
                        {
                            string actualValue = line.Substring(checkPosition, lineConfig.InitialValue.Length);
                            bool matches = actualValue == lineConfig.InitialValue;

                            if (matches)
                            {
                                _techLogger.LogTechnical(new TechLogEntry
                                {
                                    RequestId = Guid.NewGuid().ToString(),
                                    Endpoint = "IsLineValidForConfig",
                                    Level = "Info",
                                    Message = $"✅ Match por InitialValue: '{lineConfig.InitialValue}' na posição {checkPosition + 1} para {lineConfig.Name}"
                                });
                            }
                            else
                            {
                                _techLogger.LogTechnical(new TechLogEntry
                                {
                                    RequestId = Guid.NewGuid().ToString(),
                                    Endpoint = "IsLineValidForConfig",
                                    Level = "Debug",
                                    Message = $"❌ InitialValue não corresponde: esperado '{lineConfig.InitialValue}', encontrado '{actualValue}' na posição 7-{checkPosition + lineConfig.InitialValue.Length} para {lineConfig.Name}"
                                });
                            }
                            return matches;
                        }
                    }
                    else
                    {
                        _techLogger.LogTechnical(new TechLogEntry
                        {
                            RequestId = Guid.NewGuid().ToString(),
                            Endpoint = "IsLineValidForConfig",
                            Level = "Debug",
                            Message = $"Linha não começa com sequência numérica. Primeiros 6 chars: '{line.Substring(0, Math.Min(6, line.Length))}' para {lineConfig.Name}"
                        });
                    }
                }
            }

            // Se chegou aqui, não fez match
            if (line.Length >= 6 && IsNumericSequence(line.Substring(0, 6)))
            {
                string sequence = line.Substring(0, 6);
                
                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "IsLineValidForConfig",
                    Level = "Debug",
                    Message = $"Nenhum match para {lineConfig.Name}. Sequência: '{sequence}', InitialValue esperado: '{lineConfig.InitialValue}'"
                });

                // ❌ REMOVER: Lógica antiga que tentava fazer match por substring da sequência
                bool matchesByName = false;

                if (matchesByName)
                {
                    _techLogger.LogTechnical(new TechLogEntry
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        Endpoint = "IsLineValidForConfig",
                        Level = "Info",
                        Message = $"Match por nome: {lineConfig.Name} contém sequência '{sequence}'"
                    });
                    return true;
                }

                // Verificação genérica por estrutura
                bool matchesByStructure = DoesLineConfigMatchSequence(lineConfig, sequence, line);
                if (matchesByStructure)
                {
                    _techLogger.LogTechnical(new TechLogEntry
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        Endpoint = "IsLineValidForConfig",
                        Level = "Info",
                        Message = $"Match por estrutura para sequência '{sequence}' em {lineConfig.Name}"
                    });
                }
                return matchesByStructure;
            }

            _techLogger.LogTechnical(new TechLogEntry
            {
                RequestId = Guid.NewGuid().ToString(),
                Endpoint = "IsLineValidForConfig",
                Level = "Debug",
                Message = $"Nenhum critério atendido para {lineConfig.Name}"
            });

            return false;
        }

        // CORREÇÃO: Método auxiliar para verificar correspondência por sequência
        private bool DoesLineConfigMatchSequence(LineElement lineConfig, string sequence, string line)
        {
            // Para LINHA000, LINHA001, etc., verificar se a sequência corresponde
            if (lineConfig.Name.StartsWith("LINHA"))
            {
                string expectedSuffix = lineConfig.Name.Substring(5).PadLeft(3, '0'); // "000", "001", etc.
                string actualSuffix = sequence.Substring(3); // Últimos 3 dígitos da sequência

                bool suffixMatches = actualSuffix == expectedSuffix;

                if (suffixMatches)
                {
                    _techLogger.LogTechnical(new TechLogEntry
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        Endpoint = "DoesLineConfigMatchSequence",
                        Level = "Info",
                        Message = $"Sequência '{sequence}' corresponde ao sufixo '{expectedSuffix}' de {lineConfig.Name}"
                    });
                }

                return suffixMatches;
            }

            // Para configurações genéricas sem InitialValue específico
            return string.IsNullOrEmpty(lineConfig.InitialValue) &&
                   lineConfig.MaximumOccurrence > 1;
        }

        private bool IsNumericSequence(string sequence)
        {
            return sequence.Length == 6 && sequence.All(char.IsDigit) && int.TryParse(sequence, out _);
        }

        private void ParseLineFields(string line, LineElement lineConfig, List<ParsedField> parsedFields, int occurrenceIndex)
        {
            _techLogger.LogTechnical(new TechLogEntry
            {
                RequestId = Guid.NewGuid().ToString(),
                Endpoint = "ParseTextWithSequenceValidation",
                Level = "Info",
                Message = $"=== Processando: {lineConfig.Name} (Ocorrência {occurrenceIndex + 1}) ==="
            });

            _techLogger.LogTechnical(new TechLogEntry
            {
                RequestId = Guid.NewGuid().ToString(),
                Endpoint = "ParseTextWithSequenceValidation",
                Level = "Info",
                Message = $"Linha completa ({line.Length} chars): {line.Substring(0, Math.Min(100, line.Length))}..."
            });

            string paddedLine = line.PadRight(600);
            
            // ✅ EXTRAIR SEQUENCIAL DAS PRIMEIRAS 6 POSIÇÕES DA LINHA ORIGINAL (ANTES DE QUALQUER PROCESSAMENTO)
            // IMPORTANTE: Extrair diretamente da linha original, não do paddedLine, para garantir que seja o valor correto
            string lineSequence = line.Length >= 6 ? line.Substring(0, 6) : line.PadRight(6).Substring(0, 6);

            // ✅ OBTER CAMPOS ORDENADOS POR SEQUENCE
            var fieldsToProcess = lineConfig.Elements
                .Select(e =>
                {
                    try
                    {
                        return JsonConvert.DeserializeObject<FieldElement>(e);
                    }
                    catch
                    {
                        return null;
                    }
                })
                .Where(f => f != null &&
                           !f.Name.Equals("Sequencia", StringComparison.OrdinalIgnoreCase) &&
                           !f.Name.StartsWith("LINHA"))
                .OrderBy(f => f.Sequence)
                .ToList();

            _techLogger.LogTechnical(new TechLogEntry
            {
                RequestId = Guid.NewGuid().ToString(),
                Endpoint = "ParseTextWithSequenceValidation",
                Level = "Info",
                Message = $"Campos a processar: {fieldsToProcess.Count}"
            });

            // ✅ CALCULAR OFFSET BASE
            int currentPosition = CalculateLineOffset(lineConfig, paddedLine);

            _techLogger.LogTechnical(new TechLogEntry
            {
                RequestId = Guid.NewGuid().ToString(),
                Endpoint = "ParseTextWithSequenceValidation",
                Level = "Info",
                Message = $"Posição inicial dos campos: {currentPosition + 1} (após InitialValue/Sequencia)"
            });

            if (currentPosition > 0)
            {
                string prefix = paddedLine.Substring(0, currentPosition);
                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ParseTextWithSequenceValidation",
                    Level = "Info",
                    Message = $"Prefixo da linha (posições 1-{currentPosition}): '{prefix}'"
                });
            }

            // ✅ PROCESSAR CAMPOS SEQUENCIALMENTE (IGNORAR StartValue DO XML)
            foreach (var field in fieldsToProcess)
            {
                string value = "";
                string status = "ok";

                int fieldStart = currentPosition;
                int endPosition = fieldStart + field.LengthField - 1;

                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ParseTextWithSequenceValidation",
                    Level = "Info",
                    Message = $"{field.Name} → Pos: {fieldStart + 1}-{endPosition + 1} | Length: {field.LengthField} | StartValue do XML: {field.StartValue}"
                });

                if (fieldStart >= 0 && endPosition < paddedLine.Length)
                {
                    value = paddedLine.Substring(fieldStart, field.LengthField);
                    value = ApplyAlignment(value, field.AlignmentType);

                    _techLogger.LogTechnical(new TechLogEntry
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        Endpoint = "ParseTextWithSequenceValidation",
                        Level = "Info",
                        Message = $"  Valor: '{value}'"
                    });

                    if (field.IsRequired && string.IsNullOrWhiteSpace(value))
                        status = "error";
                    else if (value.Length < field.LengthField)
                        status = "warning";
                }
                else
                {
                    _techLogger.LogTechnical(new TechLogEntry
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        Endpoint = "ParseTextWithSequenceValidation",
                        Level = "Warn",
                        Message = $"  ERRO: Posição {fieldStart + 1}-{endPosition + 1} fora da linha"
                    });
                    status = "error";
                }

                parsedFields.Add(new ParsedField
                {
                    LineName = ObterLineNameSemHierarquia(lineConfig.Name),
                    FieldName = field.Name,
                    Sequence = field.Sequence,
                    Start = fieldStart + 1,
                    Length = field.LengthField,
                    Value = value,
                    Status = status,
                    IsRequired = field.IsRequired,
                    Occurrence = occurrenceIndex + 1,
                    LineSequence = lineSequence
                });

                // ✅ ATUALIZAR POSIÇÃO PARA PRÓXIMO CAMPO
                currentPosition = endPosition + 1;

                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ParseTextWithSequenceValidation",
                    Level = "Info",
                    Message = $"  Próxima posição: {currentPosition + 1}"
                });
            }

            FiltrarParsedFields(parsedFields);

            // ✅ VERIFICAR SE CHEGOU A 600
            if (currentPosition != 600)
            {
                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ParseTextWithSequenceValidation",
                    Level = "Warn",
                    Message = $"AVISO: Terminou em {currentPosition}, deveria terminar em 600"
                });
            }
        }

        private int CalculateLineOffset(LineElement lineConfig, string paddedLine)
        {
            if (lineConfig.Name == "HEADER")
            {
                // HEADER: campos começam APÓS "HEADER" (6 caracteres)
                // Estrutura: HEADER + campos (sem Sequencia própria)
                return 6;
            }
            else if (lineConfig.Name?.Equals("LINHA999999", StringComparison.OrdinalIgnoreCase) == true)
            {
                // LINHA999999: não tem InitialValue, campos começam APÓS a sequência (6)
                // Estrutura: Sequencia da linha anterior (6) + campos (sem InitialValue, sem Sequencia própria)
                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "CalculateLineOffset",
                    Level = "Info",
                    Message = $"Linha {lineConfig.Name}: Offset = 6 (Seq. anterior, SEM InitialValue)"
                });
                return 6;
            }
            else if (lineConfig.Name.StartsWith("LINHA"))
            {
                // LINHAS: campos começam APÓS Sequencia da linha anterior (6) + InitialValue
                // Estrutura: Sequencia da linha anterior (6) + InitialValue (ex: "000" = 3) + campos (sem Sequencia própria)
                int offset = 6; // Sequência da linha anterior (sempre 6 chars)
                
                if (!string.IsNullOrEmpty(lineConfig.InitialValue))
                {
                    offset += lineConfig.InitialValue.Length;
                }
                
                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "CalculateLineOffset",
                    Level = "Info",
                    Message = $"Linha {lineConfig.Name}: Offset = 6 (Seq. anterior) + {lineConfig.InitialValue?.Length ?? 0} (InitialValue '{lineConfig.InitialValue}') = {offset}"
                });
                
                return offset;
            }
            
            return 0;
        }

        private List<ParsedField> FiltrarParsedFields(List<ParsedField> parsedFields)
        {
            if (parsedFields == null)
                return new List<ParsedField>();

            return parsedFields.Where(field => !TemHierarquiaNoLineName(field.LineName) && !field.FieldName.Equals("Sequencia", StringComparison.OrdinalIgnoreCase)).ToList();
        }

        private bool TemHierarquiaNoLineName(string lineName)
        {
            if (string.IsNullOrEmpty(lineName))
                return false;

            return lineName.Contains(".");
        }

        private string ApplyAlignment(string value, AlignmentType alignmentType)
        {
            return alignmentType switch
            {
                AlignmentType.Left => value.TrimEnd(),
                AlignmentType.Right => value.TrimStart(),
                AlignmentType.Center => value.Trim(),
                _ => value.Trim()
            };
        }

        private static AlignmentType GetAlignmentType(string value)
        {
            return value?.ToLower() switch
            {
                "left" => AlignmentType.Left,
                "right" => AlignmentType.Right,
                "center" => AlignmentType.Center,
                _ => AlignmentType.Left // valor padrão
            };
        }

        private async Task<Layout> LoadLayoutAsync(Stream layoutStream)
        {
            layoutStream.Position = 0;
            using var reader = new StreamReader(layoutStream, Encoding.UTF8, true);
            string xmlContent = await reader.ReadToEndAsync();

            xmlContent = CleanXmlContent(xmlContent);

            XDocument xdoc = ParseXmlWithNamespaces(xmlContent);

            return ParseLayoutFromXDocument(xdoc);
        }

        private string CleanXmlContent(string xmlContent)
        {
            if (xmlContent.StartsWith("\uFEFF"))
                xmlContent = xmlContent.Substring(1);
            if (xmlContent.StartsWith("\uFFFE"))
                xmlContent = xmlContent.Substring(1);

            if (!xmlContent.TrimStart().StartsWith("<"))
            {
                int firstTag = xmlContent.IndexOf('<');
                if (firstTag > 0)
                    xmlContent = xmlContent.Substring(firstTag);
            }

            return xmlContent.Trim();
        }

        private XDocument ParseXmlWithNamespaces(string xmlContent)
        {
            try
            {
                return XDocument.Parse(xmlContent);
            }
            catch (Exception)
            {
                string cleanedXml = RemoveProblematicNamespaces(xmlContent);
                return XDocument.Parse(cleanedXml);
            }
        }

        private string RemoveProblematicNamespaces(string xmlContent)
        {
            xmlContent = System.Text.RegularExpressions.Regex.Replace(xmlContent, @"xmlns(:\w+)?=""[^""]*""", "");

            xmlContent = System.Text.RegularExpressions.Regex.Replace(xmlContent, @"xsi:type=""[^""]*""", "");

            xmlContent = System.Text.RegularExpressions.Regex.Replace(xmlContent, @"xmlns:xsi=""[^""]*""", "");

            return xmlContent;
        }

        private Layout ParseLayoutFromXDocument(XDocument xdoc)
        {
            var root = xdoc.Root;
            if (root == null) throw new Exception("XML root é nulo");

            XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";

            var layout = new Layout
            {
                LayoutGuid = GetElementValue(root, "LayoutGuid"),
                LayoutType = GetElementValue(root, "LayoutType"),
                Name = GetElementValue(root, "Name"),
                Description = GetElementValue(root, "Description"),
                LimitOfCaracters = GetElementIntValue(root, "LimitOfCaracters"),
                Elements = new List<LineElement>()
            };

            var elements = root.Element("Elements");
            if (elements != null)
            {
                foreach (var lineElem in elements.Elements("Element"))
                {
                    var typeAttr = lineElem.Attribute(xsi + "type") ?? lineElem.Attribute("type");

                    if (typeAttr?.Value == "LineElementVO")
                    {
                        var line = ParseLineElementWithHierarchy(lineElem, xsi);
                        if (line != null)
                        {
                            layout.Elements.Add(line);
                        }
                    }
                }
            }

            return layout;
        }

        // ✅ NOVO MÉTODO: Parse recursivo preservando hierarquia
        private LineElement ParseLineElementWithHierarchy(XElement lineElem, XNamespace xsi)
        {
            var line = new LineElement
            {
                ElementGuid = GetElementValue(lineElem, "ElementGuid"),
                Name = GetElementValue(lineElem, "Name"),
                Description = GetElementValue(lineElem, "Description"),
                Sequence = GetElementIntValue(lineElem, "Sequence"),
                IsRequired = GetElementBoolValue(lineElem, "IsRequired"),
                MinimalOccurrence = GetElementIntValue(lineElem, "MinimalOccurrence"),
                MaximumOccurrence = GetElementIntValue(lineElem, "MaximumOccurrence"),
                InitialValue = GetElementValue(lineElem, "InitialValue"),
                Elements = new List<string>()
            };

            var lineElements = lineElem.Element("Elements");
            if (lineElements != null)
            {
                foreach (var childElem in lineElements.Elements("Element"))
                {
                    var childTypeAttr = childElem.Attribute(xsi + "type") ?? childElem.Attribute("type");

                    if (childTypeAttr?.Value == "FieldElementVO")
                    {
                        // ✅ FieldElement normal
                        var field = new FieldElement
                        {
                            ElementGuid = GetElementValue(childElem, "ElementGuid"),
                            Name = GetElementValue(childElem, "Name"),
                            Description = GetElementValue(childElem, "Description"),
                            Sequence = GetElementIntValue(childElem, "Sequence"),
                            IsRequired = GetElementBoolValue(childElem, "IsRequired"),
                            StartValue = GetElementIntValue(childElem, "StartValue"),
                            LengthField = GetElementIntValue(childElem, "LengthField"),
                            AlignmentType = GetAlignmentType(GetElementValue(childElem, "AlignmentType")),
                            IsStaticValue = GetElementBoolValue(childElem, "IsStaticValue"),
                            IsSequential = GetElementBoolValue(childElem, "IsSequential"),
                            DataTypeGuid = GetElementValue(childElem, "DataTypeGuid")
                        };

                        string fieldJson = JsonConvert.SerializeObject(field);
                        line.Elements.Add(fieldJson);

                        _techLogger.LogTechnical(new TechLogEntry
                        {
                            RequestId = Guid.NewGuid().ToString(),
                            Endpoint = "ParseLayoutFromXDocument",
                            Level = "Debug",
                            Message = $"Campo: {field.Name}, StartValue: {field.StartValue}, Length: {field.LengthField}"
                        });
                    }
                    else if (childTypeAttr?.Value == "LineElementVO")
                    {
                        // ✅ CORREÇÃO CRÍTICA: Preservar LineElement filho
                        var childLine = ParseLineElementWithHierarchy(childElem, xsi);
                        if (childLine != null)
                        {
                            string childLineJson = JsonConvert.SerializeObject(childLine);
                            line.Elements.Add(childLineJson);

                            _techLogger.LogTechnical(new TechLogEntry
                            {
                                RequestId = Guid.NewGuid().ToString(),
                                Endpoint = "ParseLayoutFromXDocument",
                                Level = "Info",
                                Message = $"✅ LineElement FILHO preservado: {childLine.Name} dentro de {line.Name}"
                            });
                        }
                    }
                    else
                    {
                        _techLogger.LogTechnical(new TechLogEntry
                        {
                            RequestId = Guid.NewGuid().ToString(),
                            Endpoint = "ParseLayoutFromXDocument",
                            Level = "Warn",
                            Message = $"Tipo de elemento desconhecido: {childTypeAttr?.Value}"
                        });
                    }
                }
            }

            // Validar tamanho total da linha após carregar todos os elementos
            int totalLength = CalculateLineLengthForValidation(line);
            if (totalLength > 0) // Se tiver campos
            {
                string validationStatus = totalLength == 600 ? "✓ OK" : $" AVISO: {totalLength} posições (esperado: 600)";
                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ParseLayoutFromXDocument",
                    Level = totalLength == 600 ? "Info" : "Warn",
                    Message = $"Validação tamanho {line.Name}: {validationStatus}"
                });
            }

            return line;
        }

        // Método auxiliar para calcular tamanho da linha apenas com FieldElements (não recursivo para filhos)
        private int CalculateLineLengthForValidation(LineElement lineElement)
        {
            if (lineElement == null || lineElement.Elements == null)
                return 0;

            int totalLength = 0;

            foreach (var elementJson in lineElement.Elements)
            {
                try
                {
                    var field = JsonConvert.DeserializeObject<FieldElement>(elementJson);
                    if (field != null && field.LengthField > 0)
                    {
                        totalLength += field.LengthField;
                    }
                }
                catch { }
            }

            return totalLength;
        }

        private string GetElementValue(XElement parent, string elementName)
        {
            return parent.Element(elementName)?.Value ?? string.Empty;
        }

        private int GetElementIntValue(XElement parent, string elementName, int defaultValue = 0)
        {
            var value = parent.Element(elementName)?.Value;
            return int.TryParse(value, out int result) ? result : defaultValue;
        }

        private bool GetElementBoolValue(XElement parent, string elementName, bool defaultValue = false)
        {
            var value = parent.Element(elementName)?.Value;
            return bool.TryParse(value, out bool result) ? result : defaultValue;
        }

        private async Task<string> ReadTextFileWithEncoding(Stream txtStream)
        {
            txtStream.Position = 0;
            using var reader = new StreamReader(txtStream, Encoding.UTF8);
            return await reader.ReadToEndAsync();
        }

        private DocumentSummary CalculateSummary(ParsingResult result)
        {
            // Extrair linhas presentes e esperadas
            var linesPresent = ExtractLinesPresent(result.ParsedFields);
            var linesExpected = ExtractExpectedLinesFromLayout(result.Layout);
            var missingLines = linesExpected.Count - linesPresent.Count;

            // Calcular total de linhas lógicas (para mqseries com 600 chars por linha)
            int totalLogicalLines = 0;
            if (!string.IsNullOrEmpty(result.RawText))
            {
                var cleanContent = result.RawText.Replace("\r", "").Replace("\n", "");
                if (cleanContent.Length >= 600 && cleanContent.Length % 600 == 0)
                {
                    // Linhas de comprimento fixo de 600
                    totalLogicalLines = cleanContent.Length / 600;
                }
                else
                {
                    // Linhas físicas (quebras de linha)
                    totalLogicalLines = result.RawText.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
                }
            }

            // Extrair tipo e versão do layout a partir do nome
            string documentType = ExtractLayoutType(result.Layout?.Name);
            string layoutVersion = ExtractLayoutVersion(result.Layout?.Name);

            return new DocumentSummary
            {
                TotalFields = result.ParsedFields.Count,
                ValidFields = result.ParsedFields.Count(f => f.Status == "ok"),
                WarningFields = result.ParsedFields.Count(f => f.Status == "warning"),
                ErrorFields = result.ParsedFields.Count(f => f.Status == "error"),
                TotalLines = totalLogicalLines,
                PresentLines = linesPresent.Count,
                ExpectedLines = linesExpected.Count,
                MissingLines = missingLines > 0 ? missingLines : 0,
                DocumentType = documentType,
                LayoutVersion = layoutVersion,
                ProcessingDate = DateTime.Now
            };
        }

        private string ExtractLayoutType(string layoutName)
        {
            if (string.IsNullOrEmpty(layoutName))
                return "N/A";

            // Exemplo: "LAY_CNHI_TXT_MQSERIES_ENVNFE_4.00_NFe"
            // Procurar por padrões conhecidos
            var upperName = layoutName.ToUpper();
            
            if (upperName.Contains("MQSERIES"))
                return "MQSeries";
            if (upperName.Contains("IDOC"))
                return "iDoc";
            if (upperName.Contains("EDI"))
                return "EDI";
            if (upperName.Contains("XML"))
                return "XML";
            if (upperName.Contains("JSON"))
                return "JSON";
            
            // Tentar extrair da estrutura LAY_*_TXT_TIPO_*
            var parts = layoutName.Split('_');
            if (parts.Length >= 4)
            {
                // Retornar a parte que provavelmente é o tipo (após TXT)
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    if (parts[i].Equals("TXT", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length)
                    {
                        return parts[i + 1];
                    }
                }
            }

            return "Desconhecido";
        }

        private string ExtractLayoutVersion(string layoutName)
        {
            if (string.IsNullOrEmpty(layoutName))
                return "N/A";

            // Procurar por padrão de versão: X.XX ou X.X
            // Exemplo: "LAY_CNHI_TXT_MQSERIES_ENVNFE_4.00_NFe" -> "4.00"
            var versionPattern = new System.Text.RegularExpressions.Regex(@"_(\d+\.\d+)(?:_|$)");
            var match = versionPattern.Match(layoutName);
            
            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            // Tentar outros padrões (ex: V1, V2, etc.)
            var versionPattern2 = new System.Text.RegularExpressions.Regex(@"[Vv](\d+(?:\.\d+)?)");
            var match2 = versionPattern2.Match(layoutName);
            
            if (match2.Success)
            {
                return match2.Groups[1].Value;
            }

            return "N/A";
        }

        DocumentStructure ILayoutParserService.BuildDocumentStructure(ParsingResult result)
        {
            var linesPresent = ExtractLinesPresent(result.ParsedFields);

            var linesExpected = ExtractExpectedLinesFromLayout(result.Layout);

            var missingRequiredLines = IdentifyMissingRequiredLines(linesPresent, linesExpected);

            var lineDetails = BuildLineDetails(result.ParsedFields, linesExpected, result.RawText);

            var validation = BuildDocumentValidation(result, missingRequiredLines, linesPresent, linesExpected);

            return new DocumentStructure
            {
                LinesPresent = linesPresent,
                LinesExpected = linesExpected,
                MissingRequiredLines = missingRequiredLines,
                LineDetails = lineDetails,
                Validation = validation
            };
        }

        private List<string> ExtractExpectedLinesFromLayout(Layout layout)
        {
            var expectedLines = new List<string>();

            try
            {
                if (layout == null)
                {
                    _techLogger.LogTechnical(new TechLogEntry
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        Endpoint = "ExtractExpectedLinesFromLayout",
                        Level = "Info",
                        Message = "Layout é nulo, usando linhas padrão"
                    });

                    return GetDefaultExpectedLines();
                }

                // ✅ EXTRAIR LINHAS RECURSIVAMENTE (incluindo LineElements filhos)
                if (layout.Elements != null && layout.Elements.Any())
                {
                    foreach (var lineElement in layout.Elements)
                    {
                        ExtractLineElementsRecursively(lineElement, expectedLines);
                    }
                }

                if (expectedLines.Any())
                {
                    _techLogger.LogTechnical(new TechLogEntry
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        Endpoint = "ExtractExpectedLinesFromLayout",
                        Level = "Info",
                        Message = $"Extraídas {expectedLines.Count} linhas do layout: {string.Join(", ", expectedLines)}"
                    });
                    return expectedLines;
                }

                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ExtractExpectedLinesFromLayout",
                    Level = "Warn",
                    Message = "Não foi possível extrair linhas do layout, usando padrão"
                });

                return GetDefaultExpectedLines();
            }
            catch (Exception ex)
            {
                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ExtractExpectedLinesFromLayout",
                    Level = "Error",
                    Message = $"Erro ao extrair linhas esperadas do layout: {ex.Message}"
                });

                return GetDefaultExpectedLines();
            }
        }

        private void ExtractLineElementsRecursively(LineElement lineElement, List<string> expectedLines)
        {
            // Adicionar o próprio LineElement
            if (!string.IsNullOrEmpty(lineElement.Name))
            {
                var lineName = lineElement.Name.Trim().ToUpper();
                if (IsValidLineName(lineName) && !expectedLines.Contains(lineName))
                {
                    expectedLines.Add(lineName);
                }
            }
            else if (!string.IsNullOrEmpty(lineElement.InitialValue))
            {
                var lineName = lineElement.InitialValue.Trim().ToUpper();
                if (IsValidLineName(lineName) && !expectedLines.Contains(lineName))
                {
                    expectedLines.Add(lineName);
                }
            }

            // Extrair recursivamente os LineElements filhos
            if (lineElement.Elements != null && lineElement.Elements.Any())
            {
                foreach (var elementJson in lineElement.Elements)
                {
                    try
                    {
                        // Tentar desserializar como LineElement filho
                        if (elementJson.Contains("\"Type\":\"LineElementVO\""))
                        {
                            var childLineElement = JsonConvert.DeserializeObject<LineElement>(elementJson);
                            if (childLineElement != null)
                            {
                                ExtractLineElementsRecursively(childLineElement, expectedLines);
                            }
                        }
                    }
                    catch { }
                }
            }
        }


        private List<string> ExtractLinesPresent(List<ParsedField> parsedFields)
        {
            if (parsedFields == null || !parsedFields.Any())
                return new List<string>();

            return parsedFields.Where(field => !string.IsNullOrEmpty(field.LineName)).Select(field => field.LineName).Distinct().OrderBy(lineName => lineName).ToList();
        }

        private List<string> IdentifyMissingRequiredLines(List<string> linesPresent, List<string> linesExpected)
        {
            var missingLines = new List<string>();

            var requiredLines = new Dictionary<string, bool>
            {
                ["HEADER"] = true,
                ["LINHA000"] = true,
                ["LINHA001"] = true,
                ["LINHA002"] = false,
                ["LINHA003"] = false,
                ["TRAILER"] = true
            };

            foreach (var expectedLine in linesExpected)
            {
                if (requiredLines.ContainsKey(expectedLine) && requiredLines[expectedLine] && !linesPresent.Contains(expectedLine))
                    missingLines.Add(expectedLine);

            }

            return missingLines;
        }

        private Dictionary<string, LineDetail> BuildLineDetails(List<ParsedField> parsedFields, List<string> linesExpected, string rawText)
        {
            var lineDetails = new Dictionary<string, LineDetail>();
            var lines = rawText?.Split('\n') ?? Array.Empty<string>();

            foreach (var lineName in linesExpected)
            {
                var lineFields = parsedFields?.Where(f => f.LineName == lineName).ToList() ?? new List<ParsedField>();

                var occurrences = lineFields.Select(f => f.Occurrence).DefaultIfEmpty(0).Max();

                var lineContent = FindLineContent(lines, lineName);

                lineDetails[lineName] = new LineDetail
                {
                    LineNumber = CalculateLineNumber(lineName),
                    Occurrences = Math.Max(occurrences, lineContent.Any() ? 1 : 0),
                    IsRequired = IsLineRequired(lineName),
                    IsPresent = lineFields.Any() || lineContent.Any(),
                    FieldsCount = lineFields.Count,
                    SampleContent = lineContent.FirstOrDefault() ?? string.Empty,
                    TotalLength = lineContent.FirstOrDefault()?.Length ?? 0
                };
            }

            return lineDetails;
        }

        private DocumentValidation BuildDocumentValidation(ParsingResult result, List<string> missingRequiredLines, List<string> linesPresent, List<string> linesExpected)
        {
            var hasMissingLines = missingRequiredLines.Any();
            var hasFieldErrors = result.Summary?.ErrorFields > 0;
            var hasWarnings = result.Summary?.WarningFields > 0;

            var complianceScore = CalculateComplianceScore(result.Summary);
            var structureScore = CalculateStructureScore(linesPresent, linesExpected);
            var businessScore = CalculateBusinessScore(result.Summary);

            var overallStatus = hasMissingLines || hasFieldErrors ? "Error" :
                               hasWarnings ? "Warning" : "Valid";

            return new DocumentValidation
            {
                IsValid = overallStatus == "Valid",
                HasErrors = overallStatus == "Error",
                HasWarnings = hasWarnings,
                OverallStatus = overallStatus,

                MissingRequiredLines = missingRequiredLines,
                StructuralErrors = IdentifyStructuralErrors(result.RawText),
                ValidationWarnings = IdentifyValidationWarnings(result.ParsedFields),
                CriticalErrors = IdentifyCriticalErrors(missingRequiredLines, result.ParsedFields),

                IsStructurallyValid = !hasMissingLines,
                IsBusinessValid = !hasFieldErrors,
                IsCompliant = overallStatus != "Error",

                ComplianceScore = complianceScore,
                StructureScore = structureScore,
                BusinessScore = businessScore,

                Suggestions = BuildValidationSuggestions(result, missingRequiredLines, linesPresent)
            };
        }

        private int CalculateLineNumber(string lineName)
        {
            return lineName switch
            {
                "HEADER" => 0,
                "LINHA000" => 1,
                "LINHA001" => 2,
                "LINHA002" => 3,
                "LINHA003" => 4,
                "LINHA004" => 5,
                "TRAILER" => 99,
                _ => -1
            };
        }

        private bool IsLineRequired(string lineName)
        {
            var requiredLines = new List<string>
            {
                "HEADER",
                "LINHA000",
                "LINHA001",
                "TRAILER"
            };
            return requiredLines.Contains(lineName);
        }

        private List<string> FindLineContent(string[] lines, string lineName)
        {
            return lines.Where(line => line.StartsWith(lineName) || lineName == "HEADER" && line.StartsWith("HEADER") || lineName.StartsWith("LINHA") && line.Length >= 6 && int.TryParse(line.Substring(5, 1), out _)).ToList();
        }

        private int CalculateComplianceScore(DocumentSummary summary)
        {
            if (summary?.TotalFields == 0) return 100;
            return (int)((double)summary.ValidFields / summary.TotalFields * 100);
        }

        private int CalculateStructureScore(List<string> linesPresent, List<string> linesExpected)
        {
            if (!linesExpected.Any()) return 100;
            var expectedRequired = linesExpected.Count(IsLineRequired);
            var presentRequired = linesPresent.Count(IsLineRequired);

            return expectedRequired > 0 ? (int)((double)presentRequired / expectedRequired * 100) : 100;
        }

        private int CalculateBusinessScore(DocumentSummary summary)
        {
            if (summary?.TotalFields == 0) return 100;
            var errorWeight = summary.ErrorFields * 3;
            var warningWeight = summary.WarningFields;
            var totalWeightedIssues = errorWeight + warningWeight;

            return Math.Max(0, 100 - totalWeightedIssues * 100 / Math.Max(summary.TotalFields, 1));
        }

        private bool IsValidLineName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            var upperName = name.ToUpper();

            var validPatterns = new[]
            {
                "HEADER",
                "LINHA000", "LINHA001", "LINHA002", "LINHA003", "LINHA004", "LINHA005",
                "LINHA006", "LINHA007", "LINHA008", "LINHA009", "LINHA010",
                "TRAILER",
                "FOOTER",
                "DETAIL", "DETAIL01", "DETAIL02",
                "SUMMARY"
            };

            return validPatterns.Contains(upperName) || upperName.StartsWith("LINHA") || upperName.StartsWith("LINE") || upperName.StartsWith("RECORD");
        }

        private List<string> GetDefaultExpectedLines()
        {
            return new List<string>
            {
                "HEADER",
                "LINHA000",
                "LINHA001",
                "LINHA002",
                "LINHA003",
                "LINHA004",
                "TRAILER"
            };
        }

        private List<string> IdentifyValidationWarnings(List<ParsedField> parsedFields)
        {
            var warnings = new List<string>();

            if (parsedFields == null || !parsedFields.Any())
                return warnings;

            var fieldsByLine = parsedFields.GroupBy(f => f.LineName);

            foreach (var lineGroup in fieldsByLine)
            {
                var lineName = lineGroup.Key;
                var lineFields = lineGroup.ToList();

                var emptyOptionalFields = lineFields.Where(f => !f.IsRequired && string.IsNullOrWhiteSpace(f.Value)).ToList();

                if (emptyOptionalFields.Any())
                    warnings.Add($"{lineName}: {emptyOptionalFields.Count} campo(s) opcional(ais) vazio(s): " + string.Join(", ", emptyOptionalFields.Select(f => f.FieldName)));


                var unusualPatterns = CheckUnusualPatterns(lineFields);
                warnings.AddRange(unusualPatterns);

                var consistencyWarnings = CheckFieldConsistency(lineFields, lineName);
                warnings.AddRange(consistencyWarnings);
            }

            var globalWarnings = CheckGlobalConsistency(parsedFields);
            warnings.AddRange(globalWarnings);

            return warnings.Distinct().ToList();
        }

        private List<string> CheckUnusualPatterns(List<ParsedField> fields)
        {
            var warnings = new List<string>();

            foreach (var field in fields)
            {
                if (field.DataType == "numeric" && field.Value != null && field.Value.StartsWith("0") && field.Value.Length > 1 && !field.Value.All(c => c == '0'))
                    warnings.Add($"{field.FieldName}: Valor numérico com zero à esquerda: '{field.Value}'");

                if (field.Value == "00000000" || field.Value == "99999999" || field.Value == "12345678")
                    warnings.Add($"{field.FieldName}: Valor padrão suspeito: '{field.Value}'");

                if (field.DataType == "date" && DateTime.TryParse(field.Value, out DateTime date))
                {
                    if (date > DateTime.Now.AddYears(1))
                        warnings.Add($"{field.FieldName}: Data no futuro: {field.Value}");
                    else if (date < new DateTime(2000, 1, 1))
                        warnings.Add($"{field.FieldName}: Data muito antiga: {field.Value}");
                }
            }

            return warnings;
        }

        private List<string> IdentifyCriticalErrors(List<string> missingRequiredLines, List<ParsedField> parsedFields)
        {
            var criticalErrors = new List<string>();

            foreach (var missingLine in missingRequiredLines)
            {
                criticalErrors.Add($"LINHA OBRIGATÓRIA AUSENTE: {missingLine} - Documento pode ser rejeitado");
            }

            if (parsedFields == null || !parsedFields.Any())
            {
                criticalErrors.Add("NENHUM CAMPO ENCONTRADO - Layout pode estar incorreto ou documento vazio");
                return criticalErrors;
            }

            var criticalFields = parsedFields.Where(f => f.IsRequired && f.Status == "Error" && IsCriticalField(f.FieldName)).ToList();

            foreach (var field in criticalFields)
            {
                criticalErrors.Add($"CAMPO CRÍTICO COM ERRO: {field.FieldName} - {field.ValidationMessage}");
            }

            var severeFormatErrors = parsedFields.Where(f => f.Status == "Error" && IsSevereFormatError(f.ValidationMessage)).Select(f => $"ERRO DE FORMATAÇÃO GRAVE: {f.FieldName} - {f.ValidationMessage}").ToList();

            criticalErrors.AddRange(severeFormatErrors);

            var duplicateErrors = CheckCriticalDuplicates(parsedFields);
            criticalErrors.AddRange(duplicateErrors);

            return criticalErrors;
        }

        private bool IsCriticalField(string fieldName)
        {
            var criticalFields = new[]
            {
                "CPF", "CNPJ", "CODIGO", "ID", "CHAVE", "KEY",
                "VALOR", "AMOUNT", "TOTAL", "DATA", "DATE",
                "DOCUMENTO", "DOCUMENT", "NUMERO", "NUMBER"
            };

            return criticalFields.Any(critical => fieldName.Contains(critical, StringComparison.OrdinalIgnoreCase));
        }

        private bool IsSevereFormatError(string validationMessage)
        {
            var severeIndicators = new[]
            {
                "inválido", "invalid", "incorreto", "incorrect",
                "não numérico", "not numeric", "formato errado",
                "malformed", "fora do padrão"
            };

            return severeIndicators.Any(indicator => validationMessage.Contains(indicator, StringComparison.OrdinalIgnoreCase));
        }

        private List<ValidationSuggestion> BuildValidationSuggestions(ParsingResult result, List<string> missingRequiredLines, List<string> linesPresent)
        {
            var suggestions = new List<ValidationSuggestion>();

            foreach (var missingLine in missingRequiredLines)
            {
                suggestions.Add(new ValidationSuggestion
                {
                    Type = "Correction",
                    Target = "Line",
                    Message = $"Adicionar linha obrigatória: {missingLine}",
                    Confidence = "High",
                    LineNumber = CalculateLineNumber(missingLine)
                });
            }

            var errorFields = result.ParsedFields?.Where(f => f.Status == "Error").Take(10).ToList() ?? new List<ParsedField>();

            foreach (var field in errorFields)
            {
                var suggestion = CreateFieldSuggestion(field);
                if (suggestion != null)
                    suggestions.Add(suggestion);
            }

            var structuralSuggestions = CreateStructuralSuggestions(result, linesPresent);
            suggestions.AddRange(structuralSuggestions);

            var patternSuggestions = CreatePatternBasedSuggestions(result.ParsedFields);
            suggestions.AddRange(patternSuggestions);

            return suggestions.OrderByDescending(s => s.Confidence == "High").ThenBy(s => s.LineNumber).ToList();
        }

        private ValidationSuggestion CreateFieldSuggestion(ParsedField field)
        {
            if (field == null) return null;

            var message = field.ValidationMessage?.ToLower() ?? "";

            if (message.Contains("numérico") || message.Contains("numeric"))
            {
                return new ValidationSuggestion
                {
                    Type = "Correction",
                    Target = "Field",
                    FieldName = field.FieldName,
                    LineNumber = CalculateLineNumber(field.LineName),
                    Message = $"Campo {field.FieldName} deve conter apenas números. Valor atual: '{field.Value}'",
                    Confidence = "High"
                };
            }

            if (message.Contains("data") || message.Contains("date"))
            {
                return new ValidationSuggestion
                {
                    Type = "Correction",
                    Target = "Field",
                    FieldName = field.FieldName,
                    LineNumber = CalculateLineNumber(field.LineName),
                    Message = $"Formato de data inválido em {field.FieldName}. Use DDMMAAAA ou AAAAMMDD",
                    Confidence = "Medium"
                };
            }

            if (message.Contains("obrigatório") || message.Contains("required"))
            {
                return new ValidationSuggestion
                {
                    Type = "Correction",
                    Target = "Field",
                    FieldName = field.FieldName,
                    LineNumber = CalculateLineNumber(field.LineName),
                    Message = $"Campo obrigatório {field.FieldName} está vazio",
                    Confidence = "High"
                };
            }

            return new ValidationSuggestion
            {
                Type = "Correction",
                Target = "Field",
                FieldName = field.FieldName,
                LineNumber = CalculateLineNumber(field.LineName),
                Message = $"Corrigir campo {field.FieldName}: {field.ValidationMessage}",
                Confidence = "Medium"
            };
        }

        private List<ValidationSuggestion> CreateStructuralSuggestions(ParsingResult result, List<string> linesPresent)
        {
            var suggestions = new List<ValidationSuggestion>();

            if (linesPresent.Contains("LINHA002") && !linesPresent.Contains("LINHA001"))
            {
                suggestions.Add(new ValidationSuggestion
                {
                    Type = "Warning",
                    Target = "Structure",
                    Message = "Sequência de linhas irregular: LINHA002 presente mas LINHA001 ausente",
                    Confidence = "Medium"
                });
            }

            var detailLines = linesPresent.Count(l => l.StartsWith("LINHA") && l != "LINHA000");
            if (detailLines > 50)
            {
                suggestions.Add(new ValidationSuggestion
                {
                    Type = "Info",
                    Target = "Structure",
                    Message = $"Documento contém {detailLines} linhas de detalhe - verificar totais e consistência",
                    Confidence = "Low"
                });
            }

            return suggestions;
        }

        private List<ValidationSuggestion> CreatePatternBasedSuggestions(List<ParsedField> parsedFields)
        {
            var suggestions = new List<ValidationSuggestion>();

            if (parsedFields == null) return suggestions;

            var allValues = parsedFields.Select(f => f.Value).Where(v => !string.IsNullOrEmpty(v));

            var defaultValues = allValues.Count(v => v == "0" || v == "00000000" || v == "99999999");
            if (defaultValues > parsedFields.Count * 0.3)
            {
                suggestions.Add(new ValidationSuggestion
                {
                    Type = "Warning",
                    Target = "DataQuality",
                    Message = "Muitos campos com valores padrão - verificar qualidade dos dados",
                    Confidence = "Medium"
                });
            }

            return suggestions;
        }

        private List<string> CheckCriticalDuplicates(List<ParsedField> parsedFields)
        {
            var errors = new List<string>();

            var identifierFields = parsedFields.Where(f => f.FieldName.Contains("CPF") || f.FieldName.Contains("CNPJ") || f.FieldName.Contains("ID")).ToList();

            var duplicates = identifierFields.GroupBy(f => f.Value).Where(g => g.Count() > 1 && !string.IsNullOrWhiteSpace(g.Key)).ToList();

            foreach (var duplicateGroup in duplicates)
            {
                if (duplicateGroup.Key.Length >= 11)
                    errors.Add($"IDENTIFICADOR DUPLICADO: {duplicateGroup.Key} encontrado {duplicateGroup.Count()} vezes");

            }

            return errors;
        }

        private List<string> CheckFieldConsistency(List<ParsedField> fields, string lineName)
        {
            var warnings = new List<string>();

            var amountFields = fields.Where(f => f.FieldName.Contains("VALOR") || f.FieldName.Contains("AMOUNT")).ToList();
            var quantityFields = fields.Where(f => f.FieldName.Contains("QUANTIDADE") || f.FieldName.Contains("QUANTITY")).ToList();

            if (amountFields.Any() && quantityFields.Any())
            {
                foreach (var amountField in amountFields)
                {
                    var relatedQuantity = quantityFields.FirstOrDefault(f => f.FieldName.Replace("VALOR", "QUANTIDADE") == amountField.FieldName || f.FieldName.Replace("AMOUNT", "QUANTITY") == amountField.FieldName);

                    if (relatedQuantity != null)
                    {
                        if (decimal.TryParse(amountField.Value, out decimal amount) && decimal.TryParse(relatedQuantity.Value, out decimal quantity) && quantity > 0 && amount > 0)
                        {
                            var unitPrice = amount / quantity;
                            if (unitPrice < 0.01m || unitPrice > 1000000m)
                                warnings.Add($"{lineName}: Preço unitário incomum ({unitPrice:C}) entre {amountField.FieldName} e {relatedQuantity.FieldName}");

                        }
                    }
                }
            }

            return warnings;
        }

        private List<string> CheckGlobalConsistency(List<ParsedField> allFields)
        {
            var warnings = new List<string>();

            var totalFields = allFields.Where(f => f.FieldName.Contains("TOTAL") || f.FieldName.Contains("SOMATORIO")).ToList();
            var detailFields = allFields.Where(f => f.FieldName.Contains("DETALHE") || f.FieldName.Contains("ITEM")).ToList();

            if (totalFields.Any() && detailFields.Count > 1)
            {
                // Lógica para verificar consistência de totais
                // (implementação específica depende da estrutura do documento)
            }

            return warnings;
        }

        private List<string> IdentifyStructuralErrors(string rawText)
        {
            var errors = new List<string>();

            if (string.IsNullOrEmpty(rawText))
            {
                errors.Add("Documento vazio ou inválido");
                return errors;
            }

            // Remover quebras de linha para análise consistente
            var cleanText = rawText.Replace("\r", "").Replace("\n", "");

            // Validar se começa com HEADER
            if (!cleanText.StartsWith("HEADER"))
            {
                errors.Add("Documento não começa com HEADER");
            }

            // Validar se o tamanho total é múltiplo de 600
            if (cleanText.Length < 600)
            {
                errors.Add($"Documento muito pequeno ({cleanText.Length} caracteres, mínimo 600)");
                return errors;
            }

            if (cleanText.Length % 600 != 0)
            {
                errors.Add($"Tamanho do documento ({cleanText.Length} caracteres) não é múltiplo de 600. Possível linha incompleta.");
            }

            // Validar blocos lógicos de 600 caracteres
            int totalLogicalLines = cleanText.Length / 600;
            for (int i = 0; i < totalLogicalLines; i++)
            {
                int startPos = i * 600;
                if (startPos + 600 <= cleanText.Length)
                {
                    var logicalLine = cleanText.Substring(startPos, 600);
                    var lineNumber = i + 1;

                    if (ContainsInvalidCharacters(logicalLine))
                    {
                        errors.Add($"Bloco lógico {lineNumber} (posição {startPos}-{startPos + 599}): Contém caracteres inválidos ou não-ASCII");
                    }
                }
            }

            // Validar sequências (se houver padrões de 6 dígitos)
            var sequentialMatches = System.Text.RegularExpressions.Regex.Matches(cleanText, @"\d{6}");
            if (sequentialMatches.Count < 2)
            {
                errors.Add("Documento não contém padrões de sequência esperados (formato: NNNNNN)");
            }

            return errors;
        }

        private bool ContainsInvalidCharacters(string line)
        {
            return line.Any(c => c < 32 || c > 126) && !line.All(c => c == ' ' || c >= '0' && c <= '9' || c >= 'A' && c <= 'Z');
        }

        private void CheckLineSequence(string[] lines, List<string> errors)
        {
            int expectedLineNumber = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                if (line.StartsWith("LINHA"))
                {
                    if (line.Length >= 6 && int.TryParse(line.Substring(5, 1), out int lineNum))
                    {
                        if (lineNum != expectedLineNumber)
                            errors.Add($"Sequência quebrada: Esperado LINHA{expectedLineNumber:D3}, encontrado LINHA{lineNum:D3} na linha {i + 1}");

                        expectedLineNumber++;
                    }
                }
            }
        }

        // Substitua o trecho selecionado pelo seguinte código para realizar a varredura completa e montar o completedLayout com todos os FieldElement e LineElement aninhados:

        // Função auxiliar para varrer recursivamente todos os LineElement e FieldElement
        private void CollectAllElements(LineElement lineElement, List<LineElement> allLines, List<FieldElement> allFields)
        {
            if (lineElement == null)
                return;

            allLines.Add(lineElement);

            if (lineElement.Elements != null)
            {
                foreach (var elementJson in lineElement.Elements)
                {
                    // Tenta desserializar como FieldElement
                    try
                    {
                        var field = Newtonsoft.Json.JsonConvert.DeserializeObject<FieldElement>(elementJson);
                        if (field != null && !string.IsNullOrEmpty(field.Name))
                        {
                            allFields.Add(field);
                            continue;
                        }
                    }
                    catch { }

                    // Tenta desserializar como LineElement (aninhado)
                    try
                    {
                        var nestedLine = Newtonsoft.Json.JsonConvert.DeserializeObject<LineElement>(elementJson);
                        if (nestedLine != null && !string.IsNullOrEmpty(nestedLine.Name))
                            CollectAllElements(nestedLine, allLines, allFields);
                        
                    }
                    catch { }
                }
            }
        }

        // Método para somar o comprimento total dos campos de um LineElement (incluindo aninhados)
        private int SumLengthFieldFromFieldElements(LineElement lineElement)
        {
            if (lineElement == null || lineElement.Elements == null)
                return 0;

            int fieldsLength = 0;
            int fieldCount = 0;

            foreach (var elementJson in lineElement.Elements)
            {
                // Tenta desserializar como FieldElement
                try
                {
                    var field = JsonConvert.DeserializeObject<FieldElement>(elementJson);
                    if (field != null && field.LengthField > 0)
                    {
                        // ✅ CORREÇÃO: Excluir o campo "Sequencia" pois ele pertence à linha SEGUINTE
                        if (!field.Name.Equals("Sequencia", StringComparison.OrdinalIgnoreCase))
                        {
                            fieldsLength += field.LengthField;
                            fieldCount++;
                        }
                        continue;
                    }
                }
                catch { }

                // Tenta desserializar como LineElement (aninhado)
                // IMPORTANTE: Não somar LineElements aninhados no total da linha pai
                try
                {
                    var nestedLine = JsonConvert.DeserializeObject<LineElement>(elementJson);
                    if (nestedLine != null)
                    {
                        // Não soma - linhas aninhadas são processadas separadamente
                        continue;
                    }
                }
                catch { }
            }

            // ✅ APLICAR A MESMA LÓGICA DO ValidateLineLayoutWithResult:
            // 1. InitialValue (HEADER, "000", "001", etc.)
            int initialValueLength = !string.IsNullOrEmpty(lineElement.InitialValue) ? lineElement.InitialValue.Length : 0;
            
            // 2. Sequencia da linha ANTERIOR (6 chars), exceto para HEADER
            int sequenceFromPreviousLine = (lineElement.Name?.Equals("HEADER", StringComparison.OrdinalIgnoreCase) == true) ? 0 : 6;
            
            // 3. Total = InitialValue + campos (sem Sequencia) + Sequencia da linha anterior
            int totalLength = initialValueLength + fieldsLength + sequenceFromPreviousLine;

            // Log do tamanho calculado
            _techLogger.LogTechnical(new TechLogEntry
            {
                RequestId = Guid.NewGuid().ToString(),
                Endpoint = "SumLengthFieldFromFieldElements",
                Level = totalLength == 600 ? "Info" : "Warn",
                Message = $"Linha: {lineElement.Name ?? "Desconhecida"} | Campos: {fieldCount} ({fieldsLength} chars) + InitialValue ({initialValueLength}) + Seq. anterior ({sequenceFromPreviousLine}) = {totalLength}" + (totalLength != 600 ? "  DIFERENTE DE 600!" : " ✓")
            });

            return totalLength;
        }

        public Layout ReestruturarLayout(Layout layoutOriginal)
        {
            return _layoutNormalizer.ReestruturarLayout(layoutOriginal);
        }

        private LineElement CriarCopiaLineElement(LineElement original)
        {
            return new LineElement
            {
                ElementGuid = original.ElementGuid,
                Name = original.Name,
                Description = original.Description,
                Sequence = original.Sequence,
                IsRequired = original.IsRequired,
                MinimalOccurrence = original.MinimalOccurrence,
                MaximumOccurrence = original.MaximumOccurrence,
                InitialValue = original.InitialValue
            };
        }

        private bool EhLineElementFilho(string elementoJson)
        {
            try
            {
                var linha = JsonConvert.DeserializeObject<LineElement>(elementoJson);
                return EhLineElementFilho(linha);
            }
            catch
            {
                return false;
            }
        }

        private bool EhLineElementFilho(LineElement linha)
        {
            // Verifica se é um LineElementVO E não é a própria LINHA020
            return linha != null && !string.IsNullOrEmpty(linha.Name) && linha.Name != "LINHA020" && linha.Name.StartsWith("LINHA");
        }

        private LineElement FindMatchingLineConfigRecursive(string currentLine, List<LineElement> configs)
        {
            if (configs == null || !configs.Any())
                return null;

            // Primeiro busca nos elementos do nível atual
            foreach (var config in configs)
            {
                if (IsLineValidForConfig(currentLine, config))
                    return config;
            }

            // Se não encontrou no nível atual, busca recursivamente nos filhos
            foreach (var config in configs)
            {
                var nestedLines = new List<LineElement>();

                foreach (var json in config.Elements)
                {
                    try
                    {
                        var childLine = JsonConvert.DeserializeObject<LineElement>(json);
                        if (childLine != null && !string.IsNullOrEmpty(childLine.Name))
                            nestedLines.Add(childLine);
                        
                    }
                    catch { }
                }

                if (nestedLines.Any())
                {
                    var found = FindMatchingLineConfigRecursive(currentLine, nestedLines);
                    if (found != null)
                        return found;
                }
            }

            return null;
        }

        private string ObterLineNameSemHierarquia(string lineNameComHierarquia)
        {
            if (string.IsNullOrEmpty(lineNameComHierarquia))
                return lineNameComHierarquia;

            // Se tiver ".", pega apenas a última parte (LINHA020.LINHA021 → LINHA021)
            if (lineNameComHierarquia.Contains("."))
            {
                var partes = lineNameComHierarquia.Split('.');
                return partes[partes.Length - 1];
            }

            return lineNameComHierarquia;
        }

        public Layout ReordenarSequences(Layout layout)
        {
            return _layoutNormalizer.ReordenarSequences(layout);
        }

        private int ObterNumeroDaLinha(string nomeLinha)
        {
            if (string.IsNullOrEmpty(nomeLinha) || !nomeLinha.StartsWith("LINHA"))
                return 9999; // Coloca no final se não for uma linha numérica

            var numeroStr = nomeLinha.Substring(5); // Pega a parte depois de "LINHA"
            if (int.TryParse(numeroStr, out int numero))
            {
                return numero;
            }

            return 9999; // Coloca no final se não conseguir converter
        }

        private void ValidateLineLayout(LineElement lineConfig)
        {
            try
            {
                DebugLineElementStructure(lineConfig, $"VALIDANDO {lineConfig.Name}");

                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ValidateLineLayout",
                    Level = "Info",
                    Message = $"=== VALIDAÇÃO DO LAYOUT: {lineConfig.Name} ==="
                });

                // ✅ DEBUG: Mostrar todos os elementos brutos primeiro
                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ValidateLineLayout",
                    Level = "Debug",
                    Message = $"Elementos brutos na {lineConfig.Name}: {lineConfig.Elements?.Count ?? 0} elementos"
                });

                if (lineConfig.Elements != null)
                {
                    for (int i = 0; i < lineConfig.Elements.Count; i++)
                    {
                        string elementJson = lineConfig.Elements[i];
                        string elementPreview = elementJson.Length > 50 ? elementJson.Substring(0, 50) + "..." : elementJson;

                        _techLogger.LogTechnical(new TechLogEntry
                        {
                            RequestId = Guid.NewGuid().ToString(),
                            Endpoint = "ValidateLineLayout",
                            Level = "Debug",
                            Message = $"Elemento {i + 1}: {elementPreview}"
                        });

                        // Tentar identificar o tipo
                        try
                        {
                            var field = JsonConvert.DeserializeObject<FieldElement>(elementJson);
                            if (field != null)
                            {
                                _techLogger.LogTechnical(new TechLogEntry
                                {
                                    RequestId = Guid.NewGuid().ToString(),
                                    Endpoint = "ValidateLineLayout",
                                    Level = "Debug",
                                    Message = $"  → FieldElement: {field.Name}"
                                });
                                continue;
                            }
                        }
                        catch { }

                        try
                        {
                            var line = JsonConvert.DeserializeObject<LineElement>(elementJson);
                            if (line != null)
                            {
                                _techLogger.LogTechnical(new TechLogEntry
                                {
                                    RequestId = Guid.NewGuid().ToString(),
                                    Endpoint = "ValidateLineLayout",
                                    Level = "Debug",
                                    Message = $"  → LineElement: {line.Name}"
                                });
                                continue;
                            }
                        }
                        catch { }

                        _techLogger.LogTechnical(new TechLogEntry
                        {
                            RequestId = Guid.NewGuid().ToString(),
                            Endpoint = "ValidateLineLayout",
                            Level = "Debug",
                            Message = $"  → Tipo desconhecido"
                        });
                    }
                }

                // ✅ CORREÇÃO: Usar método mais robusto para separar elementos
                var (fieldElements, childLineElements) = SeparateElementsRobust(lineConfig);

                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ValidateLineLayout",
                    Level = "Info",
                    Message = $"RESULTADO SEPARAÇÃO: {fieldElements.Count} FieldElements, {childLineElements.Count} LineElements filhos"
                });

                if (childLineElements.Any())
                {
                    _techLogger.LogTechnical(new TechLogEntry
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        Endpoint = "ValidateLineLayout",
                        Level = "Info",
                        Message = $"ELEMENTOS FILHOS: {string.Join(", ", childLineElements.Select(c => c.Name))}"
                    });
                }

                int initialValueLength = !string.IsNullOrEmpty(lineConfig.InitialValue) ? lineConfig.InitialValue.Length : 0;

                // ✅ CORREÇÃO: Calcular apenas FieldElements (ignorar LineElements filhos E a tag "Sequencia")
                // A tag "Sequencia" de cada linha pertence à PRÓXIMA linha
                var fieldsToCalculate = fieldElements
                    .Where(f => !f.Name.Equals("Sequencia", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f.Sequence)
                    .ToList();

                int fieldsLength = fieldsToCalculate.Sum(f => f.LengthField);

                // Adicionar 6 chars (Sequencia da linha anterior) para todas as linhas EXCETO HEADER
                int sequenceFromPreviousLine = lineConfig.Name?.Equals("HEADER", StringComparison.OrdinalIgnoreCase) == true ? 0 : 6;

                int totalLength = initialValueLength + fieldsLength + sequenceFromPreviousLine;

                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ValidateLineLayout",
                    Level = "Info",
                    Message = $"InitialValue: '{lineConfig.InitialValue}' = {initialValueLength} chars"
                });

                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ValidateLineLayout",
                    Level = "Info",
                    Message = $"Campos PRÓPRIOS (EXCETO Sequencia, que pertence à próxima linha): {fieldsToCalculate.Count} campos = {fieldsLength} chars"
                });

                if (childLineElements.Any())
                {
                    _techLogger.LogTechnical(new TechLogEntry
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        Endpoint = "ValidateLineLayout",
                        Level = "Info",
                        Message = $"Elementos FILHOS (não contabilizados): {childLineElements.Count} linhas filhas"
                    });
                }

                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ValidateLineLayout",
                    Level = "Info",
                    Message = $"CÁLCULO: {initialValueLength} (InitialValue) + {fieldsLength} (Campos sem Sequencia) + {sequenceFromPreviousLine} (Sequencia da linha anterior) = {totalLength} chars"
                });

                // ✅ CORREÇÃO: Validação diferente para linhas com filhos
                bool isValid;
                if (childLineElements.Any())
                {
                    // Para LINHA020: deve ter APENAS os campos próprios + initialValue + sequence
                    // Os filhos são linhas SEPARADAS de 600 caracteres cada
                    isValid = totalLength <= 600;

                    string validationMsg = isValid ? "✅ VÁLIDO (linha com filhos)" : " AVISO: Linha com filhos";

                    if (lineConfig.Name == "LINHA020" && totalLength != 600)
                    {
                        validationMsg += $". LINHA020 tem {totalLength} chars (campos próprios apenas)";
                    }

                    _techLogger.LogTechnical(new TechLogEntry
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        Level = isValid ? "Info" : "Warn",
                        Message = $"TOTAL COM FILHOS: {totalLength} caracteres → {validationMsg}"
                    });
                }
                else
                {
                    // Para linhas sem filhos: cálculo normal
                    isValid = totalLength == 600;

                    _techLogger.LogTechnical(new TechLogEntry
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        Level = isValid ? "Info" : "Error",
                        Message = $"TOTAL: {totalLength} caracteres → {(isValid ? "✅ VÁLIDO (600)" : "❌ INVÁLIDO (deveria ser 600)")}"
                    });
                }

                if (!isValid && !childLineElements.Any())
                {
                    int difference = 600 - totalLength;
                    _techLogger.LogTechnical(new TechLogEntry
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        Endpoint = "ValidateLineLayout",
                        Level = "Warn",
                        Message = $"PROBLEMA IDENTIFICADO: {lineConfig.Name} tem {totalLength} chars ({(difference > 0 ? "faltam" : "sobram")} {Math.Abs(difference)})"
                    });
                }

                // ✅ CORREÇÃO: Log apenas dos campos próprios
                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ValidateLineLayout",
                    Level = "Info",
                    Message = "--- DETALHES DOS CAMPOS PRÓPRIOS ---"
                });

                int accumulated = initialValueLength;
                int fieldNumber = 1;

                foreach (var field in fieldsToCalculate)
                {
                    accumulated += field.LengthField;

                    _techLogger.LogTechnical(new TechLogEntry
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        Endpoint = "ValidateLineLayout",
                        Level = "Info",
                        Message = $"Campo {fieldNumber}: {field.Name} → Length: {field.LengthField} | StartValue: {field.StartValue} | Soma acumulada: {accumulated}"
                    });

                    fieldNumber++;
                }

                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ValidateLineLayout",
                    Level = "Info",
                    Message = $"Total calculado (SEM Sequencia, que pertence à próxima linha): {accumulated} chars"
                });

                // ✅ CORREÇÃO: Validar também os elementos filhos
                if (childLineElements.Any())
                {
                    _techLogger.LogTechnical(new TechLogEntry
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        Endpoint = "ValidateLineLayout",
                        Level = "Info",
                        Message = $"🎯 INICIANDO VALIDAÇÃO RECURSIVA DE {childLineElements.Count} FILHOS: {string.Join(", ", childLineElements.Select(f => f.Name))}"
                    });

                    foreach (var childLine in childLineElements)
                    {
                        _techLogger.LogTechnical(new TechLogEntry
                        {
                            RequestId = Guid.NewGuid().ToString(),
                            Endpoint = "ValidateLineLayout",
                            Level = "Info",
                            Message = $"🔍 CHAMANDO ValidateLineLayout PARA: {childLine.Name}"
                        });

                        ValidateLineLayout(childLine); // Validar recursivamente
                    }

                    _techLogger.LogTechnical(new TechLogEntry
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        Endpoint = "ValidateLineLayout",
                        Level = "Info",
                        Message = $"✅ VALIDAÇÃO DOS FILHOS DE {lineConfig.Name} CONCLUÍDA"
                    });
                }
                else
                {
                    _techLogger.LogTechnical(new TechLogEntry
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        Endpoint = "ValidateLineLayout",
                        Level = "Info",
                        Message = $"{lineConfig.Name} não possui elementos filhos"
                    });
                }

            }
            catch (Exception ex)
            {
                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ValidateLineLayout",
                    Level = "Error",
                    Message = $"Erro na validação do layout {lineConfig.Name}: {ex.Message}"
                });
            }
        }

        private (List<FieldElement> fieldElements, List<LineElement> childLineElements) SeparateElementsRobust(LineElement lineConfig)
        {
            var fieldElements = new List<FieldElement>();
            var childLineElements = new List<LineElement>();

            if (lineConfig?.Elements == null)
                return (fieldElements, childLineElements);

            _techLogger.LogTechnical(new TechLogEntry
            {
                RequestId = Guid.NewGuid().ToString(),
                Endpoint = "SeparateElementsRobust",
                Level = "Debug",
                Message = $"ANALISANDO {lineConfig.Elements.Count} ELEMENTOS EM {lineConfig.Name}"
            });

            for (int i = 0; i < lineConfig.Elements.Count; i++)
            {
                var elementJson = lineConfig.Elements[i];

                bool isLineElement = elementJson.Contains("\"Type\":\"LineElementVO\"");
                bool isFieldElement = elementJson.Contains("\"Type\":\"FieldElementVO\"");

                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "SeparateElementsRobust",
                    Level = "Debug",
                    Message = $"Elemento {i + 1}: Field={isFieldElement}, Line={isLineElement}, Preview: {elementJson.Substring(0, Math.Min(150, elementJson.Length))}"
                });

                if (isFieldElement)
                {
                    try
                    {
                        var field = JsonConvert.DeserializeObject<FieldElement>(elementJson);
                        if (field != null && !string.IsNullOrEmpty(field.Name))
                        {
                            fieldElements.Add(field);
                            _techLogger.LogTechnical(new TechLogEntry
                            {
                                RequestId = Guid.NewGuid().ToString(),
                                Endpoint = "SeparateElementsRobust",
                                Level = "Debug",
                                Message = $"FieldElement: {field.Name}"
                            });
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        _techLogger.LogTechnical(new TechLogEntry
                        {
                            RequestId = Guid.NewGuid().ToString(),
                            Endpoint = "SeparateElementsRobust",
                            Level = "Error",
                            Message = $"Erro FieldElement: {ex.Message}"
                        });
                    }
                }

                if (isLineElement)
                {
                    try
                    {
                        // ✅ CORREÇÃO: Desserializar como LineElement
                        var childLine = JsonConvert.DeserializeObject<LineElement>(elementJson);

                        if (childLine != null)
                        {
                            _techLogger.LogTechnical(new TechLogEntry
                            {
                                RequestId = Guid.NewGuid().ToString(),
                                Endpoint = "SeparateElementsRobust",
                                Level = "Debug",
                                Message = $" LineElement desserializado: Name='{childLine.Name}', InitialValue='{childLine.InitialValue}', Elements.Count={childLine.Elements?.Count}"
                            });

                            if (!string.IsNullOrEmpty(childLine.Name) && childLine.Name != lineConfig.Name)
                            {
                                childLineElements.Add(childLine);
                                _techLogger.LogTechnical(new TechLogEntry
                                {
                                    RequestId = Guid.NewGuid().ToString(),
                                    Endpoint = "SeparateElementsRobust",
                                    Level = "Info",
                                    Message = $"LineElement FILHO ADICIONADO: {childLine.Name}"
                                });
                            }
                            else
                            {
                                _techLogger.LogTechnical(new TechLogEntry
                                {
                                    RequestId = Guid.NewGuid().ToString(),
                                    Endpoint = "SeparateElementsRobust",
                                    Level = "Debug",
                                    Message = $"LineElement ignorado (mesmo nome ou vazio): {childLine.Name}"
                                });
                            }
                            continue;
                        }
                        else
                        {
                            _techLogger.LogTechnical(new TechLogEntry
                            {
                                RequestId = Guid.NewGuid().ToString(),
                                Endpoint = "SeparateElementsRobust",
                                Level = "Error",
                                Message = $"LineElement desserializado como NULL"
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _techLogger.LogTechnical(new TechLogEntry
                        {
                            RequestId = Guid.NewGuid().ToString(),
                            Endpoint = "SeparateElementsRobust",
                            Level = "Error",
                            Message = $"Erro LineElement: {ex.Message}\nJSON: {elementJson}"
                        });
                    }
                }

                // Elemento não identificado
                if (!isFieldElement && !isLineElement)
                {
                    _techLogger.LogTechnical(new TechLogEntry
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        Endpoint = "SeparateElementsRobust",
                        Level = "Warn",
                        Message = $" Elemento não identificado: {elementJson.Substring(0, Math.Min(200, elementJson.Length))}"
                    });
                }
            }

            _techLogger.LogTechnical(new TechLogEntry
            {
                RequestId = Guid.NewGuid().ToString(),
                Endpoint = "SeparateElementsRobust",
                Level = "Info",
                Message = $" SEPARAÇÃO FINAL EM {lineConfig.Name}: {fieldElements.Count} FieldElements, {childLineElements.Count} LineElements filhos"
            });

            // ✅ DEBUG: Listar todos os filhos encontrados
            if (childLineElements.Any())
            {
                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "SeparateElementsRobust",
                    Level = "Info",
                    Message = $" FILHOS ENCONTRADOS: {string.Join(", ", childLineElements.Select(f => f.Name))}"
                });
            }

            return (fieldElements, childLineElements);
        }

        // ✅ NOVO MÉTODO: Debug da estrutura do LineElement
        private void DebugLineElementStructure(LineElement line, string context)
        {
            try
            {
                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "DebugLineElementStructure",
                    Level = "Debug",
                    Message = $"{context} - Name: '{line.Name}', InitialValue: '{line.InitialValue}', Elements.Count: {line.Elements?.Count}"
                });

                if (line.Elements != null)
                {
                    for (int i = 0; i < Math.Min(line.Elements.Count, 3); i++) // Mostrar apenas os primeiros 3
                    {
                        var element = line.Elements[i];
                        _techLogger.LogTechnical(new TechLogEntry
                        {
                            RequestId = Guid.NewGuid().ToString(),
                            Endpoint = "DebugLineElementStructure",
                            Level = "Debug",
                            Message = $"  Elemento {i + 1}: {element.Substring(0, Math.Min(100, element.Length))}"
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "DebugLineElementStructure",
                    Level = "Error",
                    Message = $"Erro no debug: {ex.Message}"
                });
            }
        }

        private void ValidateCompleteLayout(Layout layout)
        {
            try
            {
                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ValidateCompleteLayout",
                    Level = "Info",
                    Message = "=== VALIDAÇÃO COMPLETA DO LAYOUT ==="
                });

                if (layout?.Elements == null)
                {
                    _techLogger.LogTechnical(new TechLogEntry
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        Endpoint = "ValidateCompleteLayout",
                        Level = "Error",
                        Message = "Layout ou Elements é nulo"
                    });
                    return;
                }

                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ValidateCompleteLayout",
                    Level = "Info",
                    Message = $"Total de linhas no layout: {layout.Elements.Count}"
                });

                // ✅ NOVO: Lista para coletar resultados
                var validationResults = new List<LineValidationResult>();

                foreach (var line in layout.Elements)
                {
                    ValidateLineLayoutWithResult(line, validationResults);
                }

                // ✅ NOVO: Mostrar resumo final
                ShowValidationSummary(validationResults);

                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ValidateCompleteLayout",
                    Level = "Info",
                    Message = "=== FIM DA VALIDAÇÃO ==="
                });

            }
            catch (Exception ex)
            {
                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ValidateCompleteLayout",
                    Level = "Error",
                    Message = $"Erro na validação completa: {ex.Message}"
                });
            }
        }

        private void ValidateLineLayoutWithResult(LineElement lineConfig, List<LineValidationResult> results)
        {
            try
            {
                var (fieldElements, childLineElements) = SeparateElementsRobust(lineConfig);

                int initialValueLength = !string.IsNullOrEmpty(lineConfig.InitialValue) ? lineConfig.InitialValue.Length : 0;

                // Calcular todos os FieldElements EXCETO "Sequencia" (que pertence à próxima linha)
                var fieldsToCalculate = fieldElements
                    .Where(f => !f.Name.Equals("Sequencia", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f.Sequence)
                    .ToList();

                int fieldsLength = fieldsToCalculate.Sum(f => f.LengthField);

                // Adicionar 6 chars (Sequencia da linha anterior) para todas as linhas EXCETO HEADER
                int sequenceFromPreviousLine = lineConfig.Name?.Equals("HEADER", StringComparison.OrdinalIgnoreCase) == true ? 0 : 6;

                int totalLength = initialValueLength + fieldsLength + sequenceFromPreviousLine;

                bool hasChildren = childLineElements.Any();
                bool isValid = hasChildren ? totalLength <= 600 : totalLength == 600;

                // ✅ Coletar resultado
                var result = new LineValidationResult
                {
                    LineName = lineConfig.Name,
                    InitialValue = lineConfig.InitialValue,
                    TotalLength = totalLength,
                    IsValid = isValid,
                    HasChildren = hasChildren,
                    FieldCount = fieldsToCalculate.Count,
                    ChildCount = childLineElements.Count
                };

                results.Add(result);

                // Log normal da validação (mantém o que já existe)
                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ValidateLineLayout",
                    Level = "Info",
                    Message = $"=== VALIDAÇÃO DO LAYOUT: {lineConfig.Name} ==="
                });

                // Validar elementos filhos recursivamente
                if (childLineElements.Any())
                {
                    foreach (var childLine in childLineElements)
                    {
                        ValidateLineLayoutWithResult(childLine, results);
                    }
                }

            }
            catch (Exception ex)
            {
                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ValidateLineLayout",
                    Level = "Error",
                    Message = $"Erro na validação do layout {lineConfig.Name}: {ex.Message}"
                });
            }
        }

        private void ShowValidationSummary(List<LineValidationResult> results)
        {
            _techLogger.LogTechnical(new TechLogEntry
            {
                RequestId = Guid.NewGuid().ToString(),
                Endpoint = "ValidateCompleteLayout",
                Level = "Info",
                Message = "=== RESUMO DA VALIDAÇÃO ==="
            });

            // Linhas válidas (exatamente 600 caracteres)
            var validLines = results.Where(r => r.IsValid && !r.HasChildren && r.TotalLength == 600).ToList();
            if (validLines.Any())
            {
                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ValidateCompleteLayout",
                    Level = "Info",
                    Message = $"LINHAS VÁLIDAS (600 caracteres): {validLines.Count}"
                });

                foreach (var line in validLines.OrderBy(r => r.LineName))
                {
                    _techLogger.LogTechnical(new TechLogEntry
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        Endpoint = "ValidateCompleteLayout",
                        Level = "Info",
                        Message = $"{line.LineName}: {line.TotalLength} chars (InitialValue: '{line.InitialValue}', Campos: {line.FieldCount})"
                    });
                }
            }

            // Linhas com filhos (não precisam ter 600)
            var linesWithChildren = results.Where(r => r.HasChildren).ToList();
            if (linesWithChildren.Any())
            {
                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ValidateCompleteLayout",
                    Level = "Info",
                    Message = $"LINHAS COM ELEMENTOS FILHOS: {linesWithChildren.Count}"
                });

                foreach (var line in linesWithChildren.OrderBy(r => r.LineName))
                {
                    _techLogger.LogTechnical(new TechLogEntry
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        Endpoint = "ValidateCompleteLayout",
                        Level = "Info",
                        Message = $"{line.LineName}: {line.TotalLength} chars (Filhos: {line.ChildCount}, Campos próprios: {line.FieldCount})"
                    });
                }
            }

            // Linhas inválidas (não têm 600 caracteres)
            var invalidLines = results.Where(r => !r.IsValid && !r.HasChildren).ToList();
            if (invalidLines.Any())
            {
                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ValidateCompleteLayout",
                    Level = "Error",
                    Message = $"LINHAS INVÁLIDAS: {invalidLines.Count}"
                });

                foreach (var line in invalidLines.OrderBy(r => r.LineName))
                {
                    int difference = 600 - line.TotalLength;
                    _techLogger.LogTechnical(new TechLogEntry
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        Endpoint = "ValidateCompleteLayout",
                        Level = "Error",
                        Message = $"{line.LineName}: {line.TotalLength} chars ({(difference > 0 ? "FALTAM" : "SOBRAM")} {Math.Abs(difference)}) - InitialValue: '{line.InitialValue}'"
                    });
                }
            }

            // Linhas válidas mas com tamanho diferente de 600 (com filhos)
            var validNonStandardLines = results.Where(r => r.IsValid && r.HasChildren && r.TotalLength != 600).ToList();
            if (validNonStandardLines.Any())
            {
                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ValidateCompleteLayout",
                    Level = "Warn",
                    Message = $"LINHAS COM FILHOS (tamanho variável): {validNonStandardLines.Count}"
                });

                foreach (var line in validNonStandardLines.OrderBy(r => r.LineName))
                {
                    _techLogger.LogTechnical(new TechLogEntry
                    {
                        RequestId = Guid.NewGuid().ToString(),
                        Endpoint = "ValidateCompleteLayout",
                        Level = "Warn",
                        Message = $"{line.LineName}: {line.TotalLength} chars (Filhos: {line.ChildCount}, Campos próprios: {line.FieldCount})"
                    });
                }
            }

            // Estatísticas finais
            _techLogger.LogTechnical(new TechLogEntry
            {
                RequestId = Guid.NewGuid().ToString(),
                Endpoint = "ValidateCompleteLayout",
                Level = "Info",
                Message = $"ESTATÍSTICAS: Total de {results.Count} linhas validadas - {validLines.Count} válidas, {invalidLines.Count} inválidas, {linesWithChildren.Count} com filhos"
            });
        }

        /// <summary>
        /// Calcula validações e posições dos campos para cada linha do layout
        /// </summary>
        public List<LineValidationInfo> CalculateLineValidations(Layout layout, int expectedLineLength = 600)
        {
            var lineValidations = new List<LineValidationInfo>();

            if (layout?.Elements == null)
                return lineValidations;

            foreach (var lineElement in layout.Elements)
            {
                CalculateLineValidationRecursive(lineElement, lineValidations, expectedLineLength);
            }

            return lineValidations;
        }

        private void CalculateLineValidationRecursive(LineElement lineConfig, List<LineValidationInfo> validations, int expectedLineLength)
        {
            try
            {
                var (fieldElements, childLineElements) = SeparateElementsRobust(lineConfig);

                int initialValueLength = !string.IsNullOrEmpty(lineConfig.InitialValue) ? lineConfig.InitialValue.Length : 0;

                // Separar campos normais da tag Sequencia
                var fieldsToCalculate = fieldElements
                    .Where(f => !f.Name.Equals("Sequencia", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f.Sequence)
                    .ToList();

                // Buscar tag Sequencia (pertence à próxima linha, mas completa esta até 600)
                var sequenciaField = fieldElements
                    .FirstOrDefault(f => f.Name.Equals("Sequencia", StringComparison.OrdinalIgnoreCase));
                int sequenciaLength = sequenciaField?.LengthField ?? 6;

                int fieldsLength = fieldsToCalculate.Sum(f => f.LengthField);

                // Adicionar 6 chars (Sequencia da linha anterior) para todas as linhas EXCETO HEADER
                int sequenceFromPreviousLine = lineConfig.Name?.Equals("HEADER", StringComparison.OrdinalIgnoreCase) == true ? 0 : 6;

                // Total = initialValue + campos (sem Sequencia própria) + sequencia anterior + sequencia própria
                int totalLength = initialValueLength + fieldsLength + sequenceFromPreviousLine + sequenciaLength;

                bool hasChildren = childLineElements.Any();
                bool isValid = hasChildren ? totalLength <= expectedLineLength : totalLength == expectedLineLength;

                // Calcular posições dos campos (1-based)
                var calculatedPositions = new Dictionary<string, int>();
                int currentPosition = sequenceFromPreviousLine + initialValueLength;

                foreach (var field in fieldsToCalculate)
                {
                    calculatedPositions[field.Name] = currentPosition + 1; // 1-based
                    currentPosition += field.LengthField;
                }

                var validationInfo = new LineValidationInfo
                {
                    LineName = lineConfig.Name,
                    InitialValue = lineConfig.InitialValue ?? "",
                    InitialValueLength = initialValueLength,
                    SequenceFromPreviousLine = sequenceFromPreviousLine,
                    FieldsLength = fieldsLength,
                    SequenciaLength = sequenciaLength,
                    TotalLength = totalLength,
                    IsValid = isValid,
                    HasChildren = hasChildren,
                    FieldCount = fieldsToCalculate.Count,
                    CalculatedPositions = calculatedPositions
                };

                validations.Add(validationInfo);

                // Processar elementos filhos recursivamente
                if (childLineElements.Any())
                {
                    foreach (var childLine in childLineElements)
                    {
                        CalculateLineValidationRecursive(childLine, validations, expectedLineLength);
                    }
                }
            }
            catch (Exception ex)
            {
                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "CalculateLineValidationRecursive",
                    Level = "Error",
                    Message = $"Erro ao calcular validação para {lineConfig.Name}: {ex.Message}"
                });
            }
        }

        /// <summary>
        /// Parseia XML do layout para objeto Layout (sem precisar de arquivo txt)
        /// </summary>
        public async Task<Layout?> ParseLayoutFromXmlAsync(string xmlContent)
        {
            try
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xmlContent));
                var layout = await LoadLayoutAsync(stream);
                
                if (layout != null)
                {
                    // Reestruturar e reordenar o layout
                    var reestruturado = ReestruturarLayout(layout);
                    var reordenado = ReordenarSequences(reestruturado);
                    return reordenado;
                }
                
                return null;
            }
            catch (Exception ex)
            {
                _techLogger.LogTechnical(new TechLogEntry
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Endpoint = "ParseLayoutFromXmlAsync",
                    Level = "Error",
                    Message = $"Erro ao parsear XML do layout: {ex.Message}"
                });
                return null;
            }
        }
    }
}