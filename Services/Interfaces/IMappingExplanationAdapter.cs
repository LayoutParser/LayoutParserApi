using LayoutParserApi.Models.Dtos.Fiscal;

namespace LayoutParserApi.Services.Interfaces
{
    /// <summary>Entrada comum aos 3 adapters de explicação (Slice 4 — issue #226/#227, design §2).</summary>
    public sealed record MappingExplanationRequest(
        Guid WorkspaceId,
        Guid UserId,
        /// <summary><c>MapperGuid</c> (engine=sysmiddle) ou <c>DraftId</c> (engine=tcl|xslt).</summary>
        string MappingId,
        /// <summary>"current" (sysmiddle) ou "draft" (tcl/xslt — só isso hoje, Slice 5 introduz número real).</summary>
        string Version);

    /// <summary>
    /// Um dos 3 tradutores determinísticos (sem LLM) de um artefato de mapeamento real para o
    /// contrato canônico <see cref="MappingExplanation"/>. Nunca lança para conteúdo não
    /// reconhecido — degrada para <c>opaque</c>/<c>unsupported</c> (design §1).
    /// </summary>
    public interface IMappingExplanationAdapter
    {
        /// <summary>"sysmiddle" | "tcl" | "xslt" — usado para roteamento por factory no controller.</summary>
        string Engine { get; }

        /// <summary>
        /// Retorna <c>null</c> quando o <c>MappingId</c>/<c>Version</c> não resolve para um artefato
        /// visível ao workspace do chamador (o controller traduz para 404 fail-closed) — nunca lança
        /// para "não encontrado", só para falha de infraestrutura real.
        /// </summary>
        Task<MappingExplanation?> ExplainAsync(MappingExplanationRequest request, CancellationToken cancellationToken);
    }
}
