using LayoutParserApi.Models.Fiscal;

namespace LayoutParserApi.Services.Fiscal
{
    /// <summary>
    /// Nomes dos checks de qualidade que podem constar em <see cref="SpecQualitySignals.ChecksRun"/>.
    /// </summary>
    public static class SpecQualityChecks
    {
        public const string MissingRequiredColumns = "missingRequiredColumns";
        public const string SkippedSheets = "skippedSheets";
        public const string EmptySheets = "emptySheets";
        // "conflicts" e "absentReferences" existem no contrato mas NENHUM check os executa neste recorte
        // (issue #424) — por isso nunca entram em ChecksRun.
    }

    /// <summary>Valores de <c>qualityStatus</c>. Análise é síncrona: nunca há "pending" neste recorte.</summary>
    public static class QualityStatus
    {
        public const string Complete = "complete";
        public const string Failed = "failed";
    }

    /// <summary>
    /// Sinais determinísticos de qualidade de uma planilha de especificação fiscal (issue #424).
    ///
    /// LEITURA HONESTA: lista vazia só significa "verificado, sem problema" se o nome do check constar
    /// em <see cref="ChecksRun"/>. <see cref="Conflicts"/> e <see cref="AbsentReferences"/> existem no
    /// JSON por contrato com o front, mas hoje nenhum check os popula (não constam em ChecksRun) —
    /// vazios ali significam "não analisado", não "sem conflito".
    /// </summary>
    public sealed record SpecQualitySignals(
        IReadOnlyList<string> MissingRequiredColumns,
        IReadOnlyList<SpecQualityConflict> Conflicts,
        IReadOnlyList<string> AbsentReferences,
        IReadOnlyList<string> SkippedSheets,
        IReadOnlyList<string> EmptySheets,
        IReadOnlyList<string> ChecksRun);

    public sealed record SpecQualityConflict(string Field, string Reason);

    /// <summary>
    /// Resultado da análise de um artefato: <see cref="Status"/> "complete" com <see cref="Signals"/>, ou
    /// "failed" com <see cref="Error"/> seguro (sem conteúdo da planilha) e sem sinais.
    /// </summary>
    public sealed record SpecQualityResult(string Status, SpecQualitySignals? Signals, string? Error);

    /// <summary>
    /// Checks determinísticos sobre o resultado JÁ extraído por <see cref="IFiscalMappingRuleExtractor"/>
    /// — sem reler a planilha, sem LLM, sem tocar em valores de célula (só nomes de aba/coluna).
    /// </summary>
    public static class FiscalSpecQualityAnalyzer
    {
        /// <summary>
        /// Chave de configuração (lista) das colunas obrigatórias do cabeçalho de cada aba de regra.
        /// NÃO há lista canônica no código: o extrator só exige o rótulo "Regra" + ≥1 coluna de condição
        /// (que por construção já existem em toda aba reconhecida). Portanto o default é VAZIO e o check
        /// <c>missingRequiredColumns</c> só roda (e só consta em checksRun) quando o dono do domínio
        /// fiscal definir a lista aqui. Ex.: <c>FiscalPackage:Quality:RequiredRuleSheetColumns:0 = "Regra"</c>.
        /// </summary>
        public const string RequiredColumnsConfigKey = "FiscalPackage:Quality:RequiredRuleSheetColumns";

        public static SpecQualitySignals Analyze(FiscalMappingRuleExtractionResult extraction, IReadOnlyList<string> requiredColumns)
        {
            var checksRun = new List<string>();
            var missing = new List<string>();

            if (requiredColumns.Count > 0)
            {
                checksRun.Add(SpecQualityChecks.MissingRequiredColumns);
                foreach (var sheet in extraction.DecisionTableSheets)
                {
                    extraction.SheetHeaders.TryGetValue(sheet, out var headers);
                    var present = new HashSet<string>(headers ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                    foreach (var required in requiredColumns)
                    {
                        if (!present.Contains(required.Trim()))
                            missing.Add($"{sheet}!{required.Trim()}");
                    }
                }
            }

            checksRun.Add(SpecQualityChecks.SkippedSheets);
            checksRun.Add(SpecQualityChecks.EmptySheets);

            var sheetsWithRules = extraction.Rules.Select(r => r.SheetName).ToHashSet(StringComparer.Ordinal);
            var empty = extraction.DecisionTableSheets.Where(s => !sheetsWithRules.Contains(s)).Distinct().ToList();

            return new SpecQualitySignals(
                missing,
                Array.Empty<SpecQualityConflict>(),
                Array.Empty<string>(),
                extraction.SkippedSheets.ToList(),
                empty,
                checksRun);
        }
    }
}
