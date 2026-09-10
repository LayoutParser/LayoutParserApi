using System.Text.Json.Serialization;

namespace LayoutParserApi.Services.Transformation.Ai.Retraining
{
    /// <summary>
    /// Estado durável do gatilho de retraining (F4.2, issue #351). Serializado como JSON no
    /// caminho de <see cref="RetrainingOptions.StateFilePath"/>. Todos os instantes em UTC.
    /// </summary>
    public class RetrainingState
    {
        /// <summary>
        /// Exemplos novos capturados por F3 (<see cref="TrainingDataCaptureService"/>) desde o
        /// último treino concluído. Incrementado a cada convergência real gravada no JSONL.
        /// </summary>
        [JsonPropertyName("examplesSinceLastTraining")]
        public int ExamplesSinceLastTraining { get; set; }

        /// <summary>Instante em que este estado foi criado pela primeira vez — âncora do teto de
        /// agenda enquanto nenhum treino tiver concluído.</summary>
        [JsonPropertyName("createdUtc")]
        public DateTime CreatedUtc { get; set; }

        /// <summary>Instante do último treino concluído (marcador de disparo removido + lock livre).
        /// <c>null</c> = nunca treinou por este mecanismo.</summary>
        [JsonPropertyName("lastTrainingCompletedUtc")]
        public DateTime? LastTrainingCompletedUtc { get; set; }

        /// <summary>Instante do último disparo escrito (arquivo-marcador criado).</summary>
        [JsonPropertyName("lastTriggeredUtc")]
        public DateTime? LastTriggeredUtc { get; set; }

        /// <summary><c>true</c> entre o disparo e a detecção da conclusão — evita disparo duplicado
        /// enquanto o treino de ~40h roda.</summary>
        [JsonPropertyName("triggerPending")]
        public bool TriggerPending { get; set; }

        /// <summary>Valor de <see cref="ExamplesSinceLastTraining"/> no instante do disparo — na
        /// conclusão, só ESTE tanto é zerado, preservando o que F3 capturou durante o treino.</summary>
        [JsonPropertyName("examplesCountedAtTrigger")]
        public int ExamplesCountedAtTrigger { get; set; }

        /// <summary>Motivo do último disparo (volume ou teto de agenda), pra auditoria.</summary>
        [JsonPropertyName("lastTriggerReason")]
        public string? LastTriggerReason { get; set; }
    }
}
