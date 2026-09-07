namespace XslSynth.Model;

/// <summary>
/// Modelo de dados da resolução estrutural TXT↔XML (issue #140, design em
/// docs/architecture/design-resolucao-estrutural-txt-xml-issue-140.md §7). Coordenadas
/// puramente estruturais (GUID/nome/posição/XPath) — NUNCA carrega valor real de documento
/// (regra dura reforçada no design e em .claude/rules/security.md).
/// </summary>

/// <summary>Tipo de mapeamento entre origem TXT e destino XML, derivado de <see cref="StructuredRule"/>
/// sem regex ad-hoc (design §3).</summary>
public enum MappingKind
{
    /// <summary>Mapeamento 1:1 campo→campo sem DSL — veio de <c>LinkMappings</c>, não de <c>Rules</c>.</summary>
    Direct,
    /// <summary>Regra DSL com função(ões) não-concatenadora(s), condicional (múltiplos branches) ou loop.</summary>
    Transformed,
    /// <summary>Múltiplas origens combinadas por função de concatenação conhecida (ex.: ConcatString).</summary>
    Concatenated,
    /// <summary>Valor literal, sem nenhuma origem <c>I.</c>.</summary>
    Static
}

/// <summary>Tipo de nó XML de destino.</summary>
public enum XmlNodeKind
{
    Element,
    Attribute,
    Text
}

/// <summary>Confiança da resolução — critério objetivo e binário (design §5), nunca subjetivo.</summary>
public enum Confidence
{
    /// <summary>Todas as condições objetivas do design §5 (+ condição de reversibilidade em
    /// <see cref="Direction.Reverse"/>, Fase B da issue #151) são verdadeiras.</summary>
    Authoritative,
    /// <summary>Qualquer outro caso — inclusive fallback heurístico ou divergência não eliminável.</summary>
    BestEffort
}

/// <summary>
/// Fase B da issue #151 (spike aprovado pelo dono em 2026-09-07, design em
/// docs/architecture/design-reconstrucao-reversa-xml-txt-2026-09-03.md §4): sentido da resolução
/// estrutural. <see cref="Forward"/> é o caminho já em produção (#140/#141): TXT posicional →
/// XML. <see cref="Reverse"/> é o caso de uso novo da issue #151 (XML → TXT, "reconstrução
/// reversa best-effort") — reaproveita o MESMO <see cref="FieldToXmlMappingComposer"/> e as
/// mesmas 5 condições de <c>authoritative</c>/<c>best-effort</c>, só troca qual lado é o
/// "conhecido" e adiciona a condição extra de reversibilidade da função/branch (Fase A).
/// Deliberadamente NÃO inclui um <c>OccurrenceResolver</c> simétrico completo (XML-ocorrência →
/// linha física do TXT) — isso é escopo da Fase C, fora deste spike.
/// </summary>
public enum Direction
{
    /// <summary>TXT posicional (origem) → XML (destino) — caminho existente de #140/#141.</summary>
    Forward,
    /// <summary>XML (conhecido) → TXT posicional (a reconstruir) — caso de uso da issue #151.</summary>
    Reverse
}

/// <summary>Referência a um campo de origem no layout posicional (TXT/MQSeries/IDOC).</summary>
public sealed record TxtFieldReference(
    string LineGuid,
    string LineName,
    string FieldGuid,
    string FieldName,
    /// <summary>De <c>ParsedField.Occurrence</c> (fragmento físico, <c>IsAggregatedOccurrence == false</c>) — nunca o agregado.</summary>
    int LineOccurrence,
    int StartPosition,
    int Length);

/// <summary>Referência a um nó do XML de destino (NF-e, por ora — design §0).</summary>
public sealed record XmlNodeReference(
    string Xpath,
    XmlNodeKind NodeKind,
    /// <summary>Null quando não há repetição confirmada no ancestral (design §4).</summary>
    int? XmlOccurrence);

/// <summary>Um mapeamento estrutural TXT→XML completo, unidade central da issue #140.</summary>
public sealed record FieldToXmlMapping(
    string MappingId,
    /// <summary>Vazio quando <see cref="Kind"/> == <see cref="MappingKind.Static"/>.</summary>
    IReadOnlyList<TxtFieldReference> Sources,
    IReadOnlyList<XmlNodeReference> Targets,
    MappingKind Kind,
    Confidence Confidence,
    /// <summary>Motivo(s) quando <see cref="Confidence.BestEffort"/> — nunca null nesse caso (design §7).</summary>
    IReadOnlyList<string>? Limitations = null,
    /// <summary>Fase B da issue #151 — sentido em que este mapeamento foi composto. Default
    /// <see cref="Model.Direction.Forward"/> preserva 100% o comportamento de #140/#141.</summary>
    Direction Direction = Direction.Forward);
