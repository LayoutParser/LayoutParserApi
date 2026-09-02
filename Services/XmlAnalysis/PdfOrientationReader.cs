using UglyToad.PdfPig;

namespace LayoutParserApi.Services.XmlAnalysis
{
    /// <summary>
    /// Lê o conteúdo de PDFs de orientação (manuais/notas técnicas da SEFAZ) e extrai os
    /// trechos relevantes para um conjunto de erros de validação XSD (issue #172).
    /// </summary>
    /// <remarks>
    /// Biblioteca escolhida: PdfPig (Apache-2.0) — mesma já validada em
    /// <c>ai/XslSynth/NtPipeline/PdfSmokeExtractor.cs</c> (protótipo B5 P-2). Evita
    /// PdfSharp/iTextSharp, que têm licenciamento AGPL/comercial em versões recentes.
    /// Resiliência: qualquer falha de leitura (arquivo corrompido, biblioteca indisponível,
    /// PDF protegido) é capturada e devolvida como <see cref="PdfOrientationReadResult"/>
    /// vazio/sem sucesso — o chamador (<see cref="XsdValidationService"/>) decide o fallback
    /// para a mensagem genérica, nunca deixamos uma exceção subir e quebrar a resposta de
    /// validação (princípio central do projeto, ver .claude/rules/dotnet-standards.md).
    /// </remarks>
    public class PdfOrientationReader
    {
        private readonly ILogger<PdfOrientationReader> _logger;

        // Quantidade de caracteres de contexto ao redor de cada ocorrência de erro/palavra-chave
        // encontrada no PDF — grande o suficiente para dar contexto útil, pequeno o suficiente
        // para não devolver o PDF inteiro numa única orientação.
        private const int JanelaDeContexto = 280;

        // Limite de trechos devolvidos por PDF — evita que um PDF com muitas ocorrências do
        // mesmo termo (comum em manuais grandes) sature a resposta.
        private const int MaxTrechosPorArquivo = 5;

        public PdfOrientationReader(ILogger<PdfOrientationReader> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Lê todos os PDFs da pasta informada e extrai trechos relevantes para os códigos/termos
        /// de erro informados. Quando <paramref name="errorCodes"/> é vazio, devolve um resumo
        /// (início do texto de cada PDF) em vez de tentar casar contra um erro específico.
        /// </summary>
        public PdfOrientationReadResult ReadOrientations(string pdfFolderPath, IReadOnlyList<string>? errorCodes)
        {
            var result = new PdfOrientationReadResult();

            IReadOnlyList<string> arquivosPdf;
            try
            {
                arquivosPdf = Directory.EnumerateFiles(pdfFolderPath, "*.pdf", SearchOption.TopDirectoryOnly).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao listar PDFs de orientação em {Pasta}", pdfFolderPath);
                return result;
            }

            if (arquivosPdf.Count == 0)
            {
                _logger.LogInformation("Nenhum PDF de orientação encontrado em {Pasta}", pdfFolderPath);
                return result;
            }

            foreach (var caminho in arquivosPdf)
            {
                try
                {
                    var texto = ExtrairTexto(caminho);
                    if (string.IsNullOrWhiteSpace(texto))
                        continue;

                    var trechos = errorCodes is { Count: > 0 }
                        ? ExtrairTrechosRelevantes(texto, errorCodes)
                        : new List<string> { ResumoInicial(texto) };

                    if (trechos.Count > 0)
                    {
                        result.Trechos.AddRange(trechos.Select(t => new PdfOrientationTrecho(Path.GetFileName(caminho), t)));
                        result.Success = true;
                    }
                }
                catch (Exception ex)
                {
                    // Best-effort por arquivo: um PDF corrompido não pode impedir a leitura dos
                    // demais PDFs válidos da mesma pasta.
                    _logger.LogWarning(ex, "Falha ao ler PDF de orientação: {Arquivo}", caminho);
                }
            }

            return result;
        }

        private static string ExtrairTexto(string caminhoPdf)
        {
            using var documento = PdfDocument.Open(caminhoPdf);
            var texto = new System.Text.StringBuilder();
            foreach (var pagina in documento.GetPages())
                texto.AppendLine(pagina.Text);
            return texto.ToString();
        }

        private static string ResumoInicial(string texto)
        {
            var normalizado = texto.Trim();
            return normalizado.Length > JanelaDeContexto ? normalizado[..JanelaDeContexto] + "…" : normalizado;
        }

        /// <summary>
        /// Busca cada código/termo de erro no texto do PDF (case-insensitive) e devolve uma janela
        /// de contexto ao redor de cada ocorrência — heurística simples de matching por substring,
        /// suficiente para manuais/notas técnicas onde o código de erro aparece perto da explicação.
        /// </summary>
        private static List<string> ExtrairTrechosRelevantes(string texto, IReadOnlyList<string> errorCodes)
        {
            var trechos = new List<string>();

            foreach (var codigo in errorCodes)
            {
                if (string.IsNullOrWhiteSpace(codigo))
                    continue;

                var indice = texto.IndexOf(codigo, StringComparison.OrdinalIgnoreCase);
                if (indice < 0)
                    continue;

                var inicio = Math.Max(0, indice - JanelaDeContexto / 2);
                var fim = Math.Min(texto.Length, indice + codigo.Length + JanelaDeContexto / 2);
                var trecho = texto[inicio..fim].Trim().Replace("\r", "").Replace("\n", " ");

                trechos.Add($"[{codigo}] …{trecho}…");

                if (trechos.Count >= MaxTrechosPorArquivo)
                    break;
            }

            return trechos;
        }
    }

    /// <summary>Trecho de orientação extraído de um PDF, com o arquivo de origem para rastreabilidade.</summary>
    public record PdfOrientationTrecho(string Arquivo, string Texto);

    /// <summary>Resultado da leitura de orientações em PDF (issue #172).</summary>
    public class PdfOrientationReadResult
    {
        public bool Success { get; set; }
        public List<PdfOrientationTrecho> Trechos { get; } = new();
    }
}
