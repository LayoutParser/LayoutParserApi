namespace LayoutParserApi.Services.Transformation.Ai
{
    /// <summary>
    /// Resposta do <c>GET .../generated-transformation</c> (issue #438). <see cref="ValidationBasis"/>
    /// é <c>"declared_dsl"</c> quando <see cref="Status"/> é <c>ready</c> (<c>stale</c> nunca é devolvido: vira <c>generating</c> com regeneração disparada) — NUNCA
    /// <c>"live_execution"</c> (o runner Sysmiddle in-process trava na licença do host FiatMQ, ver
    /// ADR §2): a cobertura reportada é contra a regra DECLARADA no mapeador (<c>MapperVo</c>/DSL
    /// decifrado via <see cref="XslSynth.Contracts.Core.RealMapperParser"/>), não contra execução
    /// real do motor Sysmiddle.
    /// </summary>
    public sealed record GeneratedMapperArtifactResponse(
        string MapperGuid,
        string Status,
        string? Content,
        string? CoverageJson,
        string? ValidationBasis,
        DateTimeOffset? GeneratedAt,
        string? CorrelationId);

    /// <summary>
    /// Orquestra a leitura + disparo lazy da síntese automática de TCL/XSL/XSLT para um mapeador
    /// Sysmiddle real (issue #438, ADR <c>docs/architecture/adr-geracao-automatica-gabarito-sysmiddle.md</c>
    /// §5 — escopo mínimo). Não bloqueia: quando o candidato não existe ou está desatualizado, dispara
    /// a geração em background e devolve <c>generating</c> imediatamente.
    /// </summary>
    public interface IGeneratedMapperArtifactService
    {
        /// <summary>
        /// <c>null</c> se <paramref name="mapperGuid"/> não existir no catálogo <c>tbMapper</c>
        /// (o controller traduz para 404) — distinto de "existe mas ainda não foi gerado" (<c>none</c>
        /// nunca acontece como retorno desta chamada: se não existe candidato, a chamada já dispara
        /// a geração e devolve <c>generating</c>).
        /// </summary>
        Task<GeneratedMapperArtifactResponse?> GetOrTriggerAsync(string mapperGuid, string correlationId, CancellationToken cancellationToken);
    }
}
