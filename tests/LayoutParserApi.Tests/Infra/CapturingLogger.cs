using Microsoft.Extensions.Logging;

namespace LayoutParserApi.Tests.Infra
{
    /// <summary>
    /// <c>ILogger&lt;T&gt;</c> que guarda a mensagem JÁ renderizada, para os testes que precisam
    /// inspecionar o texto que viraria linha de log (é o texto, não o objeto, que o painel relê).
    /// </summary>
    /// <remarks>
    /// Usa o renderizador do Microsoft.Extensions.Logging, não o do Serilog (que é quem escreve a
    /// linha em produção). Os dois coincidem pros valores que o serviço já formata como string
    /// (layout, números, timestamp), mas divergem em DOIS pontos — medido gravando a MESMA geração
    /// pelos dois caminhos:
    /// <list type="bullet">
    ///   <item><description><c>null</c>: MEL escreve <c>(null)</c>, Serilog escreve <c>null</c>.
    ///   Esta divergência é PERIGOSA — o leitor só mapeia o literal <c>null</c> de volta pra nulo
    ///   (<c>ParseNullableString</c>/<c>ParseNullableBool</c>), então <c>(null)</c> chegaria ao
    ///   painel como valor de verdade (um cStat literal "(null)").</description></item>
    ///   <item><description><c>bool</c>: MEL escreve <c>True</c>, Serilog escreve <c>true</c>.
    ///   Inofensiva na prática — <c>bool.TryParse</c> aceita as duas caixas.</description></item>
    /// </list>
    /// Conclusão prática: este logger serve pra inspecionar o TEXTO (sanitização, truncamento,
    /// determinismo do reenvio). Toda asserção sobre campo NULO tem que ser feita contra o arquivo
    /// real, escrito por um Serilog de verdade — ver <c>AiMetricsRoundTripTests</c>.
    /// </remarks>
    internal sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();

        /// <summary>
        /// Nível de cada entrada de <see cref="Messages"/>, no mesmo índice — aditivo (não quebra
        /// os testes existentes que só usam <see cref="Messages"/>). Necessário para os testes do
        /// honeypot/canary (ADR M2M, Parte 2), que precisam distinguir <c>LogCritical</c> de
        /// qualquer outro nível.
        /// </summary>
        public List<LogLevel> Levels { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoOpScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            Levels.Add(logLevel);
        }

        private sealed class NoOpScope : IDisposable
        {
            public static readonly NoOpScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
