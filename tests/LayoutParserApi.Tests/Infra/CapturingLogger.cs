using Microsoft.Extensions.Logging;

namespace LayoutParserApi.Tests.Infra
{
    /// <summary>
    /// <c>ILogger&lt;T&gt;</c> que guarda a mensagem JÁ renderizada, para os testes que precisam
    /// inspecionar o texto que viraria linha de log (é o texto, não o objeto, que o painel relê).
    /// </summary>
    /// <remarks>
    /// Usa o renderizador do Microsoft.Extensions.Logging, não o do Serilog. Os dois coincidem
    /// pros valores usados aqui (strings/números/bools já formatados pelo serviço), mas divergem
    /// em <c>null</c> — MEL escreve "(null)" e o Serilog escreve "null". Onde essa diferença
    /// importa, o teste é feito contra o arquivo real (ver <c>AiMetricsRoundTripTests</c>).
    /// </remarks>
    internal sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoOpScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));

        private sealed class NoOpScope : IDisposable
        {
            public static readonly NoOpScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
