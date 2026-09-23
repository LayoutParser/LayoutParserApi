using LayoutParserApi.Services.Transformation.Ai.Retraining;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Tests.Services.Transformation.Ai.Retraining
{
    /// <summary>Orquestração do gatilho de retraining (F4.2/F4.3, issue #351).</summary>
    public class RetrainingCoordinatorTests
    {
        private sealed class InMemoryStateStore : IRetrainingStateStore
        {
            public RetrainingState State = new() { CreatedUtc = DateTime.UtcNow.AddDays(-1) };
            public RetrainingState Load() => State;
            public void Save(RetrainingState state) => State = state;
            public RetrainingState Update(Action<RetrainingState> mutate) { mutate(State); return State; }
        }

        private sealed class FakeLock : IRetrainingLock
        {
            public bool Held;
            public bool IsHeld() => Held;
            public string LockFilePath => "fake.lock";
        }

        private static (RetrainingCoordinator coord, InMemoryStateStore store, FakeLock lockSvc, string dir, string triggerPath)
            Criar(bool enabled, int threshold = 300)
        {
            var dir = Path.Combine(Path.GetTempPath(), "lp-coord-" + Guid.NewGuid().ToString("N"));
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["XslSynth:TrainingDataPath"] = dir })
                .Build();
            var store = new InMemoryStateStore();
            var lockSvc = new FakeLock();
            var options = Options.Create(new RetrainingOptions { Enabled = enabled, NewExampleThreshold = threshold });
            var coord = new RetrainingCoordinator(
                NullLogger<RetrainingCoordinator>.Instance, store, lockSvc, options, config);
            return (coord, store, lockSvc, dir, Path.Combine(dir, "retraining.trigger.json"));
        }

        [Fact]
        public void RegisterCapturedExample_incrementa_o_contador()
        {
            var (coord, store, _, dir, _) = Criar(enabled: true);
            try
            {
                coord.RegisterCapturedExample();
                coord.RegisterCapturedExample();

                Assert.Equal(2, store.State.ExamplesSinceLastTraining);
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task Dispara_e_escreve_o_marcador_quando_habilitado_e_volume_atingido()
        {
            var (coord, store, _, dir, triggerPath) = Criar(enabled: true, threshold: 5);
            try
            {
                store.State.ExamplesSinceLastTraining = 5;

                await coord.EvaluateAndMaybeTriggerAsync(CancellationToken.None);

                Assert.True(File.Exists(triggerPath));
                Assert.True(store.State.TriggerPending);
                Assert.Equal(5, store.State.ExamplesCountedAtTrigger);
                Assert.Equal("VolumeThreshold", store.State.LastTriggerReason);
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task Nao_escreve_marcador_quando_desabilitado()
        {
            var (coord, store, _, dir, triggerPath) = Criar(enabled: false, threshold: 5);
            try
            {
                store.State.ExamplesSinceLastTraining = 50;

                await coord.EvaluateAndMaybeTriggerAsync(CancellationToken.None);

                Assert.False(File.Exists(triggerPath));
                Assert.False(store.State.TriggerPending);
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task Nao_dispara_enquanto_o_lock_esta_ativo()
        {
            var (coord, store, lockSvc, dir, triggerPath) = Criar(enabled: true, threshold: 5);
            try
            {
                store.State.ExamplesSinceLastTraining = 999;
                lockSvc.Held = true;

                await coord.EvaluateAndMaybeTriggerAsync(CancellationToken.None);

                Assert.False(File.Exists(triggerPath));
                Assert.False(store.State.TriggerPending);
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task Detecta_conclusao_e_preserva_exemplos_capturados_durante_o_treino()
        {
            var (coord, store, lockSvc, dir, _) = Criar(enabled: true, threshold: 5);
            try
            {
                // Estado pós-disparo: 5 contados no disparo + 4 capturados durante o treino de ~40h.
                store.State.TriggerPending = true;
                store.State.ExamplesCountedAtTrigger = 5;
                store.State.ExamplesSinceLastTraining = 9;
                lockSvc.Held = false; // treino terminou, lock liberado
                // marcador não existe (script da VM removeu)

                await coord.EvaluateAndMaybeTriggerAsync(CancellationToken.None);

                Assert.False(store.State.TriggerPending);
                Assert.Equal(4, store.State.ExamplesSinceLastTraining);
                Assert.Equal(0, store.State.ExamplesCountedAtTrigger);
                Assert.NotNull(store.State.LastTrainingCompletedUtc);
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }
    }
}
