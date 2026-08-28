namespace LayoutParserApi.Services.Transformation.StructuralResolution
{
    /// <summary>
    /// Configuração do motor de resolução estrutural TXT↔XML (issue #140, itens 2/6-9 da divisão
    /// de trabalho — design em docs/architecture/design-resolucao-estrutural-txt-xml-issue-140.md).
    ///
    /// Decisão do dono (2026-08-27, registrada na memória de <c>@lp-parser-llm</c>): a fonte de
    /// verdade da estrutura XML de destino é o XSD da SEFAZ, por tipo de documento — NF-e por ora.
    /// Só um par arquivo+elemento raiz é suportado nesta primeira versão; a seção já é nomeada
    /// "Nfe*" (não "Schema*"/"Xsd*" genérico) para deixar explícito que a extensão a outros tipos
    /// de documento fiscal (CT-e etc.) exige uma opção nova, não uma mudança nesta.
    /// </summary>
    public class StructuralResolutionOptions
    {
        /// <summary>Caminho absoluto do arquivo XSD raiz da NF-e (ex.: <c>nfe_v4.00.xsd</c>, mirror
        /// <c>nfephp-org/sped-nfe</c>) — pode <c>xs:include</c>/<c>xs:import</c> outros arquivos no
        /// mesmo diretório. Vazio/ausente = motor de resolução estrutural indisponível neste host
        /// (degrada, não derruba — ver <see cref="StructuralXmlCatalogCacheService"/>).</summary>
        public string NfeSchemaPath { get; set; } = string.Empty;

        /// <summary>Nome do elemento global raiz a carregar do XSD (default <c>"NFe"</c>).</summary>
        public string NfeRootElementName { get; set; } = "NFe";
    }
}
