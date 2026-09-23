namespace LayoutParserApi.Models.Generation
{
    /// <summary>
    /// Response de <c>POST /api/layouts/{layoutGuid}/generate-sample</c> (issue #355).
    /// </summary>
    public class GenerateSampleResponse
    {
        /// <summary>Documento sintético gerado (linhas separadas por <c>\n</c> quando <c>NumberOfRecords &gt; 1</c>).</summary>
        public string GeneratedDocument { get; set; } = string.Empty;

        /// <summary>Formato do documento: <c>"positional"</c> (esta issue) ou <c>"xml"</c> (issue #356, não implementado aqui).</summary>
        public string Format { get; set; } = string.Empty;

        /// <summary>
        /// Avisos honestos sobre a qualidade do dado gerado — nunca sobre-prometer qualidade
        /// fiscal (ver ADR §"Decisão 1"/correção do dono).
        /// </summary>
        public List<string> Warnings { get; set; } = new();
    }
}
