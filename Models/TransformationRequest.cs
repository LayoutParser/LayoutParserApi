namespace LayoutParserApi.Models
{
    // Models de request
    public class TransformationRequest
    {
        public string InputContent { get; set; }
        public string LayoutName { get; set; }
        /// <summary>
        /// LayoutGuid retornado pelo parse. Opcional para manter compatibilidade com clientes
        /// existentes; aceita o formato com prefixo LAY_.
        /// </summary>
        public string? LayoutGuid { get; set; }
        public string SourceDocumentType { get; set; }
        public string TargetDocumentType { get; set; }
        public bool Validate { get; set; } = false;
        public string ExpectedOutput { get; set; }
    }
}
