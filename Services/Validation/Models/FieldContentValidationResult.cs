// Sem namespace propositalmente - segue a convenção já existente nos demais arquivos
// desta mesma pasta (DocumentPattern.cs, LineContext.cs, PatternSuggestion.cs).

/// <summary>
/// Resultado da validação determinística de conteúdo de um campo de input
/// (tamanho/formato/checksum), sem uso de IA.
/// Item 3.1 do dispatch de IA (docs/architecture/ai-roadmap-dispatch.md, 2026-07-21).
/// </summary>
public class FieldContentValidationResult
{
    public string FieldName { get; set; } = "";
    public string Value { get; set; } = "";
    public bool IsValid { get; set; } = true;
    public int ExpectedLength { get; set; }
    public int ActualLength { get; set; }

    /// <summary>
    /// Mensagens descrevendo os problemas encontrados (vazio quando IsValid = true).
    /// </summary>
    public List<string> Issues { get; set; } = new();
}
