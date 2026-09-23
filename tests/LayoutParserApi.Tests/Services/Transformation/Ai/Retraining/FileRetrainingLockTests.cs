using LayoutParserApi.Services.Transformation.Ai.Retraining;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Tests.Services.Transformation.Ai.Retraining
{
    /// <summary>Lock de exclusão mútua Ollama × treino (F4.3, issue #351).</summary>
    public class FileRetrainingLockTests
    {
        private static (FileRetrainingLock lockSvc, string dir) Criar(int staleAfterHours = 60)
        {
            var dir = Path.Combine(Path.GetTempPath(), "lp-retraining-lock-" + Guid.NewGuid().ToString("N"));
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["XslSynth:TrainingDataPath"] = dir })
                .Build();
            var lockSvc = new FileRetrainingLock(
                NullLogger<FileRetrainingLock>.Instance,
                Options.Create(new RetrainingOptions { StaleLockAfterHours = staleAfterHours }),
                config);
            return (lockSvc, dir);
        }

        [Fact]
        public void Nao_travado_quando_o_arquivo_nao_existe()
        {
            var (lockSvc, dir) = Criar();
            try
            {
                Assert.False(lockSvc.IsHeld());
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [Fact]
        public void Travado_quando_o_arquivo_existe()
        {
            var (lockSvc, dir) = Criar();
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(lockSvc.LockFilePath, "pid 1234");

                Assert.True(lockSvc.IsHeld());
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [Fact]
        public void Lock_antigo_continua_travado_e_nao_lanca()
        {
            var (lockSvc, dir) = Criar(staleAfterHours: 1);
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(lockSvc.LockFilePath, "pid 1234");
                File.SetLastWriteTimeUtc(lockSvc.LockFilePath, DateTime.UtcNow.AddHours(-5));

                Assert.True(lockSvc.IsHeld());
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }
    }
}
