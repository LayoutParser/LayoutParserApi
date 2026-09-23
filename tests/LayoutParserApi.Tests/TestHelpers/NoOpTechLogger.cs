using LayoutParserApi.Models.Logging;
using LayoutParserApi.Services.Interfaces;

namespace LayoutParserApi.Tests.TestHelpers
{
    /// <summary>
    /// Stub compartilhado de <see cref="ITechLogger"/> para testes que só precisam instanciar
    /// serviços com essa dependência (ex.: <see cref="LayoutParserApi.Services.Parsing.Implementations.LineSplitter"/>),
    /// sem verificar o conteúdo do log.
    /// </summary>
    public sealed class NoOpTechLogger : ITechLogger
    {
        public void LogTechnical(LogEntry entry) { }
    }
}
