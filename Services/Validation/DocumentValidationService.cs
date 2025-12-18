using LayoutParserApi.Models.Validation;
using LayoutParserApi.Services.Interfaces;

namespace LayoutParserApi.Services.Validation
{
    /// <summary>
    /// Serviço para validação de documentos TXT - valida linhas baseado em sequência de 6 dígitos
    /// </summary>
    public class DocumentValidationService
    {
        private readonly ITechLogger _techLogger;
        private readonly ILogger<DocumentValidationService> _logger;

        public DocumentValidationService(
            ITechLogger techLogger,
            ILogger<DocumentValidationService> logger)
        {
            _techLogger = techLogger;
            _logger = logger;
        }

        /// <summary>
        /// Valida um documento TXT baseado nas sequências (6 dígitos) - cada linha deve ter exatamente 600 caracteres
        /// </summary>
        /// <param name="documentContent">Conteúdo completo do documento TXT</param>
        /// <returns>Resultado da validação com lista de erros encontrados</returns>
        public DocumentValidationResult ValidateDocument(string documentContent)
        {
            var result = new DocumentValidationResult();

            try
            {
                if (string.IsNullOrEmpty(documentContent))
                {
                    result.ErrorMessage = "Documento vazio";
                    result.IsValid = false;
                    return result;
                }

                // Remover quebras de linha para análise contínua
                var cleanText = documentContent.Replace("\r", "").Replace("\n", "");

                if (cleanText.Length < 600)
                {
                    result.ErrorMessage = $"Documento muito pequeno ({cleanText.Length} caracteres, mínimo 600)";
                    result.IsValid = false;
                    return result;
                }

                // Validar linha por linha baseado na sequência
                int currentPosition = 0;
                int lineIndex = 0;
                string? previousSequence = null;

                while (currentPosition < cleanText.Length)
                {
                    // Extrair sequência (primeiros 6 caracteres da linha)
                    if (currentPosition + 6 > cleanText.Length)
                    {
                        // Linha incompleta
                        result.LineErrors.Add(new DocumentLineError
                        {
                            LineIndex = lineIndex,
                            Sequence = cleanText.Substring(currentPosition),
                            StartPosition = currentPosition,
                            EndPosition = cleanText.Length - 1,
                            ActualLength = cleanText.Length - currentPosition,
                            ExpectedLength = 600,
                            ErrorMessage = $"Linha incompleta: sequência não encontrada ou incompleta na posição {currentPosition}"
                        });
                        result.ProcessingStopped = true;
                        break;
                    }

                    string currentSequence = cleanText.Substring(currentPosition, 6);
                    int lineStart = currentPosition;
                    int expectedLineEnd = currentPosition + 600;

                    // Verificar se linha cabe no documento
                    if (expectedLineEnd > cleanText.Length)
                    {
                        // Última linha pode ser menor, mas ainda é erro se não for exatamente o resto
                        int actualLength = cleanText.Length - currentPosition;
                        result.LineErrors.Add(new DocumentLineError
                        {
                            LineIndex = lineIndex,
                            Sequence = currentSequence,
                            StartPosition = lineStart,
                            EndPosition = cleanText.Length - 1,
                            ActualLength = actualLength,
                            ExpectedLength = 600,
                            ErrorMessage = $"Última linha tem {actualLength} caracteres (esperado: 600). Faltam {600 - actualLength} caracteres."
                        });
                        result.InvalidLinesCount++;
                        result.ProcessingStopped = true;
                        break;
                    }

                    // Extrair linha completa (600 caracteres esperados)
                    // Se linha exceder, pegar até onde conseguir (para detectar excedente)
                    int maxExtractLength = Math.Min(610, cleanText.Length - currentPosition); // Pega um pouco mais para detectar excesso
                    string currentLine = cleanText.Substring(currentPosition, maxExtractLength);
                    int actualLineLength = currentLine.Length;
                    
                    // ✅ VALIDAR: Verificar se próxima sequência está na posição correta (600 chars depois)
                    // A regra: (000001) sequencial(6) + (051) linha(3) = 9 posições iniciais
                    // Depois disso começam os campos. Total deve ser 600.
                    bool isLineLengthCorrect = true;
                    bool lineExceedsLimit = false;
                    int excessChars = 0;
                    
                    // Verificar se linha excede 600 caracteres
                    if (actualLineLength > 600)
                    {
                        lineExceedsLimit = true;
                        excessChars = actualLineLength - 600;
                        isLineLengthCorrect = false;
                    }
                    else if (expectedLineEnd < cleanText.Length)
                    {
                        // Verificar se próxima sequência está na posição esperada (600 chars depois)
                        string nextSequenceAtExpected = cleanText.Substring(expectedLineEnd, Math.Min(6, cleanText.Length - expectedLineEnd));
                        
                        // Se próxima sequência não for válida onde esperado, linha atual pode ter excedido
                        if (nextSequenceAtExpected.Length == 6)
                        {
                            if (!IsValidSequence(nextSequenceAtExpected))
                            {
                                // Próxima sequência não é válida onde esperado → linha atual provavelmente excedeu
                                // Tentar encontrar sequência válida logo após
                                bool foundValidSequenceNearby = false;
                                for (int offset = 1; offset <= 10 && expectedLineEnd + offset + 6 <= cleanText.Length; offset++)
                                {
                                    string checkSequence = cleanText.Substring(expectedLineEnd + offset, 6);
                                    if (IsValidSequence(checkSequence))
                                    {
                                        foundValidSequenceNearby = true;
                                        excessChars = offset; // Linha excedeu em 'offset' caracteres
                                        break;
                                    }
                                }
                                
                                if (!foundValidSequenceNearby)
                                {
                                    // Não encontrou sequência válida próxima → linha atual está incorreta
                                    isLineLengthCorrect = false;
                                }
                            }
                        }
                    }
                    
                    // Validar comprimento da linha
                    if (actualLineLength != 600 || !isLineLengthCorrect || lineExceedsLimit)
                    {
                        // Ajustar actualLineLength se estiver excedendo
                        int reportedLength = lineExceedsLimit ? actualLineLength : 600;
                        
                        result.LineErrors.Add(new DocumentLineError
                        {
                            LineIndex = lineIndex,
                            Sequence = currentSequence,
                            StartPosition = lineStart,
                            EndPosition = lineStart + reportedLength - 1,
                            ActualLength = reportedLength,
                            ExpectedLength = 600,
                            ErrorMessage = lineExceedsLimit 
                                ? $"Linha excede 600 caracteres: {reportedLength} caracteres encontrados (excedendo em {excessChars} caracteres). A partir desta linha, o documento está desalinhado."
                                : $"Linha tem tamanho incorreto: {reportedLength} caracteres (esperado: 600). Próxima sequência não encontrada na posição esperada."
                        });
                        result.InvalidLinesCount++;
                    }

                    // Validar sequência (deve ser numérica de 6 dígitos ou "HEADER")
                    if (currentSequence == "HEADER")
                    {
                        // HEADER é válido
                        if (previousSequence != null && previousSequence != "HEADER")
                        {
                            result.LineErrors.Add(new DocumentLineError
                            {
                                LineIndex = lineIndex,
                                Sequence = currentSequence,
                                StartPosition = lineStart,
                                EndPosition = expectedLineEnd - 1,
                                ActualLength = 600,
                                ExpectedLength = 600,
                                ErrorMessage = "HEADER encontrado fora da primeira linha"
                            });
                            result.InvalidLinesCount++;
                        }
                        previousSequence = "HEADER";
                    }
                    else if (!IsValidSequence(currentSequence))
                    {
                        result.LineErrors.Add(new DocumentLineError
                        {
                            LineIndex = lineIndex,
                            Sequence = currentSequence,
                            StartPosition = lineStart,
                            EndPosition = expectedLineEnd - 1,
                            ActualLength = 600,
                            ExpectedLength = 600,
                            ErrorMessage = $"Sequência inválida: '{currentSequence}' (deve ser numérica de 6 dígitos ou 'HEADER')"
                        });
                        result.InvalidLinesCount++;
                    }
                    else
                    {
                        // Validar sequência sequencial
                        if (previousSequence != null && previousSequence != "HEADER")
                        {
                            if (!IsSequentialSequence(previousSequence, currentSequence))
                            {
                                result.LineErrors.Add(new DocumentLineError
                                {
                                    LineIndex = lineIndex,
                                    Sequence = currentSequence,
                                    StartPosition = lineStart,
                                    EndPosition = expectedLineEnd - 1,
                                    ActualLength = 600,
                                    ExpectedLength = 600,
                                    ExpectedNextSequence = CalculateNextSequence(previousSequence),
                                    ErrorMessage = $"Sequência fora de ordem: esperado '{CalculateNextSequence(previousSequence)}', encontrado '{currentSequence}'"
                                });
                                result.InvalidLinesCount++;
                            }
                        }
                        previousSequence = currentSequence;
                    }

                    // ✅ Contar linhas válidas apenas se não houver erro nesta linha
                    bool hasErrorInThisLine = result.LineErrors.Any(e => e.LineIndex == lineIndex);
                    
                    if (!hasErrorInThisLine)
                    {
                        // Verificar próxima sequência (se houver mais conteúdo)
                        if (expectedLineEnd < cleanText.Length)
                        {
                            string nextSequence = cleanText.Substring(expectedLineEnd, Math.Min(6, cleanText.Length - expectedLineEnd));

                            // Se próxima sequência não começa na posição esperada, linha está incorreta
                            if (nextSequence.Length == 6 && IsValidSequence(nextSequence))
                            {
                                // Próxima sequência encontrada corretamente
                                result.ValidLinesCount++;
                            }
                            else if (nextSequence.Length < 6)
                            {
                                // Fim do documento
                                result.ValidLinesCount++;
                            }
                        }
                        else
                        {
                            // Última linha
                            result.ValidLinesCount++;
                        }
                    }
                    
                    // ✅ Se linha atual tem erro, todas as linhas seguintes também terão (desalinhamento)
                    // Mas continuamos processando para marcar todas
                    
                    // Avançar para próxima linha (sempre avançar 600, mesmo se houver erro)
                    currentPosition = expectedLineEnd;
                    lineIndex++;
                    result.TotalLinesProcessed++;
                    
                    // Se chegou ao fim do documento
                    if (currentPosition >= cleanText.Length)
                    {
                        break;
                    }
                }

                result.IsValid = result.LineErrors.Count == 0;
                if (result.IsValid)
                {
                    result.ErrorMessage = "Documento válido";
                }
                else
                {
                    result.ErrorMessage = $"Encontrados {result.LineErrors.Count} erro(s) em {result.InvalidLinesCount} linha(s)";
                }

                _logger.LogInformation("Validação de documento concluída: {TotalLines} linhas processadas, {ValidLines} válidas, {InvalidLines} inválidas",
                    result.TotalLinesProcessed, result.ValidLinesCount, result.InvalidLinesCount);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao validar documento");
                result.IsValid = false;
                result.ErrorMessage = $"Erro na validação: {ex.Message}";
                return result;
            }
        }

        /// <summary>
        /// Valida se a string é uma sequência válida (6 dígitos numéricos ou "HEADER")
        /// </summary>
        private bool IsValidSequence(string sequence)
        {
            if (sequence == "HEADER")
                return true;

            if (sequence.Length != 6)
                return false;

            return sequence.All(char.IsDigit);
        }

        /// <summary>
        /// Verifica se a sequência atual segue sequencialmente a anterior
        /// </summary>
        private bool IsSequentialSequence(string previousSequence, string currentSequence)
        {
            if (previousSequence == "HEADER")
                return currentSequence == "000001"; // HEADER deve ser seguido de 000001

            if (!int.TryParse(previousSequence, out int prevNum) || !int.TryParse(currentSequence, out int currNum))
                return false;

            return currNum == prevNum + 1;
        }

        /// <summary>
        /// Calcula a próxima sequência esperada
        /// </summary>
        private string CalculateNextSequence(string currentSequence)
        {
            if (currentSequence == "HEADER")
                return "000001";

            if (int.TryParse(currentSequence, out int num))
            {
                return (num + 1).ToString("D6");
            }

            return "??????";
        }
    }
}

