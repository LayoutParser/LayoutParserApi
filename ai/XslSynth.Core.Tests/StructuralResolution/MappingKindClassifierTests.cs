using XslSynth.Core.StructuralResolution;
using XslSynth.Model;
using XslSynth.Prompting;

namespace XslSynth.Core.Tests.StructuralResolution;

/// <summary>Testes do item 3 (issue #140, design §3) — classificação puramente estrutural sobre
/// <see cref="StructuredRule"/> sintética (nenhum dado real de mapeador).</summary>
public sealed class MappingKindClassifierTests
{
    private static StructuredRule Rule(
        string? staticValue = null,
        string? loopType = null,
        params (string condition, string target, string[] sources, string[] functions)[] branches)
    {
        var list = branches.Select(b => new StructuredBranch(b.condition, b.target, b.sources, b.functions)).ToList();
        return new StructuredRule(StructuredRuleSchema.Version, "RULE_1", "regra-teste", list.FirstOrDefault()?.Target, list, staticValue, loopType);
    }

    [Fact]
    public void ClassifyLinkMapping_SempreDirect()
    {
        Assert.Equal(MappingKind.Direct, MappingKindClassifier.ClassifyLinkMapping());
    }

    [Fact]
    public void ClassifyRule_ValorEstaticoSemOrigens_Static()
    {
        var rule = Rule(staticValue: "1", branches: ("true", "NFe/infNFe/det/prod/CFOP", Array.Empty<string>(), Array.Empty<string>()));

        Assert.Equal(MappingKind.Static, MappingKindClassifier.ClassifyRule(rule));
    }

    [Fact]
    public void ClassifyRule_DuasOrigensComConcatString_Concatenated()
    {
        var rule = Rule(branches: ("true", "NFe/infNFe/ide/natOp",
            new[] { "LINHA01/CAMPO_A", "LINHA01/CAMPO_B" }, new[] { "ConcatString" }));

        Assert.Equal(MappingKind.Concatenated, MappingKindClassifier.ClassifyRule(rule));
    }

    [Fact]
    public void ClassifyRule_DuasOrigensSemFuncaoDeConcat_Transformed()
    {
        // Duas sources em branches condicionais diferentes, sem ConcatString — não deve inferir
        // concatenação só pela contagem (design §3, item 3, nota explícita).
        var rule = Rule(branches: new (string, string, string[], string[])[]
        {
            ("len(campoChaveAcesso) == 44", "NFe/infNFe/@Id", new[] { "LINHA01/CHAVE" }, Array.Empty<string>()),
            ("else", "NFe/infNFe/@Id", new[] { "LINHA01/CHAVE_ALT" }, Array.Empty<string>())
        });

        Assert.Equal(MappingKind.Transformed, MappingKindClassifier.ClassifyRule(rule));
    }

    [Fact]
    public void ClassifyRule_FuncaoNaoConcatenadora_Transformed()
    {
        var rule = Rule(branches: ("true", "NFe/infNFe/@Id",
            new[] { "LINHA01/CHAVE" }, new[] { "CalculateVerifierDigit" }));

        Assert.Equal(MappingKind.Transformed, MappingKindClassifier.ClassifyRule(rule));
    }

    [Fact]
    public void ClassifyRule_ComLoop_Transformed()
    {
        var rule = Rule(loopType: "foreach", branches: ("true", "NFe/infNFe/det/prod/CFOP",
            new[] { "LINHA_ITEM/CFOP" }, Array.Empty<string>()));

        Assert.Equal(MappingKind.Transformed, MappingKindClassifier.ClassifyRule(rule));
    }
}
