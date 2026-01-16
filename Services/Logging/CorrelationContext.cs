using System.Threading;

namespace LayoutParserApi.Services.Logging
{
    /// <summary>
    /// CorrelationId por request (propaga via AsyncLocal).
    /// Usado para manter o mesmo GUID em logs e para repassar para processos externos (Decrypt/Runner).
    /// </summary>
    public static class CorrelationContext
    {
        private static readonly AsyncLocal<string?> _current = new();

        public static string? CurrentId
        {
            get => _current.Value;
            set => _current.Value = value;
        }
    }
}


