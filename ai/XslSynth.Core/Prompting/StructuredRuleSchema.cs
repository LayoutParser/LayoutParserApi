namespace XslSynth.Prompting;

/// <summary>
/// Camada 1 do desenho de contexto/prompt (docs/architecture/design-dsl-mapper-prompt-ia-2026-08-16.md
/// §2): schema JSON versionado que a Camada 0 (<see cref="XslSynth.Core.DslStructuredParser"/>)
/// produz e que o prompt do LLM consome. O Ollama NUNCA vê <c>ContentValue</c> bruto — só esta
/// representação estruturada e simplificada. Mudar a FORMA deste contrato (não os dados) exige
/// bump de <see cref="StructuredRuleSchema.Version"/>.
/// </summary>
public static class StructuredRuleSchema
{
    /// <summary>Versão do schema — bump quando um campo mudar de forma/semântica (não ao adicionar dado).</summary>
    public const int Version = 1;
}

/// <summary>
/// Regra DSL Sysmiddle já traduzida para JSON estruturado. É o resultado da Camada 0 — 100%
/// determinístico, sem IA. Serializa direto como o "contrato" descrito no design (§2): campos
/// <c>branches</c>/<c>condition</c>/<c>sources</c>/<c>target</c>/<c>functions</c>/<c>staticValue</c>/
/// <c>loopType</c>.
/// </summary>
public sealed record StructuredRule(
    int SchemaVersion,
    string? RuleId,
    string? Name,
    /// <summary>Caminho de destino primário (primeira atribuição <c>T.</c> encontrada), quando houver.</summary>
    string? Target,
    /// <summary>Ramos da árvore de decisão — 1 ramo "true" único quando não há condicional.</summary>
    IReadOnlyList<StructuredBranch> Branches,
    /// <summary>Valor estático (regra sem nenhuma origem <c>I.</c>, só literal) — null no caso comum.</summary>
    string? StaticValue,
    /// <summary>"for" | "foreach" | "while" | null — detectado na DSL, ainda sem semântica resolvida.</summary>
    string? LoopType)
{
    /// <summary>Todas as origens <c>I.&lt;Linha&gt;/&lt;Campo&gt;</c> referenciadas em qualquer ramo (deduplicado).</summary>
    public IReadOnlyList<string> AllSources =>
        Branches.SelectMany(b => b.Sources).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();

    /// <summary>Todas as funções <c>F.*</c> referenciadas em qualquer ramo (deduplicado).</summary>
    public IReadOnlyList<string> AllFunctions =>
        Branches.SelectMany(b => b.Functions).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
}

/// <summary>
/// Um ramo da árvore de decisão: a condição (já simplificada para texto legível) sob a qual um
/// determinado <c>T.</c> é emitido, com as origens/funções necessárias para calculá-lo.
/// </summary>
public sealed record StructuredBranch(
    /// <summary>Condição simplificada (ex.: "len(campoChaveAcesso) == 44", "true" se incondicional).</summary>
    string Condition,
    /// <summary>Caminho de destino emitido neste ramo (<c>T.&lt;path&gt;</c>, sem o prefixo).</summary>
    string Target,
    /// <summary>Origens <c>I.&lt;Linha&gt;/&lt;Campo&gt;</c> lidas para calcular este ramo (transitivamente, via #./$.)</summary>
    IReadOnlyList<string> Sources,
    /// <summary>Nomes das funções <c>F.*</c> chamadas neste ramo (ex.: "ConcatString", "CalculateVerifierDigit").</summary>
    IReadOnlyList<string> Functions);
