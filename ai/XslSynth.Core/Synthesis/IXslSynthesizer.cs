using XslSynth.Core;
using XslSynth.Model;

namespace XslSynth.Synthesis;

/// <summary>
/// Fragmento XSLT que materializa uma Rule: a folha completa (com sua lógica interna)
/// e o caminho do pai onde deve ser inserida na árvore de saída.
/// </summary>
/// <param name="RuleName">Nome da regra de origem.</param>
/// <param name="TargetParentPath">XPath do elemento pai (ex.: "/Nota").</param>
/// <param name="LeafXsl">Elemento-folha XSLT como texto (ex.: "&lt;faixa&gt;&lt;xsl:choose&gt;...").</param>
public sealed record RuleFragment(string RuleName, string TargetParentPath, string LeafXsl);

/// <summary>Briefing estruturado passado ao sintetizador (o que ele precisa saber).</summary>
public sealed class SynthesisBriefing
{
    public required MapperVo Mapper { get; init; }
    public required string TargetRootName { get; init; }

    /// <summary>XSL já existente no mapeador (semente few-shot), se houver.</summary>
    public string? SeedXsl { get; init; }
}

/// <summary>
/// Passos 3 e 6 do loop (LLM): traduz Rules C#→XSLT e conserta a partir dos diffs.
/// Duas implementações: <see cref="OllamaXslSynthesizer"/> (LLM local) e
/// <see cref="MockXslSynthesizer"/> (determinístico, para o demo offline).
/// </summary>
public interface IXslSynthesizer
{
    /// <summary>Nome legível da implementação (para log).</summary>
    string Name { get; }

    /// <summary>Passo 3 — traduz cada Rule C# em um fragmento XSLT (completa os buracos).</summary>
    Task<IReadOnlyList<RuleFragment>> SynthesizeRulesAsync(
        SynthesisBriefing briefing, CancellationToken ct = default);

    /// <summary>Passo 6 — recebe os diffs residuais e devolve o XSLT corrigido (texto completo).</summary>
    Task<string> RepairFromDiffAsync(
        string currentXsl, IReadOnlyList<NodeDiff> diffs, SynthesisBriefing briefing, CancellationToken ct = default);
}
