namespace XslSynth.Core.StructuralResolution;

/// <summary>
/// Item 4 da divisão de trabalho da issue #140 (design §4): conecta <c>lineOccurrence</c> (já
/// resolvido do lado TXT via <c>ParsedField.Occurrence</c>/<c>IsAggregatedOccurrence</c> — fora do
/// escopo desta classe, o chamador já traz o valor físico correto) a <c>xmlOccurrence</c>, usando
/// <c>LineElement.IsPositionalGroupRepetition</c> do lado origem e <see cref="XmlLayoutNode.MaxOccurs"/>
/// de um ancestral do lado destino.
///
/// Desacoplada de <c>Models.Entities</c> (runtime Windows-only da API) por design — recebe
/// primitivos, não os tipos EF/XML-serializados. O chamador (endpoint em <c>@lp-backend-dev</c>,
/// item 6 do design) faz a ponte.
/// </summary>
public static class OccurrenceResolver
{
    /// <summary>
    /// Resolve <c>xmlOccurrence</c> para um <see cref="XmlNodeReference"/> alvo.
    /// </summary>
    /// <param name="isPositionalGroupRepetition"><c>LineElement.IsPositionalGroupRepetition</c> do <c>LineElement</c> de origem.</param>
    /// <param name="lineOccurrence"><c>ParsedField.Occurrence</c> físico (1-based, fragmento bruto — não o agregado).</param>
    /// <param name="targetNode">Nó XML de destino já resolvido no catálogo.</param>
    /// <param name="catalog">Catálogo da árvore XML de destino (para percorrer ancestrais).</param>
    public static OccurrenceResolution Resolve(
        bool isPositionalGroupRepetition,
        int lineOccurrence,
        XmlLayoutNode targetNode,
        XmlLayoutCatalog catalog)
    {
        var repeatedAncestor = catalog.Ancestors(targetNode).FirstOrDefault(a => a.IsRepeatable);
        var xmlSideRepeats = repeatedAncestor is not null;

        if (isPositionalGroupRepetition && xmlSideRepeats)
        {
            // Hipótese estrutural (design §4.2): correspondência 1:1 posicional entre a N-ésima
            // ocorrência da linha repetida e o N-ésimo nó XML repetido, mesma ordem de emissão.
            // Confirmação real fica para a validação comportamental (§6) — aqui só a resolução.
            return new OccurrenceResolution(XmlOccurrence: lineOccurrence, RepetitionMatchConfirmed: true);
        }

        if (isPositionalGroupRepetition != xmlSideRepeats)
        {
            // Mismatch: origem repetida e destino não (ou vice-versa) — não pode assumir
            // correspondência 1:1 (design §4.2). Cai automaticamente em best-effort (§5 condição 3).
            return new OccurrenceResolution(XmlOccurrence: null, RepetitionMatchConfirmed: false);
        }

        // Nem origem nem destino repetem — condição 3 do §5 não se aplica (trivialmente satisfeita).
        return new OccurrenceResolution(XmlOccurrence: null, RepetitionMatchConfirmed: true);
    }
}

/// <summary>Resultado da resolução de ocorrência: valor (quando aplicável) + se a condição 3 do
/// critério §5 (authoritative) está satisfeita.</summary>
public readonly record struct OccurrenceResolution(int? XmlOccurrence, bool RepetitionMatchConfirmed);
