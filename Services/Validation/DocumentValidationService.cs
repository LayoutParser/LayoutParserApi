using LayoutParserApi.Models.Configuration;
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
        /// Valida um documento TXT baseado nas sequências (6 dígitos) - cada linha deve ter exatamente o tamanho esperado
        /// </summary>
        /// <param name="documentContent">Conteúdo completo do documento TXT</param>
        /// <param name="expectedLineLength">Tamanho esperado de cada linha (default legado, ver <see cref="LineLengthResolver"/>)</param>
        /// <returns>Resultado da validação com lista de erros encontrados</returns>
        public DocumentValidationResult ValidateDocument(string documentContent, int expectedLineLength = LineLengthResolver.LegacyDefaultLineLength)
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

                if (cleanText.Length < expectedLineLength)
                {
                    result.ErrorMessage = $"Documento muito pequeno ({cleanText.Length} caracteres, mínimo {expectedLineLength})";
                    result.IsValid = false;
                    return result;
                }

                // Validar linha por linha baseado na sequência
                int currentPosition = 0;
                int lineIndex = 0;

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
                            ExpectedLength = expectedLineLength,
                            ErrorMessage = $"Linha incompleta: sequência não encontrada ou incompleta na posição {currentPosition}"
                        });
                        result.ProcessingStopped = true;
                        break;
                    }

                    string currentSequence = cleanText.Substring(currentPosition, 6);
                    int lineStart = currentPosition;
                    int expectedLineEnd = currentPosition + expectedLineLength;

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
                            ExpectedLength = expectedLineLength,
                            ErrorMessage = $"Última linha tem {actualLength} caracteres (esperado: {expectedLineLength}). Faltam {expectedLineLength - actualLength} caracteres."
                        });
                        result.InvalidLinesCount++;
                        result.ProcessingStopped = true;
                        break;
                    }

                    string currentLine;
                    int actualLineLength;
                    
                    if (expectedLineEnd <= cleanText.Length)
                    {
                        // Linha completa disponível - extrair exatamente o tamanho esperado
                        currentLine = cleanText.Substring(currentPosition, expectedLineLength);
                        actualLineLength = expectedLineLength;
                    }
                    else
                    {
                        // Última linha pode ser menor
                        currentLine = cleanText.Substring(currentPosition);
                        actualLineLength = currentLine.Length;
                    }
                    
                    // Se próxima sequência não estiver onde esperado, linha atual pode ter excedido o tamanho esperado
                    bool lineExceedsLimit = false;
                    int excessChars = 0;
                    
                    if (expectedLineEnd < cleanText.Length)
                    {
                        // Verificar se próxima sequência está na posição esperada
                        string nextSequenceAtExpected = cleanText.Substring(expectedLineEnd, Math.Min(6, cleanText.Length - expectedLineEnd));
                        
                        if (nextSequenceAtExpected.Length == 6)
                        {
                            // Se próxima sequência não for válida onde esperado, linha atual pode ter excedido
                            if (!IsValidSequence(nextSequenceAtExpected))
                            {
                                // Tentar encontrar sequência válida logo após (tolerância de até 10 caracteres)
                                for (int offset = 1; offset <= 10 && expectedLineEnd + offset + 6 <= cleanText.Length; offset++)
                                {
                                    string checkSequence = cleanText.Substring(expectedLineEnd + offset, 6);
                                    if (IsValidSequence(checkSequence))
                                    {
                                        // Encontrou sequência válida deslocada → linha atual excedeu
                                        excessChars = offset;
                                        lineExceedsLimit = true;
                                        actualLineLength = expectedLineLength + offset; // Ajustar para o tamanho real
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    
                    if (lineExceedsLimit && excessChars > 0)
                    {
                        result.LineErrors.Add(new DocumentLineError
                        {
                            LineIndex = lineIndex,
                            Sequence = currentSequence,
                            StartPosition = lineStart,
                            EndPosition = lineStart + actualLineLength - 1,
                            ActualLength = actualLineLength,
                            ExpectedLength = expectedLineLength,
                            ErrorMessage = $"Linha excede {expectedLineLength} caracteres: {actualLineLength} caracteres encontrados (excedendo em {excessChars} caracteres). A partir desta linha, o documento está desalinhado."
                        });
                        result.InvalidLinesCount++;
                    }

                    // NÃO validar ordem sequencial - apenas validar tamanho da linha
                    if (currentSequence == "HEADER")
                    {
                        // HEADER é válido - apenas validar se está na primeira linha
                        if (lineIndex > 0)
                        {
                            result.LineErrors.Add(new DocumentLineError
                            {
                                LineIndex = lineIndex,
                                Sequence = currentSequence,
                                StartPosition = lineStart,
                                EndPosition = expectedLineEnd - 1,
                                ActualLength = expectedLineLength,
                                ExpectedLength = expectedLineLength,
                                ErrorMessage = "HEADER encontrado fora da primeira linha"
                            });
                            result.InvalidLinesCount++;
                        }
                    }
                    else if (!IsValidSequence(currentSequence))
                    {
                        // Sequência inválida (não é 6 dígitos numéricos)
                        result.LineErrors.Add(new DocumentLineError
                        {
                            LineIndex = lineIndex,
                            Sequence = currentSequence,
                            StartPosition = lineStart,
                            EndPosition = expectedLineEnd - 1,
                            ActualLength = expectedLineLength,
                            ExpectedLength = expectedLineLength,
                            ErrorMessage = $"Sequência inválida: '{currentSequence}' (deve ser numérica de 6 dígitos ou 'HEADER')"
                        });
                        result.InvalidLinesCount++;
                    }

                    bool hasErrorInThisLine = result.LineErrors.Any(e => e.LineIndex == lineIndex);
                    
                    if (!hasErrorInThisLine)
                    {
                        // Verificar próxima sequência (se houver mais conteúdo)
                        if (expectedLineEnd < cleanText.Length)
                        {
                            string nextSequence = cleanText.Substring(expectedLineEnd, Math.Min(6, cleanText.Length - expectedLineEnd));

                            // Se próxima sequência não começa na posição esperada, linha está incorreta
                            if (nextSequence.Length == 6 && IsValidSequence(nextSequence))
                                // Próxima sequência encontrada corretamente
                                result.ValidLinesCount++;
                            else if (nextSequence.Length < 6)
                                // Fim do documento
                                result.ValidLinesCount++;
                        }
                        else
                        {
                            // Última linha
                            result.ValidLinesCount++;
                        }
                    }
                    
                    // Avançar para próxima linha (sempre avançar o tamanho esperado, mesmo se houver erro)
                    currentPosition = expectedLineEnd;
                    lineIndex++;
                    result.TotalLinesProcessed++;
                    
                    // Se chegou ao fim do documento
                    if (currentPosition >= cleanText.Length)
                        break;
                }

                result.IsValid = result.LineErrors.Count == 0;
                if (result.IsValid)
                    result.ErrorMessage = $"Documento válido - todas as linhas têm {expectedLineLength} caracteres";
                else
                    result.ErrorMessage = $"Encontrados {result.LineErrors.Count} erro(s) de tamanho em {result.InvalidLinesCount} linha(s) do documento";

                _logger.LogInformation("Validação de documento concluída: {TotalLines} linhas processadas, {ValidLines} válidas, {InvalidLines} inválidas",result.TotalLinesProcessed, result.ValidLinesCount, result.InvalidLinesCount);

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
    }
}