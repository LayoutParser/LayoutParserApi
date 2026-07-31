using LayoutParserApi.Models.Logging;

namespace LayoutParserApi.Services.Interfaces
{
    /// <summary>
    /// Contraparte de ESCRITA do <see cref="IAiMetricsReaderService"/>: recebe gerações vindas do
    /// job ai/XslSynth --mode=metrics-batch (que roda noutra máquina — VM Linux 172.25.32.31) e as
    /// grava no MESMO log que o leitor já consome, como linhas "Geracao concluida." com
    /// Source=AiMetrics. Assim o painel do Gap 3 continua tendo uma única fonte da verdade
    /// (o log unificado), sem banco/arquivo paralelo.
    /// Ver §A4 de docs/architecture/handoff-job2-cypress-batch.md.
    /// </summary>
    public interface IAiMetricsIngestService
    {
        /// <summary>Teto de itens aceitos por requisição (defesa contra payload absurdo).</summary>
        int TamanhoMaximoLote { get; }

        /// <summary>
        /// Grava um lote de gerações no log de métricas de IA. Item inválido é ignorado
        /// individualmente (nunca derruba o lote inteiro). Síncrono de propósito: o sink de arquivo
        /// do Serilog é síncrono — devolver <c>Task</c> aqui seria async falso.
        /// </summary>
        /// <param name="geracoes">Itens do lote, na ordem em que devem ser gravados.</param>
        /// <returns>Contagem de recebidos/ingeridos/ignorados e os motivos dos descartes.</returns>
        AiMetricsIngestResult IngestGenerations(IReadOnlyList<AiMetricsGenerationIngestRequest> geracoes);
    }
}
