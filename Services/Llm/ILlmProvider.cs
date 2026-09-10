namespace LayoutParserApi.Services.Llm
{
    /// <summary>Onde o provider roda — usado pelo <see cref="LlmProviderResolver"/> como gate de segurança.</summary>
    public enum ProviderLocality
    {
        Local,
        Cloud
    }

    /// <summary>
    /// Requisição genérica de geração de texto a um provider de LLM. <see cref="Sensitivity"/> é
    /// obrigatório (sem default) — spec ADR §2.1: nenhuma chamada nasce sem declarar conscientemente
    /// se o dado é fiscal real ou sintético/anonimizado.
    /// </summary>
    public sealed record LlmRequest(
        string Prompt,
        DataSensitivity Sensitivity,
        string? JsonSchema = null,
        double Temperature = 0.0);

    /// <summary>Resposta genérica de um provider de LLM — cobre sucesso, timeout e erro de infraestrutura sem lançar exceção para o chamador.</summary>
    public sealed record LlmResponse(
        string Text,
        bool Success,
        bool TimedOut = false,
        string? ErrorMessage = null);

    /// <summary>
    /// Abstração de provedor de LLM (ADR docs/architecture/adr-llm-provider-plugavel-2026-09-08.md).
    /// Segue o mesmo espírito de <c>ai/XslSynth.Core/Synthesis/IXslSynthesizer.cs</c> — interface
    /// enxuta, sem vazar detalhe de payload HTTP de nenhum provider concreto no contrato. Só Ollama
    /// implementa nesta fase (F1); providers de nuvem são fases futuras (F3/F4), gated por
    /// <see cref="LlmProviderResolver"/>.
    /// </summary>
    public interface ILlmProvider
    {
        /// <summary>Identificador do provider (ex.: "ollama-local", "anthropic") — usado em logs e, futuramente, em resolução por nome.</summary>
        string Name { get; }

        ProviderLocality Locality { get; }

        Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken = default);

        Task<bool> IsReachableAsync(CancellationToken cancellationToken = default);
    }
}
