namespace LayoutParserApi.Models.Dtos.Fiscal
{
    /// <summary>Cardinalidade de um nó (issue #425), de <c>MinimalOccurrence</c>/<c>MaximumOccurrence</c> — <c>null</c> quando o LayoutVO não declara (ex.: atributo).</summary>
    public sealed record LayoutTreeCardinality(int? Min, int? Max);

    /// <summary>
    /// Nó recursivo da árvore de layout (issue #425). <c>Kind</c> é <c>"group"</c> (tem filhos),
    /// <c>"element"</c> (folha) ou <c>"attribute"</c>. <c>ElementGuid</c> é o GUID estável
    /// (TAG_/GRT_/ATT_/FLD_/LIN_…) que casa com <see cref="LayoutTreeRule.SourceElementGuid"/>/
    /// <see cref="LayoutTreeRule.TargetElementGuid"/> — pode ser <c>null</c> em nós sem GUID no XML.
    /// </summary>
    public sealed record LayoutTreeNodeDto(
        string? ElementGuid,
        string Name,
        string Kind,
        LayoutTreeCardinality? Cardinality,
        IReadOnlyList<LayoutTreeNodeDto> Children);

    /// <summary>Uma das duas árvores (origem/destino) — layout resolvido, tipo (<c>text</c>/<c>xml</c>) e raízes.</summary>
    public sealed record LayoutTreeSide(string? LayoutGuid, string Kind, IReadOnlyList<LayoutTreeNodeDto> Roots);

    /// <summary>
    /// Vínculo direto campo→campo (<c>LinkMappingItemVO</c> real do Sysmiddle) entre um nó da
    /// árvore de origem e um nó da árvore de destino, pelos mesmos GUIDs que já aparecem em
    /// <c>MappingExplanation</c> (Slice 4).
    /// </summary>
    public sealed record LayoutTreeRule(string RuleId, string? SourceElementGuid, string? TargetElementGuid);

    /// <summary>Contrato de <c>GET .../mappings/{mappingId}/layout-tree</c> (issue #425, ADR de 2026-09-16).</summary>
    public sealed record LayoutTreeResponse(
        string MapperGuid,
        LayoutTreeSide Source,
        LayoutTreeSide Target,
        IReadOnlyList<LayoutTreeRule> Rules);
}
