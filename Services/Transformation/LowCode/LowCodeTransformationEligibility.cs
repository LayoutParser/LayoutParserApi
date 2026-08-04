namespace LayoutParserApi.Services.Transformation.LowCode
{
    /// <summary>
    /// Centraliza o gate do pathway low-code disparado após o parse. Somente tipos posicionais
    /// explicitamente conhecidos entram; tipos futuros permanecem bloqueados até decisão consciente.
    /// </summary>
    public static class LowCodeTransformationEligibility
    {
        // Allowlist deliberadamente fechada: tipos futuros não entram no Sysmiddle por omissão.
        // Ver spec-fase3-fase4-gate-transformacao-e-dataset.md §1.2.
        private static readonly HashSet<string> PositionalTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "mqseries",
            "idoc",
            "unknown",
            "txt"
        };

        public const string NoMapperReason = "no_mapper";
        public const string TypeNotPositionalReason = "type_not_positional";
        public const string EmptyInputReason = "empty_input";
        public const string TimeoutSyncReason = "timeout_sync";
        public const string StructuralErrorReason = "structural_error";

        public static LowCodeTransformationEligibilityResult Evaluate(
            bool parseSucceeded,
            string? layoutGuid,
            string? rawText,
            string? detectedType,
            bool isXmlInput)
        {
            if (isXmlInput)
                return LowCodeTransformationEligibilityResult.NotEligible(TypeNotPositionalReason);

            if (!parseSucceeded)
                return LowCodeTransformationEligibilityResult.NotEligible(StructuralErrorReason);

            if (string.IsNullOrWhiteSpace(rawText))
                return LowCodeTransformationEligibilityResult.NotEligible(EmptyInputReason);

            if (string.IsNullOrWhiteSpace(layoutGuid))
                return LowCodeTransformationEligibilityResult.NotEligible(NoMapperReason);

            if (string.IsNullOrWhiteSpace(detectedType) || !PositionalTypes.Contains(detectedType))
                return LowCodeTransformationEligibilityResult.NotEligible(TypeNotPositionalReason);

            return LowCodeTransformationEligibilityResult.Eligible();
        }
    }

    public sealed record LowCodeTransformationEligibilityResult(bool IsEligible, string? Reason)
    {
        public static LowCodeTransformationEligibilityResult Eligible() => new(true, null);

        public static LowCodeTransformationEligibilityResult NotEligible(string reason) => new(false, reason);
    }
}
