namespace LayoutParserApi.Services.Transformation.Models
{
    /// <summary>
    /// Categoria da divergência de campo (issue #173) — mapeada a partir do <c>Kind</c> do
    /// <c>XslSynth.Core.NodeDiff</c> (diff canônico node-a-node já usado pelo loop de IA).
    /// </summary>
    public enum FieldDiffType
    {
        /// <summary>Elemento/atributo presente nos dois lados, com valor diferente ("text"/"attr" com ambos os valores presentes).</summary>
        ValueMismatch,
        /// <summary>Esperado no XML de referência, ausente na saída gerada ("missing", ou "attr" sem valor atual).</summary>
        MissingInOutput,
        /// <summary>Presente na saída gerada, sem correspondente no XML de referência ("extra", ou "attr" sem valor esperado).</summary>
        UnexpectedInOutput,
        /// <summary>Nome/tipo do elemento diverge na mesma posição ("name") — estrutura incompatível, não só valor.</summary>
        TypeMismatch,
    }

    /// <summary>
    /// Divergência de UM campo entre o XML esperado e o gerado, com o XPath exato — substitui o
    /// resultado binário anterior de <c>TransformationValidatorService.CompareWithExpectedAsync</c>
    /// (issue #173). Reaproveita o comparador determinístico do loop de IA
    /// (<c>XslSynth.Core.CanonicalDiffer</c>) em vez de um segundo diff paralelo.
    /// </summary>
    public sealed class FieldValidationDiff
    {
        public string XPath { get; set; }
        public string Expected { get; set; }
        public string Actual { get; set; }
        public FieldDiffType DiffType { get; set; }
    }
}
