using LayoutParserApi.Models.Entities;

namespace LayoutParserApi.Services.XmlAnalysis
{
    /// <summary>
    /// Item 3 do critério de aceite da issue #151 (Fase 4) — o gap que faltava sobre
    /// <see cref="ReverseReconstructionService"/>: quando o TXT original existe na sessão, a
    /// reconstrução é VALIDADA contra ele (bate/não bate por campo), não apenas gerada.
    /// </summary>
    /// <remarks>
    /// Puro/sem estado — não faz IO nem parse, só compara o que os dois chamadores (endpoint) já
    /// produziram: <see cref="ReconstructionResult.ReconstructedFields"/> (issue #151) contra
    /// <see cref="ParsedField"/> reais do TXT original, obtidos do MESMO
    /// <see cref="Services.Interfaces.ILayoutParserService.ParseAsync"/> usado no pathway direto —
    /// não reimplementa parsing posicional.
    /// <para>
    /// <b>Comparação é best-effort, não byte-exata</b> (mesma limitação já documentada em
    /// <see cref="ReverseReconstructionService.ReconstructOneMapping"/>): o reconstrutor sempre
    /// preenche à direita com espaço (alinhamento não é conhecido no crosswalk), então a comparação
    /// aqui usa <c>TrimEnd()</c> nos dois lados — um campo originalmente zero-padded à esquerda
    /// (numérico) pode divergir mesmo com o valor lógico idêntico. Isso não é escondido: é a mesma
    /// natureza "best-effort" do contrato inteiro da issue #151.
    /// </para>
    /// </remarks>
    public static class ReverseReconstructionValidator
    {
        public static ReconstructionValidationResult Validate(
            IReadOnlyList<ReconstructedFieldEntry> reconstructedFields,
            IReadOnlyList<ParsedField> originalParsedFields)
        {
            var validation = new ReconstructionValidationResult();

            // Só fragmentos físicos reais (IsAggregatedOccurrence == false) — mesmo racional do
            // crosswalk: TxtFieldReference.LineOccurrence sempre vem de ParsedField.Occurrence do
            // fragmento físico, nunca do agregado (Occurrence == 0).
            var originalByKey = (originalParsedFields ?? new List<ParsedField>())
                .Where(f => !f.IsAggregatedOccurrence)
                .GroupBy(f => (f.LineName, f.FieldName, f.Occurrence))
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var field in reconstructedFields ?? new List<ReconstructedFieldEntry>())
            {
                var entry = new ReconstructionFieldValidation
                {
                    LineName = field.LineName,
                    FieldName = field.FieldName,
                    Occurrence = field.Occurrence
                };

                if (originalByKey.TryGetValue((field.LineName, field.FieldName, field.Occurrence), out var original))
                {
                    entry.OriginalValue = original.Value;
                    entry.Matched = string.Equals(
                        (field.Value ?? string.Empty).TrimEnd(),
                        (original.Value ?? string.Empty).TrimEnd(),
                        StringComparison.Ordinal);
                }
                else
                {
                    // Campo reconstruído não existe (ou não bateu por chave) no parse do TXT
                    // original — terceiro caso, distinto de "bateu"/"divergiu com gabarito
                    // conhecido": não inventa OriginalValue.
                    entry.OriginalValue = null;
                    entry.Matched = false;
                }

                validation.Fields.Add(entry);
            }

            validation.MatchedFields = validation.Fields.Count(f => f.Matched);
            validation.MismatchedFields = validation.Fields.Count - validation.MatchedFields;
            validation.PercentMatch = validation.Fields.Count == 0
                ? 0
                : Math.Round(100.0 * validation.MatchedFields / validation.Fields.Count, 2);

            return validation;
        }
    }
}
