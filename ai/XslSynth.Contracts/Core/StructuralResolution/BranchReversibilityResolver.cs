using XslSynth.Prompting;

namespace XslSynth.Core.StructuralResolution;

/// <summary>Resultado da checagem de reversibilidade de um <c>StructuredBranch</c> — mesmo
/// espírito de <see cref="OccurrenceResolution"/>: valor + se a condição está confirmada.</summary>
public readonly record struct BranchReversibility(bool Reversible, string? Reason);

/// <summary>
/// Fase B da issue #151 (spike aprovado pelo dono em 2026-09-07): costura o metadado curado da
/// Fase A (<see cref="FunctionCatalog"/>/<see cref="FunctionReversibilityCatalog"/>) para o nível
/// de <see cref="StructuredBranch"/>, que é a granularidade usada por
/// <see cref="FieldToXmlMappingComposer"/> ao compor um <see cref="MappingCandidate"/>.
///
/// Critério (design §3, tabela por <c>MappingKind</c>): um branch sem nenhuma função referenciada
/// é estruturalmente reversível (cópia posicional, mesma categoria de <c>MappingKind.Direct</c>).
/// Um branch com funções só é reversível se TODAS as funções referenciadas forem, individualmente,
/// <c>Reversible == true</c> no catálogo curado — uma única função com perda (ex.:
/// <c>CalculateVerifierDigit</c>) torna o branch inteiro não-reversível, mesmo que as demais sejam
/// bijetoras (a composição de bijetora + não-bijetora não é bijetora).
/// </summary>
public static class BranchReversibilityResolver
{
    /// <summary>Resolve a reversibilidade de um branch. Puro — sem I/O, sem exceção; degrada para
    /// <c>false</c> com motivo explícito quando o catálogo está indisponível ou a função é
    /// desconhecida (mesmo padrão "sinalize incerteza" do resto do motor de #139/#140).</summary>
    public static BranchReversibility Resolve(StructuredBranch branch, FunctionCatalog? catalog)
    {
        if (branch.Functions.Count == 0)
        {
            // Sem função — cópia/posicional direta (mesma categoria de MappingKind.Direct no §3),
            // sempre reversível estruturalmente.
            return new BranchReversibility(true, null);
        }

        if (catalog is null)
        {
            return new BranchReversibility(false,
                "FunctionCatalog indisponível — não foi possível confirmar a reversibilidade das funções.");
        }

        var unknown = branch.Functions
            .Where(f => catalog.Lookup(f) is null)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (unknown.Count > 0)
        {
            return new BranchReversibility(false,
                $"Função(ões) não catalogada(s): {string.Join(", ", unknown)}.");
        }

        var irreversible = branch.Functions
            .Select(f => catalog.Lookup(f)!)
            .Where(e => !e.Reversible)
            .ToList();

        if (irreversible.Count > 0)
        {
            var reasons = irreversible
                .Select(e => e.IrreversibilityReason ?? $"{e.Name}: não reversível")
                .Distinct(StringComparer.Ordinal);
            return new BranchReversibility(false, string.Join(" | ", reasons));
        }

        return new BranchReversibility(true, null);
    }
}
