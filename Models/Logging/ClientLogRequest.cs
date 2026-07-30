namespace LayoutParserApi.Models.Logging
{
    /// <summary>
    /// Payload de log enviado pelo front-end (LayoutParserReact) via POST api/logs/client.
    /// Não gera arquivo/sink novo: cai no mesmo Serilog do backend, marcado com Source=Frontend.
    /// </summary>
    public class ClientLogRequest
    {
        /// <summary>Nível aceito: Debug, Information, Warning ou Error (case-insensitive).</summary>
        public string Level { get; set; } = string.Empty;

        /// <summary>Mensagem de log. Obrigatória, não-vazia.</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>Contexto adicional opcional (objeto ou string livre), serializado no log.</summary>
        public object? Context { get; set; }
    }
}
