namespace LayoutParserApi.Services.Generation.TxtGenerator.Enum
{
    // ✅ SemanticAI removido em 2026-08-10 junto com o decommission de Gemini/OpenAI: era o único
    // modo que saía para a nuvem, e o gerador que o implementava (SemanticAIGenerator) existia
    // exclusivamente para chamar o GeminiAIService. Dado fiscal não sai para nuvem sem autorização
    // explícita — ver rules/security.md. Se a geração semântica voltar, será sobre Ollama local.
    public enum GenerationMode
    {
        Deterministic,
        Random
    }
}