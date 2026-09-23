namespace LayoutParserApi.Models.Generation
{
    /// <summary>
    /// Resultado da geração de documento XML de exemplo a partir de um <c>LayoutVO</c> tipo
    /// <c>Xml</c> (issue #356). Degrade gracioso: falha de parse/serialização vira
    /// <see cref="Success"/> <c>false</c> + <see cref="ErrorMessage"/>, nunca exceção propagada.
    /// </summary>
    public class XmlSampleResult
    {
        public bool Success { get; set; }

        /// <summary>Documento XML gerado (com declaração), quando <see cref="Success"/>.</summary>
        public string? Xml { get; set; }

        public string? ErrorMessage { get; set; }

        /// <summary>Avisos honestos específicos do caminho XML (ex.: múltiplas raízes, grupo repetível colapsado).</summary>
        public List<string> Warnings { get; set; } = new();

        /// <summary>Quantidade de elementos criados na árvore — paridade com o relatório da POC.</summary>
        public int ElementCount { get; set; }
    }
}
