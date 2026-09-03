public class ComparisonResult
{
    public bool Match { get; set; }
    public string Message { get; set; }
    public List<string> Differences { get; set; } = new();

    /// <summary>
    /// Detalhamento por campo (issue #173) — XPath exato + tipo de divergência, produzido pelo
    /// <c>XslSynth.Core.CanonicalDiffer</c> (mesmo comparador determinístico do loop de IA).
    /// <c>Differences</c> acima é mantido (retrocompatibilidade) como resumo textual.
    /// </summary>
    public List<LayoutParserApi.Services.Transformation.Models.FieldValidationDiff> FieldDiffs { get; set; } = new();
}