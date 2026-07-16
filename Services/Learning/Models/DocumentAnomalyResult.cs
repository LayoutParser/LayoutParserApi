namespace LayoutParserApi.Services.Learning.Models
{
    /// <summary>
    /// Resultado da detecção de anomalia de um documento contra o histórico
    /// de padrões (MLData/DocumentPatterns) do mesmo LayoutGuid.
    /// </summary>
    public class DocumentAnomalyResult
    {
        public string LayoutGuid { get; set; } = "";

        /// <summary>Quantidade de padrões históricos usados na distribuição.</summary>
        public int HistoricalSampleCount { get; set; }

        /// <summary>
        /// False quando há menos amostras que o mínimo exigido — nesse caso
        /// <see cref="AnomalyScore"/> é null e nenhum score é inventado.
        /// </summary>
        public bool HasSufficientData { get; set; }

        /// <summary>
        /// Score de anomalia normalizado em [0..1] (0 = típico, 1 = fortemente anômalo).
        /// Null quando não há dados suficientes para pontuar com honestidade.
        /// </summary>
        public double? AnomalyScore { get; set; }

        /// <summary>True quando <see cref="AnomalyScore"/> ultrapassa o limiar de anomalia.</summary>
        public bool IsAnomalous { get; set; }

        /// <summary>Confiança no score em [0..1], crescente com o nº de amostras históricas.</summary>
        public double Confidence { get; set; }

        /// <summary>Features cujo z-score ultrapassou o limiar de suspeita (|z| >= 2).</summary>
        public List<SuspiciousFeature> SuspiciousFeatures { get; set; } = new();

        /// <summary>Explicação legível (PT-BR) do resultado.</summary>
        public string Explanation { get; set; } = "";
    }

    /// <summary>
    /// Feature individual apontada como suspeita, com o valor observado,
    /// o z-score e a faixa esperada (média ± 2 desvios) do histórico.
    /// </summary>
    public class SuspiciousFeature
    {
        public string FeatureName { get; set; } = "";
        public double Value { get; set; }
        public double ZScore { get; set; }
        public double ExpectedMin { get; set; }
        public double ExpectedMax { get; set; }
    }
}
