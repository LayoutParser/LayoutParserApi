// Sem namespace propositalmente - segue a convenção já existente nos demais arquivos
// desta mesma pasta (DocumentPattern.cs, LineContext.cs, XsdErrorClassificationResult.cs).

/// <summary>
/// Uma entrada da tabela CFOP (Código Fiscal de Operações e Prestações), indexada por
/// código de 4 dígitos. Base do item 6.1 do dispatch de IA
/// (docs/architecture/ai-roadmap-dispatch.md, 2026-07-21) e ia-fiscal-diagnosis-vision.md §4.
/// Ver <c>CfopOperationCatalogService</c> para como é carregada/consultada.
/// </summary>
public class CfopEntry
{
    /// <summary>Código CFOP de 4 dígitos (ex.: "5102").</summary>
    public string Cfop { get; set; } = "";

    /// <summary>Descrição oficial (fonte pública - ver docblock de CfopOperationCatalogService).</summary>
    public string Descricao { get; set; } = "";

    /// <summary>
    /// true quando a linha é um CABEÇALHO de grupo/subgrupo da tabela oficial (ex.: 1100
    /// "COMPRAS PARA INDUSTRIALIZAÇÃO..."), não um código transacionável de verdade - a
    /// tabela oficial tem 3 níveis (grupo x000/x100.../x900, subgrupo x0/x50, código-folha).
    /// Um documento real NUNCA deveria declarar um CFOP com IsGrupo=true - se acontecer, é
    /// em si um sinal de anomalia.
    /// </summary>
    public bool IsGrupo { get; set; }

    /// <summary>"Entrada" ou "Saida", derivado do 1º dígito do CFOP (1/2/3 = Entrada; 5/6/7 = Saída).</summary>
    public string Direcao { get; set; } = "";

    /// <summary>"MesmoEstado" | "OutroEstado" | "Exterior", derivado do 1º dígito do CFOP.</summary>
    public string Escopo { get; set; } = "";

    /// <summary>
    /// Natureza da operação, classificada DETERMINISTICAMENTE (regra de palavra-chave sobre
    /// a descrição oficial, aplicada UMA VEZ na construção do índice - não é classificação
    /// fuzzy de documento, é rótulo de uma tabela pública estática e finita). Valores:
    /// Venda, Compra, Devolucao, Retorno, Transferencia, Remessa, PrestacaoServico,
    /// Aquisicao, Industrializacao, Anulacao, Ressarcimento, ComercioExterior,
    /// LancamentoContabil, EntradaGenerica, SaidaGenerica, Outras. Vazio quando IsGrupo=true.
    /// </summary>
    public string Categoria { get; set; } = "";
}
