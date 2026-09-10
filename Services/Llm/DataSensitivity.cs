namespace LayoutParserApi.Services.Llm
{
    /// <summary>
    /// Classifica a sensibilidade do dado que trafega num <see cref="LlmRequest"/> — obrigatório
    /// no construtor do request (sem valor default), forçando quem escreve uma chamada nova a
    /// decidir conscientemente. Ver ADR docs/architecture/adr-llm-provider-plugavel-2026-09-08.md §2.1.
    /// </summary>
    public enum DataSensitivity
    {
        /// <summary>Documento/campo de cliente real (fiscal) — jamais pode ir para provider de nuvem.</summary>
        RealFiscalDocument,

        /// <summary>Dado gerado, anonimizado, ou fixture de teste — provider de nuvem é elegível (fases futuras).</summary>
        SyntheticOrAnonymized
    }
}
