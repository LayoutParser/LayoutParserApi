// Sem namespace propositalmente - segue a convenção já existente nos demais arquivos
// desta mesma pasta (DocumentPattern.cs, LineContext.cs, PatternSuggestion.cs).

using LayoutParserApi.Models.XmlAnalysis;

/// <summary>
/// Resultado da classificação determinística de erros XSD em "defeito real" vs.
/// "esperado" (conhecido-e-aceito - ex.: ausência de assinatura digital).
/// Item 3.2 do dispatch de IA (docs/architecture/ai-roadmap-dispatch.md, 2026-07-21).
/// </summary>
public class XsdErrorClassificationResult
{
    /// <summary>Defeitos reais - devem seguir para explicação (IA ou orientação estática).</summary>
    public List<XsdValidationError> RealErrors { get; set; } = new();

    /// <summary>
    /// Itens "esperados" (conhecido-e-aceitos): não são defeito real, apenas ruído
    /// conhecido do XSD (ex.: assinatura digital ausente em documento ainda não assinado).
    /// Nunca devem ser reportados ao usuário como "erro".
    /// </summary>
    public List<XsdValidationError> AcceptedIssues { get; set; } = new();
}
