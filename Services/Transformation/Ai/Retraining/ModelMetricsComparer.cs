namespace LayoutParserApi.Services.Transformation.Ai.Retraining
{
    /// <summary>
    /// Foto das métricas de um modelo contra o held-out set (saída do
    /// <c>MetricsBatchRunner --mode=metrics-batch</c>). Taxas em 0..1.
    /// </summary>
    public readonly record struct ModelMetricsSnapshot(
        string Model,
        int SampleCount,
        double ConvergenceRate,
        double XsdValidRate,
        double DiffZeroRate);

    /// <summary>Decisão de promoção do modelo recém-treinado.</summary>
    public readonly record struct RetrainingPromotionDecision(bool Promote, IReadOnlyList<string> Reasons);

    /// <summary>
    /// Comparação pós-treino (F4.3, issue #351, ADR §6.3): só promove o modelo novo (troca de
    /// tag/symlink no Ollama, feita no lado da VM) se as métricas contra o held-out atual NÃO
    /// regredirem frente ao modelo em produção. Lógica pura, testável — a execução do
    /// <c>MetricsBatchRunner</c> e o swap do symlink ficam no script da VM (handoff @lp-devops).
    /// </summary>
    public static class ModelMetricsComparer
    {
        /// <summary>
        /// <paramref name="tolerance"/>: quanto de queda numa taxa ainda é aceitável (default 0 =
        /// "não regredir"). Uma folga pequena (ex. 0.01) absorve ruído de amostragem se o held-out
        /// for pequeno.
        /// </summary>
        public static RetrainingPromotionDecision Decide(
            ModelMetricsSnapshot current,
            ModelMetricsSnapshot candidate,
            double tolerance = 0.0)
        {
            var reasons = new List<string>();

            if (candidate.SampleCount <= 0)
            {
                reasons.Add("Candidato sem amostras avaliadas — impossível comparar; não promove.");
                return new RetrainingPromotionDecision(false, reasons);
            }

            if (candidate.SampleCount < current.SampleCount)
                reasons.Add($"Held-out do candidato menor ({candidate.SampleCount}) que o do atual ({current.SampleCount}) — comparação menos confiável.");

            CheckRate("convergência", current.ConvergenceRate, candidate.ConvergenceRate, tolerance, reasons);
            CheckRate("XSD válido", current.XsdValidRate, candidate.XsdValidRate, tolerance, reasons);
            CheckRate("diff==0", current.DiffZeroRate, candidate.DiffZeroRate, tolerance, reasons);

            var regressed = reasons.Exists(r => r.Contains("REGRESSÃO", StringComparison.Ordinal));
            if (!regressed)
                reasons.Add("Nenhuma métrica regrediu além da tolerância — promover o modelo novo.");

            return new RetrainingPromotionDecision(!regressed, reasons);
        }

        private static void CheckRate(string nome, double atual, double novo, double tolerance, List<string> reasons)
        {
            if (novo < atual - tolerance)
                reasons.Add($"REGRESSÃO em {nome}: {atual:P1} → {novo:P1} (queda de {(atual - novo):P1}, tolerância {tolerance:P1}).");
            else
                reasons.Add($"{nome}: {atual:P1} → {novo:P1} (ok).");
        }
    }
}
