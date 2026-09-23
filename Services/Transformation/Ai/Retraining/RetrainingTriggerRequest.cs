using System.Text.Json.Serialization;

namespace LayoutParserApi.Services.Transformation.Ai.Retraining
{
    /// <summary>
    /// Payload do arquivo-marcador (<see cref="RetrainingOptions.TriggerFilePath"/>) que a API
    /// escreve quando o gatilho de retraining dispara. O cron/script da VM observa este arquivo,
    /// roda <c>train_lora.py</c> e o REMOVE ao terminar — é assim que a API detecta a conclusão.
    /// Só dados de contexto; nenhum segredo.
    /// </summary>
    public class RetrainingTriggerRequest
    {
        [JsonPropertyName("requestedUtc")]
        public string RequestedUtc { get; set; } = string.Empty;

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = string.Empty;

        [JsonPropertyName("explanation")]
        public string Explanation { get; set; } = string.Empty;

        [JsonPropertyName("examplesSinceLastTraining")]
        public int ExamplesSinceLastTraining { get; set; }

        [JsonPropertyName("threshold")]
        public int Threshold { get; set; }

        [JsonPropertyName("lastTrainingCompletedUtc")]
        public string? LastTrainingCompletedUtc { get; set; }

        /// <summary>Nota operacional pro humano/script que ler o marcador.</summary>
        [JsonPropertyName("note")]
        public string Note { get; set; } =
            "Marcador escrito por LayoutParserApi (F4.2). O script da VM deve criar o retraining.lock " +
            "antes de iniciar train_lora.py, e APAGAR este arquivo (e o lock) ao concluir. " +
            "Após o treino, rodar MetricsBatchRunner --mode=metrics-batch contra o held-out atual e " +
            "só promover o modelo novo se as métricas não regredirem (ver ModelMetricsComparer).";
    }
}
