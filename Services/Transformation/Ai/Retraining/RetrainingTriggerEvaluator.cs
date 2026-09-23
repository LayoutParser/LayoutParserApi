namespace LayoutParserApi.Services.Transformation.Ai.Retraining
{
    /// <summary>Motivo pelo qual o retraining foi (ou não) disparado.</summary>
    public enum RetrainingTriggerReason
    {
        /// <summary>Nenhuma condição de disparo satisfeita.</summary>
        None = 0,

        /// <summary>Contador de exemplos novos atingiu <see cref="RetrainingOptions.NewExampleThreshold"/>.</summary>
        VolumeThreshold = 1,

        /// <summary>Teto de agenda (<see cref="RetrainingOptions.MaxDaysBetweenTrainings"/>) estourado.</summary>
        ScheduleCeiling = 2,

        /// <summary>Já há um disparo pendente (treino em andamento) — não dispara de novo.</summary>
        AlreadyPending = 3,
    }

    /// <summary>Decisão do avaliador de gatilho.</summary>
    public readonly record struct RetrainingTriggerDecision(bool ShouldTrigger, RetrainingTriggerReason Reason, string Explanation)
    {
        public static RetrainingTriggerDecision Skip(RetrainingTriggerReason reason, string explanation)
            => new(false, reason, explanation);

        public static RetrainingTriggerDecision Fire(RetrainingTriggerReason reason, string explanation)
            => new(true, reason, explanation);
    }

    /// <summary>
    /// Lógica pura do gatilho de retraining (F4.2, issue #351, ADR §6.1). Sem I/O — recebe o
    /// estado e as opções já resolvidas e devolve a decisão. Testável isoladamente.
    ///
    /// <para>Regra: dispara quando <b>(exemplos novos ≥ N)</b> OU <b>(dias desde o último treino ≥
    /// teto)</b>, o que vier primeiro. O teto de agenda só dispara se houver pelo menos 1 exemplo
    /// novo — retreinar com dataset idêntico ao anterior não agrega. Nunca dispara se já houver
    /// um disparo pendente.</para>
    /// </summary>
    public static class RetrainingTriggerEvaluator
    {
        public static RetrainingTriggerDecision Evaluate(RetrainingState state, RetrainingOptions options, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(options);

            if (state.TriggerPending)
                return RetrainingTriggerDecision.Skip(
                    RetrainingTriggerReason.AlreadyPending,
                    "Já há um disparo de retraining pendente (treino em andamento ou marcador não removido).");

            var threshold = options.NewExampleThreshold > 0
                ? options.NewExampleThreshold
                : RetrainingOptions.DefaultNewExampleThreshold;

            var maxDays = options.MaxDaysBetweenTrainings > 0
                ? options.MaxDaysBetweenTrainings
                : RetrainingOptions.DefaultMaxDaysBetweenTrainings;

            if (state.ExamplesSinceLastTraining >= threshold)
                return RetrainingTriggerDecision.Fire(
                    RetrainingTriggerReason.VolumeThreshold,
                    $"Contador de exemplos novos ({state.ExamplesSinceLastTraining}) atingiu o limite ({threshold}).");

            var anchor = state.LastTrainingCompletedUtc ?? state.CreatedUtc;
            var elapsed = nowUtc - anchor;

            if (elapsed >= TimeSpan.FromDays(maxDays) && state.ExamplesSinceLastTraining > 0)
                return RetrainingTriggerDecision.Fire(
                    RetrainingTriggerReason.ScheduleCeiling,
                    $"Passaram {elapsed.TotalDays:F1} dias desde o último treino (teto {maxDays}d) " +
                    $"com {state.ExamplesSinceLastTraining} exemplo(s) novo(s) acumulado(s).");

            return RetrainingTriggerDecision.Skip(
                RetrainingTriggerReason.None,
                $"Sem disparo: {state.ExamplesSinceLastTraining}/{threshold} exemplos novos, " +
                $"{elapsed.TotalDays:F1}/{maxDays} dias desde o último treino.");
        }
    }
}
