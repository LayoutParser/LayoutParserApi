namespace LayoutParserApi.Models.Fiscal
{
    /// <summary>
    /// Exemplo real de transformação TCL/XSL da Neogrid, usado como corpus de referência/oráculo
    /// (não é um release compilado pelo pipeline nem dado de cliente). Ver
    /// <see cref="LayoutParserApi.Services.Fiscal.IReferenceExampleCatalogService"/>.
    /// </summary>
    public sealed class ReferenceExample
    {
        /// <summary>Identificador estável do exemplo (hash do caminho relativo), usado para buscar o conteúdo completo.</summary>
        public required string Id { get; set; }

        /// <summary>Tipo de documento fiscal (ex.: NFe, CTe, MDFe, NFSe) — corresponde à pasta de 1º nível do corpus.</summary>
        public required string DocType { get; set; }

        /// <summary>Versão do layout (ex.: "4.00", "2.06c") — corresponde à pasta de 2º nível do corpus.</summary>
        public string? Version { get; set; }

        /// <summary>Cenário/operação (ex.: "EnvioNFe", "CancNFe", "RetEnvNFe") — extraído do nome do arquivo, best-effort.</summary>
        public required string Scenario { get; set; }

        /// <summary>Sentido da transformação (ex.: "NeoGridToSefaz", "SefazToNeoGrid") — extraído do nome do arquivo, best-effort.</summary>
        public string? Direction { get; set; }

        /// <summary>Nome do arquivo TCL original (sem conteúdo — ver <see cref="IReferenceExampleCatalogService.GetContentAsync"/>).</summary>
        public string? TclFileName { get; set; }

        /// <summary>Nome do arquivo XSL/XSLT original, quando existe um par com o mesmo nome-base (sem conteúdo).</summary>
        public string? XslFileName { get; set; }
    }

    /// <summary>Conteúdo completo (TCL e/ou XSL) de um <see cref="ReferenceExample"/> — retornado sob demanda, não na listagem.</summary>
    public sealed class ReferenceExampleContent
    {
        public required string Id { get; set; }
        public string? TclContent { get; set; }
        public string? XslContent { get; set; }
    }
}
