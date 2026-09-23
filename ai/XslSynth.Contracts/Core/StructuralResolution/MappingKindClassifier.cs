using XslSynth.Model;
using XslSynth.Prompting;

namespace XslSynth.Core.StructuralResolution;

/// <summary>
/// Item 3 da divisão de trabalho da issue #140 (design §3): deriva <see cref="MappingKind"/> a
/// partir de <see cref="StructuredRule"/> — sem regex ad-hoc, tudo sobre a representação já
/// parseada por <see cref="DslStructuredParser"/> (Camada 0).
/// </summary>
public static class MappingKindClassifier
{
    /// <summary>Funções de concatenação conhecidas — <c>"ConcatString"</c> confirmado como exemplo
    /// real em <c>StructuredRuleSchema.cs</c> (design §3, item 3). Lista fechada e auditável: uma
    /// função fora desta lista, mesmo que "pareça" concatenar, cai em <see cref="MappingKind.Transformed"/>
    /// — não é adivinhação por nome, é lista fechada.</summary>
    private static readonly HashSet<string> ConcatenationFunctions =
        new(StringComparer.Ordinal) { "ConcatString" };

    /// <summary>Classifica um mapeamento vindo de <c>LinkMappings</c> (sem DSL) — sempre <see cref="MappingKind.Direct"/>
    /// por definição estrutural (design §3, item 2).</summary>
    public static MappingKind ClassifyLinkMapping() => MappingKind.Direct;

    /// <summary>Classifica um mapeamento vindo de <c>Rules</c> (DSL Sysmiddle já traduzida para <see cref="StructuredRule"/>).</summary>
    public static MappingKind ClassifyRule(StructuredRule rule)
    {
        // 1. static — sem nenhuma origem I., só literal.
        if (rule.StaticValue is not null && rule.AllSources.Count == 0)
        {
            return MappingKind.Static;
        }

        // 3. concatenated — múltiplas origens E função de concatenação conhecida.
        //    Não infere por contagem de sources sozinha (uma condicional pode ter 2 sources em
        //    ramos diferentes sem concatenar nada — cai em transformed, item 4 abaixo).
        if (rule.AllSources.Count > 1 && rule.AllFunctions.Any(ConcatenationFunctions.Contains))
        {
            return MappingKind.Concatenated;
        }

        // 4. transformed — catch-all: função não-concatenadora, múltiplos branches (condicional)
        //    ou loop. StructuredRule já normaliza toda a DSL — não há 5ª categoria escondida.
        return MappingKind.Transformed;
    }
}
