using XslSynth.Prompting;
using Xunit.Abstractions;

namespace XslSynth.Core.Tests;

/// <summary>
/// Medição real do spike da issue #151 (Fase A+B, aprovado pelo dono em 2026-09-07): quantas
/// funções catalogadas por reflection-only na DLL Sysmiddle real (vendorizada no repo em
/// <c>tools/LowCodeRunner/Functions/SysMiddle.ConnectUs.Functions.dll</c>) são de fato
/// reversíveis, segundo a curadoria manual de <see cref="FunctionReversibilityCatalog"/>.
///
/// Este NÃO é um teste de comportamento no sentido usual — é o instrumento de medição que produz
/// o número concreto usado em docs/architecture/spike-resultado-reconstrucao-reversa-2026-09-07.md.
/// Roda contra o artefato real (não sintético), mas pula graciosamente se a DLL não existir na
/// máquina (mesmo padrão de degradação de <see cref="XslSynth.Prompting.FunctionCatalog"/>).
/// </summary>
public sealed class SpikeReversibilidadeMeasurementTests
{
    private readonly ITestOutputHelper _output;

    public SpikeReversibilidadeMeasurementTests(ITestOutputHelper output) => _output = output;

    private static readonly string RealDllPath = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "tools", "LowCodeRunner", "Functions", "SysMiddle.ConnectUs.Functions.dll");

    [Fact]
    public void MedicaoReal_QuantasFuncoesDaDllSaoReversiveis()
    {
        Assert.True(File.Exists(RealDllPath),
            $"DLL vendorizada esperada em '{RealDllPath}' — sem ela o spike não tem número real pra reportar.");

        var logs = new List<string>();
        var catalog = FunctionCatalog.ExtractFromDll(RealDllPath, logs.Add);

        Assert.True(catalog.Count > 0, "catálogo vazio — extração por reflection falhou.");

        var reversible = catalog.All.Where(e => e.Reversible).ToList();
        var irreversible = catalog.All.Where(e => !e.Reversible).ToList();
        var naoCurada = catalog.All.Where(e => e.IrreversibilityReason == FunctionReversibilityCatalog.NaoCuradaReason).ToList();

        var pct = 100.0 * reversible.Count / catalog.Count;

        _output.WriteLine($"Total de funções extraídas (reflection, {Path.GetFileName(RealDllPath)}): {catalog.Count}");
        _output.WriteLine($"Reversíveis (curadoria Fase A): {reversible.Count} ({pct:F1}%)");
        _output.WriteLine($"Não-reversíveis (curadoria Fase A): {irreversible.Count}");
        _output.WriteLine($"Sem curadoria manual (default conservador): {naoCurada.Count}");
        _output.WriteLine("Reversíveis: " + string.Join(", ", reversible.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal)));

        // Trava de regressão do número reportado no spike — se a curadoria mudar, o documento do
        // spike precisa ser atualizado junto (não deixar o número do doc ficar desatualizado
        // silenciosamente).
        Assert.Equal(173, catalog.Count);
        Assert.Empty(naoCurada); // as 173 classes reais foram todas revisadas nesta curadoria
        Assert.Equal(23, reversible.Count);
    }
}
