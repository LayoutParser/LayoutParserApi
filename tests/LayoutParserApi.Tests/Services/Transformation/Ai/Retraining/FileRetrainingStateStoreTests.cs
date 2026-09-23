using LayoutParserApi.Services.Transformation.Ai.Retraining;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Tests.Services.Transformation.Ai.Retraining
{
    /// <summary>Persistência durável do estado de retraining em arquivo (F4.2, issue #351).</summary>
    public class FileRetrainingStateStoreTests
    {
        private static (FileRetrainingStateStore store, string dir) Criar()
        {
            var dir = Path.Combine(Path.GetTempPath(), "lp-retraining-" + Guid.NewGuid().ToString("N"));
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["XslSynth:TrainingDataPath"] = dir })
                .Build();
            var store = new FileRetrainingStateStore(
                NullLogger<FileRetrainingStateStore>.Instance,
                Options.Create(new RetrainingOptions()),
                config);
            return (store, dir);
        }

        [Fact]
        public void Load_sem_arquivo_cria_estado_novo_com_createdUtc()
        {
            var (store, dir) = Criar();
            try
            {
                var state = store.Load();

                Assert.NotEqual(default, state.CreatedUtc);
                Assert.Equal(0, state.ExamplesSinceLastTraining);
                Assert.True(File.Exists(Path.Combine(dir, "retraining-state.json")));
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [Fact]
        public void Update_persiste_a_mutacao_e_sobrevive_a_um_reload()
        {
            var (store, dir) = Criar();
            try
            {
                store.Update(s => s.ExamplesSinceLastTraining += 7);
                store.Update(s => s.ExamplesSinceLastTraining += 3);

                var reloaded = store.Load();
                Assert.Equal(10, reloaded.ExamplesSinceLastTraining);
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [Fact]
        public void Arquivo_corrompido_volta_para_estado_novo_sem_lancar()
        {
            var (store, dir) = Criar();
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "retraining-state.json"), "{ nao é json válido");

                var state = store.Load();
                Assert.Equal(0, state.ExamplesSinceLastTraining);
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }
    }
}
