namespace LayoutParserApi.Services.Learning.Models
{
    /// <summary>
    /// Um cenário fiscal sintético rotulado - a UNIDADE que alimenta o índice RAG da IA
    /// fiscal especializada (item 4.1/4.2 do dispatch de IA,
    /// docs/architecture/ai-roadmap-dispatch.md, 2026-07-21). NÃO é fixture de teste
    /// descartável: o rótulo (<see cref="Rotulo"/>) é o texto que o modelo pequeno recupera
    /// ao explicar uma divergência real parecida - ver
    /// <c>SyntheticFiscalScenarioGenerator</c>.
    /// </summary>
    public class SyntheticFiscalScenario
    {
        public string EmitenteNome { get; set; } = "";
        public string EmitenteCnpj { get; set; } = "";
        public string DestinatarioNome { get; set; } = "";

        /// <summary>CPF (pessoa física) ou CNPJ (pessoa jurídica) do destinatário, conforme o cenário.</summary>
        public string DestinatarioDocumento { get; set; } = "";

        public string ProdutoDescricao { get; set; } = "";
        public decimal ValorTotal { get; set; }

        public string Cfop { get; set; } = "";
        public string CfopDescricao { get; set; } = "";
        public string CfopCategoria { get; set; } = "";

        /// <summary><c>ide/finNFe</c>: 1=Normal, 2=Complementar, 3=Ajuste, 4=Devolução/Retorno.</summary>
        public string FinNFe { get; set; } = "";

        /// <summary>false = cenário fiscalmente consistente; true = divergência deliberada (rotulada).</summary>
        public bool EhDivergente { get; set; }

        /// <summary>
        /// Texto em linguagem natural descrevendo o cenário e (quando EhDivergente) a
        /// divergência - é isso que entra no índice RAG, não os valores crus.
        /// </summary>
        public string Rotulo { get; set; } = "";

        /// <summary>Semente usada nesta geração (reprodutibilidade - útil para depurar/repetir um caso específico).</summary>
        public int Seed { get; set; }
    }
}
