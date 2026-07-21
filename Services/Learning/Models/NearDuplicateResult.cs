namespace LayoutParserApi.Services.Learning.Models
{
    /// <summary>
    /// Resultado da checagem de near-duplicate (item 4.4 do dispatch de IA) de um texto
    /// "sintético" contra um corpus de documentos REAIS - requisito de design obrigatório,
    /// não nice-to-have: este projeto é material de TCC, que circula mais solto que dado
    /// real controlado. "Sintético" não pode mascarar valor real copiado verbatim.
    /// </summary>
    public class NearDuplicateResult
    {
        /// <summary>Maior similaridade (Jaccard sobre shingles de palavras, 0..1) encontrada contra QUALQUER item do corpus.</summary>
        public double MaiorSimilaridade { get; set; }

        /// <summary>Índice (na lista de corpus informada) do item mais parecido, ou -1 se o corpus estava vazio.</summary>
        public int IndiceMaisParecido { get; set; } = -1;

        /// <summary>
        /// true quando <see cref="MaiorSimilaridade"/> ultrapassa o limiar configurado - o
        /// texto NÃO deve ser tratado/indexado como "sintético" (risco real de vazar dado
        /// verbatim do corpus real). Sempre revisar manualmente antes de publicar, este é um
        /// filtro de primeira linha, não veredito jurídico.
        /// </summary>
        public bool EhNearDuplicate { get; set; }
    }
}
