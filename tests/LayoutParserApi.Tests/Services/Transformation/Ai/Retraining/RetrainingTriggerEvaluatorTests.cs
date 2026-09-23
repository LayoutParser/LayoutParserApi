using LayoutParserApi.Services.Transformation.Ai.Retraining;

namespace LayoutParserApi.Tests.Services.Transformation.Ai.Retraining
{
    /// <summary>Lógica pura do gatilho de retraining (F4.2, issue #351).</summary>
    public class RetrainingTriggerEvaluatorTests
    {
        private static readonly DateTime Now = new(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc);

        private static RetrainingOptions Options(int threshold = 300, int maxDays = 90) => new()
        {
            NewExampleThreshold = threshold,
            MaxDaysBetweenTrainings = maxDays,
        };

        [Fact]
        public void Dispara_por_volume_quando_contador_atinge_o_limite()
        {
            var state = new RetrainingState { CreatedUtc = Now.AddDays(-1), ExamplesSinceLastTraining = 300 };

            var decision = RetrainingTriggerEvaluator.Evaluate(state, Options(), Now);

            Assert.True(decision.ShouldTrigger);
            Assert.Equal(RetrainingTriggerReason.VolumeThreshold, decision.Reason);
        }

        [Fact]
        public void Nao_dispara_quando_abaixo_do_limite_e_dentro_do_prazo()
        {
            var state = new RetrainingState { CreatedUtc = Now.AddDays(-10), ExamplesSinceLastTraining = 299 };

            var decision = RetrainingTriggerEvaluator.Evaluate(state, Options(), Now);

            Assert.False(decision.ShouldTrigger);
            Assert.Equal(RetrainingTriggerReason.None, decision.Reason);
        }

        [Fact]
        public void Dispara_pelo_teto_de_agenda_quando_ha_exemplos_novos()
        {
            var state = new RetrainingState
            {
                CreatedUtc = Now.AddDays(-200),
                LastTrainingCompletedUtc = Now.AddDays(-91),
                ExamplesSinceLastTraining = 5,
            };

            var decision = RetrainingTriggerEvaluator.Evaluate(state, Options(), Now);

            Assert.True(decision.ShouldTrigger);
            Assert.Equal(RetrainingTriggerReason.ScheduleCeiling, decision.Reason);
        }

        [Fact]
        public void Teto_de_agenda_nao_dispara_sem_exemplos_novos()
        {
            var state = new RetrainingState
            {
                CreatedUtc = Now.AddDays(-200),
                LastTrainingCompletedUtc = Now.AddDays(-120),
                ExamplesSinceLastTraining = 0,
            };

            var decision = RetrainingTriggerEvaluator.Evaluate(state, Options(), Now);

            Assert.False(decision.ShouldTrigger);
        }

        [Fact]
        public void Usa_createdUtc_como_ancora_do_teto_quando_nunca_treinou()
        {
            var state = new RetrainingState { CreatedUtc = Now.AddDays(-95), ExamplesSinceLastTraining = 1 };

            var decision = RetrainingTriggerEvaluator.Evaluate(state, Options(), Now);

            Assert.True(decision.ShouldTrigger);
            Assert.Equal(RetrainingTriggerReason.ScheduleCeiling, decision.Reason);
        }

        [Fact]
        public void Nao_dispara_quando_ja_ha_disparo_pendente()
        {
            var state = new RetrainingState
            {
                CreatedUtc = Now.AddDays(-200),
                ExamplesSinceLastTraining = 100_000,
                TriggerPending = true,
            };

            var decision = RetrainingTriggerEvaluator.Evaluate(state, Options(), Now);

            Assert.False(decision.ShouldTrigger);
            Assert.Equal(RetrainingTriggerReason.AlreadyPending, decision.Reason);
        }

        [Fact]
        public void Opcoes_nao_positivas_caem_nos_defaults()
        {
            var state = new RetrainingState { CreatedUtc = Now.AddDays(-1), ExamplesSinceLastTraining = RetrainingOptions.DefaultNewExampleThreshold };

            var decision = RetrainingTriggerEvaluator.Evaluate(state, Options(threshold: 0, maxDays: -5), Now);

            Assert.True(decision.ShouldTrigger);
            Assert.Equal(RetrainingTriggerReason.VolumeThreshold, decision.Reason);
        }
    }
}
