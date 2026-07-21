// Sem namespace propositalmente - segue a convenção já existente nos demais arquivos
// desta mesma pasta (DocumentPattern.cs, LineContext.cs, XsdErrorClassificationResult.cs).

/// <summary>
/// Resultado do cruzamento determinístico CFOP declarado x <c>ide/finNFe</c> declarado
/// (item 6.2 do dispatch de IA - lookup puro, sem IA; o LLM entra só depois, para explicar
/// a divergência em linguagem natural). Ver <c>CfopOperationCatalogService.CheckConsistenciaComFinalidade</c>.
/// </summary>
public class CfopSemanticCheckResult
{
    /// <summary>O CFOP existe na tabela (e não é cabeçalho de grupo).</summary>
    public bool CfopEncontrado { get; set; }

    /// <summary>Entrada resolvida da tabela CFOP (null se não encontrado).</summary>
    public CfopEntry? Entry { get; set; }

    /// <summary>
    /// false quando a checagem determinística encontrou divergência entre a categoria do
    /// CFOP e o <c>finNFe</c> declarado (ex.: CFOP de natureza "Venda" com finNFe=4
    /// "Devolução"). Divergência é candidata a explicação, não veredito absoluto - casos de
    /// borda existem (ver comentário do método). true por padrão (nenhuma divergência
    /// detectada, ou CFOP não encontrado - nesse caso não há base para comparar).
    /// </summary>
    public bool IsConsistente { get; set; } = true;

    /// <summary>Descrição da(s) divergência(s) encontrada(s), em linguagem simples (pré-LLM).</summary>
    public List<string> Divergencias { get; set; } = new();
}
