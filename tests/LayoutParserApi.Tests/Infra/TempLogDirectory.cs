using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Serilog;
using Serilog.Extensions.Logging;

namespace LayoutParserApi.Tests.Infra
{
    /// <summary>
    /// Diretório de log descartável para os testes que exercitam o pipeline REAL de arquivo
    /// (escrita Serilog → leitura do <c>UnifiedLogReaderService</c>).
    /// </summary>
    /// <remarks>
    /// ⚠️ Regra herdada do harness de QA: teste de logging NUNCA aponta para o
    /// <c>Logging:File:Directory</c> configurado no appsettings (é o diretório de PRODUÇÃO). Um
    /// incidente real já perdeu histórico de log por causa disso. Aqui a configuração é montada
    /// em memória apontando para uma pasta temporária, apagada no <see cref="Dispose"/>.
    /// </remarks>
    internal sealed class TempLogDirectory : IDisposable
    {
        // Mesmo outputTemplate do Program.cs — é o contrato de formato entre quem escreve e o
        // regex de quem lê. Divergência aqui é exatamente a classe de bug que zerou o painel
        // (dois Serilog independentes escrevendo no mesmo arquivo com templates diferentes).
        private const string OutputTemplate =
            "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] [Corr:{CorrelationId}] [Src:{Source}] {Message:lj}{NewLine}{Exception}";

        public TempLogDirectory()
        {
            FullPath = Path.Combine(Path.GetTempPath(), "lp-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(FullPath);
        }

        /// <summary>Caminho da pasta temporária que faz o papel do diretório de logs.</summary>
        public string FullPath { get; }

        /// <summary>Configuração mínima que o <c>UnifiedLogReaderService</c> consome.</summary>
        public IConfiguration BuildConfiguration()
            => new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Logging:File:Directory"] = FullPath })
                .Build();

        /// <summary>Grava linhas cruas (já no formato do arquivo) na fonte informada.</summary>
        public void WriteRawLines(string fileName, params string[] lines)
            => File.WriteAllLines(Path.Combine(FullPath, fileName), lines);

        /// <summary>
        /// Fábrica de <c>ILogger&lt;T&gt;</c> apoiada num Serilog REAL escrevendo no arquivo da API
        /// desta pasta temporária, com o mesmo outputTemplate e a mesma ordem de enrichers do
        /// Program.cs (FromLogContext antes do Source padrão, senão o <c>Src:</c> da linha nunca
        /// seria "AiMetrics"). Descartar a fábrica fecha o sink e libera o arquivo pra leitura.
        /// </summary>
        public ILoggerFactory CreateSerilogLoggerFactory()
        {
            var serilog = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Source", "Backend")
                .WriteTo.File(
                    Path.Combine(FullPath, "layoutparserapi.log"),
                    rollingInterval: RollingInterval.Infinite,
                    outputTemplate: OutputTemplate,
                    shared: true)
                .CreateLogger();

            return new SerilogLoggerFactory(serilog, dispose: true);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(FullPath, recursive: true);
            }
            catch (IOException)
            {
                // Pasta temporária presa por handle aberto não é falha de teste — o SO limpa depois.
            }
        }
    }
}
