namespace LayoutParserApi.Models.Fiscal
{
    /// <summary>
    /// Uma regra fiscal extraída de uma planilha "tabela de decisão" (ex.: aba
    /// "Regra-CST 40 41 e 50" do Excel de autoria fiscal, issue #103). Formato real observado:
    /// linha de cabeçalho (<c>Regra | orig | CST | vICMS | motDesICMS</c>) seguida de linhas de
    /// dados onde as colunas do meio são condições (podem ter múltiplos valores, ex.: "0 ou 1 ou 2",
    /// ou faixas em texto livre, ex.: "Maior 0,00") e a(s) última(s) coluna(s) são o resultado em
    /// texto livre (ex.: "Sistema Signature gera as TAG's VICMS e motDesICMS").
    ///
    /// Isto é DIFERENTE do desenho original hipotético (tripla campoOrigem/campoDestino/condição) —
    /// a planilha real do dono não tem colunas fixas de origem/destino; é uma tabela de decisão
    /// (múltiplas condições → 1 desfecho textual). O extrator (<see cref="Services.Fiscal.FiscalMappingRuleExtractor"/>)
    /// reflete essa estrutura real, não a hipotética.
    /// </summary>
    public class FiscalMappingRule
    {
        /// <summary>Aba de origem (ex.: "Regra-CST 40 41 e 50").</summary>
        public string SheetName { get; set; } = string.Empty;

        /// <summary>Número/identificador da regra (1ª coluna da linha de dados, ex.: "1", "2").</summary>
        public string RuleNumber { get; set; } = string.Empty;

        /// <summary>
        /// Condições da regra, na ordem das colunas da planilha: nome da coluna (do cabeçalho,
        /// ex.: "orig", "CST", "vICMS") → valor bruto da célula (ex.: "0 ou 1 ou 2", "Maior 0,00").
        /// Mantido como texto — não tentamos inferir operador (=, IN, >) nesta primeira fatia
        /// determinística; ver <see cref="RequiresManualReview"/>.
        /// </summary>
        public List<FiscalRuleCondition> Conditions { get; set; } = new();

        /// <summary>
        /// Desfecho da regra em texto livre (última(s) coluna(s) não mapeadas a um cabeçalho de
        /// condição). Ex.: "Erro - Operação Invalida pelo Signature". Isto é o campo que, quando a
        /// planilha tiver texto ambíguo demais para virar condição estruturada, fica marcado para
        /// revisão humana (Passo 1 do plano não usa LLM — ver <see cref="RequiresManualReview"/>).
        /// </summary>
        public string Outcome { get; set; } = string.Empty;

        /// <summary>Número da linha na planilha original (1-based), para rastreabilidade/auditoria.</summary>
        public int SourceRowNumber { get; set; }

        /// <summary>
        /// true quando o parser determinístico não conseguiu separar condição de desfecho com
        /// confiança (ex.: linha com número de colunas diferente do cabeçalho). Nunca adivinha —
        /// sinaliza para revisão humana em vez de inventar estrutura.
        /// </summary>
        public bool RequiresManualReview { get; set; }
    }

    /// <summary>Uma condição nomeada de uma <see cref="FiscalMappingRule"/>.</summary>
    public class FiscalRuleCondition
    {
        /// <summary>Nome da coluna de condição (vindo do cabeçalho da tabela, ex.: "CST").</summary>
        public string Field { get; set; } = string.Empty;

        /// <summary>Valor bruto da célula, sem interpretação (ex.: "40 ou 41 ou 50").</summary>
        public string RawValue { get; set; } = string.Empty;
    }

    /// <summary>
    /// Resultado completo da extração determinística (Passo 1, sem LLM) de um arquivo Excel de
    /// autoria fiscal: uma ou mais tabelas de decisão encontradas, mais a lista de abas que
    /// pareciam planilhas de layout posicional (catálogo de campo, não regra condicional) e por
    /// isso foram deliberadamente ignoradas por este extrator — cobertas pelo parsing de layout
    /// já existente no projeto, não por <c>FiscalMappingRuleExtractor</c>.
    /// </summary>
    public class FiscalMappingRuleExtractionResult
    {
        public List<FiscalMappingRule> Rules { get; set; } = new();

        /// <summary>Abas reconhecidas como tabela de decisão e efetivamente processadas.</summary>
        public List<string> DecisionTableSheets { get; set; } = new();

        /// <summary>
        /// Abas visíveis/ocultas que não bateram com o formato de tabela de decisão (ex.: são
        /// catálogo de layout posicional, ou não têm uma linha de cabeçalho reconhecível) — não é
        /// erro, é informação para o humano revisar se esperava uma regra ali.
        /// </summary>
        public List<string> SkippedSheets { get; set; } = new();
    }
}
