using XslSynth.Model;

namespace XslSynth.Core.StructuralResolution;

/// <summary>
/// Entrada intermediária para <see cref="FieldToXmlMappingComposer"/> — uma unidade candidata a
/// virar <see cref="FieldToXmlMapping"/>, já com os dados de origem resolvidos por quem monta o
/// candidato (peças "já existem" do design §1: <c>ParsedField</c>/<c>LineElement</c> do lado TXT,
/// <c>LinkMappingItem</c>/<c>MapperRule</c>+<c>StructuredRule</c> do lado DSL). O composer (item 5)
/// só cuida da resolução do lado XML e do critério <c>authoritative</c>/<c>best-effort</c> (§5) —
/// não conhece <c>Models.Entities</c> nem faz I/O, por isso é 100% testável com fixtures sintéticas.
/// </summary>
public sealed record MappingCandidate(
    string MappingId,
    IReadOnlyList<TxtFieldReference> Sources,
    MappingKind Kind,
    /// <summary>Caminho de destino: caminho completo de nomes (ex.: "NFe/infNFe/det/prod/CFOP",
    /// vindo de <c>MapperRule.TargetPath</c>/<c>StructuredBranch.Target</c>) OU só o nome da folha
    /// (vindo de <c>LinkMappingItem.TargetLeafName</c>) — sinalizado por <see cref="TargetPathIsFullPath"/>.</summary>
    string TargetPath,
    bool TargetPathIsFullPath,
    IReadOnlyList<string> Functions,
    string? LoopType,
    /// <summary>Condição 1 do §5: todo <c>TxtFieldReference</c> resolveu um <c>ElementGuid</c> real
    /// no layout de origem, sem fallback por nome/heurística (quem monta o candidato já sabe disso
    /// pelo parser de origem existente).</summary>
    bool AllSourcesResolvedFromOriginLayout,
    /// <summary><c>LineElement.IsPositionalGroupRepetition</c> do(s) <c>LineElement</c> de origem
    /// (design §4.2) — todas as fontes de um mesmo candidato vêm da mesma linha na prática.</summary>
    bool SourcesHavePositionalGroupRepetition,
    /// <summary>Catálogo de funções conhecidas (Camada 2, <c>FunctionCatalog</c>) — <c>null</c>
    /// quando indisponível no host (degrada para best-effort, não lança exceção — dotnet-standards.md).</summary>
    IReadOnlySet<string>? KnownFunctions = null);

/// <summary>
/// Item 5 da divisão de trabalho da issue #140 (design §5, §8): junta os itens 1-4 (catálogo XML,
/// classificador de <c>mappingKind</c>, resolução de ocorrência) e aplica o critério objetivo
/// <c>authoritative</c>/<c>best-effort</c>.
/// </summary>
public sealed class FieldToXmlMappingComposer
{
    private readonly XmlLayoutCatalog _catalog;

    public FieldToXmlMappingComposer(XmlLayoutCatalog catalog) => _catalog = catalog;

    public FieldToXmlMapping Compose(MappingCandidate candidate)
    {
        var limitations = new List<string>();

        // Resolução do nó de destino — caminho completo (preciso) vs. nome de folha (heurístico).
        XmlLayoutNode? targetNode = candidate.TargetPathIsFullPath
            ? _catalog.TryResolveByPath(candidate.TargetPath)
            : null;

        var usedLeafNameFallback = false;
        if (targetNode is null && !candidate.TargetPathIsFullPath)
        {
            var candidates = _catalog.ResolveByLeafName(candidate.TargetPath);
            if (candidates.Count == 1)
            {
                targetNode = candidates[0];
                usedLeafNameFallback = true; // resolvido, mas por heurística de nome — nunca authoritative
            }
            else if (candidates.Count > 1)
            {
                limitations.Add($"Nome de folha '{candidate.TargetPath}' ambíguo no catálogo XML ({candidates.Count} candidatos) — não resolvido.");
            }
        }
        else if (targetNode is null && candidate.TargetPathIsFullPath)
        {
            limitations.Add($"Caminho de destino '{candidate.TargetPath}' não encontrado no catálogo XML (XSD).");
        }

        IReadOnlyList<XmlNodeReference> targets;
        bool repetitionMatchConfirmed = true;

        if (targetNode is null)
        {
            targets = Array.Empty<XmlNodeReference>();
        }
        else
        {
            int? xmlOccurrence = null;
            if (candidate.Sources.Count > 0)
            {
                var occurrenceSource = candidate.Sources.FirstOrDefault(s => s.LineOccurrence > 0) ?? candidate.Sources[0];
                var resolution = OccurrenceResolver.Resolve(
                    isPositionalGroupRepetition: candidate.SourcesHavePositionalGroupRepetition,
                    lineOccurrence: occurrenceSource.LineOccurrence,
                    targetNode: targetNode,
                    catalog: _catalog);
                xmlOccurrence = resolution.XmlOccurrence;
                repetitionMatchConfirmed = resolution.RepetitionMatchConfirmed;

                if (!repetitionMatchConfirmed)
                {
                    limitations.Add("Repetição não confirmada entre origem (linha) e destino (XML) — correspondência 1:1 não pode ser assumida.");
                }
            }

            targets = new[]
            {
                new XmlNodeReference(_catalog.BuildAbsoluteXPath(targetNode), targetNode.Kind, xmlOccurrence)
            };
        }

        // Critério §5 — todas as 5 condições, binário e auditável.
        var condition1 = candidate.Kind == MappingKind.Static || candidate.AllSourcesResolvedFromOriginLayout;
        var condition2 = targetNode is not null && !usedLeafNameFallback;
        var condition3 = repetitionMatchConfirmed;
        var loopTypeIsDynamic = candidate.LoopType is "for" or "foreach" or "while";
        var condition4 = !(candidate.Kind == MappingKind.Transformed && loopTypeIsDynamic);
        var condition5 = candidate.KnownFunctions is not null && candidate.Functions.All(candidate.KnownFunctions.Contains);

        if (!condition1) limitations.Add("Origem não resolveu ElementGuid real no layout de origem (fallback heurístico).");
        if (!condition2 && targetNode is not null) limitations.Add("Destino resolvido por heurística de nome (fallback), não por caminho completo.");
        if (loopTypeIsDynamic && candidate.Kind == MappingKind.Transformed) limitations.Add($"Loop dinâmico ('{candidate.LoopType}') — contagem de ocorrência não resolvível estruturalmente sem executar a DSL.");
        if (!condition5) limitations.Add(candidate.KnownFunctions is null
            ? "FunctionCatalog indisponível — não foi possível confirmar as funções referenciadas."
            : "Função referenciada não catalogada — destino pode divergir de forma não estrutural.");

        var authoritative = condition1 && condition2 && condition3 && condition4 && condition5;

        return new FieldToXmlMapping(
            candidate.MappingId,
            candidate.Sources,
            targets,
            candidate.Kind,
            authoritative ? Confidence.Authoritative : Confidence.BestEffort,
            authoritative ? null : limitations.Distinct().ToList());
    }
}
