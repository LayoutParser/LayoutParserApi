using XslSynth.Core.StructuralResolution;
using XslSynth.Prompting;

namespace XslSynth.Core.Tests.StructuralResolution;

/// <summary>Testes da Fase B da issue #151 (generalização do composer para <c>Direction</c>) —
/// especificamente da costura entre a curadoria de reversibilidade da Fase A
/// (<see cref="FunctionCatalog"/>) e o nível de <see cref="StructuredBranch"/>.</summary>
public sealed class BranchReversibilityResolverTests
{
    private static StructuredBranch Branch(params string[] functions) =>
        new(Condition: "true", Target: "T.Doc/Campo", Sources: new[] { "I.LINHA01/CAMPO_A" }, Functions: functions);

    [Fact]
    public void SemFuncoes_SempreReversivel()
    {
        var result = BranchReversibilityResolver.Resolve(Branch(), catalog: null);

        Assert.True(result.Reversible);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void CatalogoIndisponivel_DegradaParaNaoReversivel_SemLancar()
    {
        var branch = Branch("QualquerFuncao");

        var result = BranchReversibilityResolver.Resolve(branch, catalog: null);

        Assert.False(result.Reversible);
        Assert.Contains("indisponível", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FuncaoNaoCatalogada_NaoReversivel()
    {
        var catalog = FunctionCatalog.ExtractFromDll(@"C:\caminho\que\nao\existe.dll"); // catálogo vazio
        var branch = Branch("ConcatString");

        var result = BranchReversibilityResolver.Resolve(branch, catalog);

        Assert.False(result.Reversible);
        Assert.Contains("não catalogada", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly string RealDllPath = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "tools", "LowCodeRunner", "Functions", "SysMiddle.ConnectUs.Functions.dll");

    [Fact]
    public void FuncaoReversivelCatalogada_TornaBranchReversivel()
    {
        if (!File.Exists(RealDllPath)) return; // DLL vendorizada não encontrada nesta máquina — pula graciosamente.

        var catalog = FunctionCatalog.ExtractFromDll(RealDllPath);
        // "Concat" é o nome-palpite real (ConcatFunction -> Concat via GuessDslName) — mas
        // Concat é curado como NÃO reversível (ambiguidade de delimitador), então o branch some
        // como não-reversível mesmo estando catalogado. Já "UriEscape" é curado como reversível.
        var branch = Branch("UriEscape");

        var result = BranchReversibilityResolver.Resolve(branch, catalog);

        Assert.True(result.Reversible);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void FuncaoComPerdaCatalogada_TornaBranchNaoReversivel()
    {
        if (!File.Exists(RealDllPath)) return;

        var catalog = FunctionCatalog.ExtractFromDll(RealDllPath);
        var branch = Branch("Concat"); // ConcatFunction -> "Concat", curado como false

        var result = BranchReversibilityResolver.Resolve(branch, catalog);

        Assert.False(result.Reversible);
        Assert.Contains("delimitador", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UmaFuncaoIrreversivelEntreVariasReversiveis_TornaBranchInteiroNaoReversivel()
    {
        if (!File.Exists(RealDllPath)) return;

        var catalog = FunctionCatalog.ExtractFromDll(RealDllPath);
        // UriEscape (true) + Concat (false) juntos — composição não é bijetora.
        var branch = Branch("UriEscape", "Concat");

        var result = BranchReversibilityResolver.Resolve(branch, catalog);

        Assert.False(result.Reversible);
    }
}
