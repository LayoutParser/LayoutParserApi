namespace LayoutParserApi.Services.Transformation.LowCode
{
    /// <summary>
    /// Centraliza o gate do pathway low-code disparado após o parse. O tipo detectado do
    /// documento é apenas metadado da execução: qualquer entrada não XML que tenha sido
    /// parseada com sucesso pode ser transformada.
    /// </summary>
    public static class LowCodeTransformationEligibility
    {
        public const string NoMapperReason = "no_mapper";
        public const string TypeNotPositionalReason = "type_not_positional";
        public const string EmptyInputReason = "empty_input";
        public const string TimeoutSyncReason = "timeout_sync";
        public const string StructuralErrorReason = "structural_error";

        public static LowCodeTransformationEligibilityResult Evaluate(
            bool parseSucceeded,
            string? layoutGuid,
            string? rawText,
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

            return LowCodeTransformationEligibilityResult.Eligible();
        }
    }

    public sealed record LowCodeTransformationEligibilityResult(bool IsEligible, string? Reason)
    {
        public static LowCodeTransformationEligibilityResult Eligible() => new(true, null);

        public static LowCodeTransformationEligibilityResult NotEligible(string reason) => new(false, reason);
    }
}
