using XslSynth.Metrics;

namespace XslSynth.Core.Tests;

/// <summary>
/// Cobertura do critério de aceite do #352: "resumo agregado ao final do lote reporta a taxa de
/// convergência real (%) por modelo". Testa a agregação PURA (<see cref="RepairBatchRunner.Summarize"/>)
/// a partir de <see cref="RepairCaseResult"/> simulados — sem Ollama, sem I/O, sem Serilog.
/// </summary>
public class RepairBatchSummaryTests
{
    private static RepairCaseResult Medido(string id, bool converged, int iterations, int diffs, bool? xsdValid = true) =>
        new(id, InstanceMatched: true, Converged: converged, Iterations: iterations,
            FinalDiffsCount: diffs, XsdValid: xsdValid, Fixture: "instancia.txt", Erro: null);

    private static RepairCaseResult SemInstancia(string id) =>
        new(id, InstanceMatched: false, Converged: false, Iterations: 0, FinalDiffsCount: -1,
            XsdValid: null, Fixture: null, Erro: "sem instância");

    [Fact]
    public void Summarize_ComTodosConvergindo_TaxaCemPorCento()
    {
        var resultados = new[]
        {
            Medido("caso1", converged: true, iterations: 1, diffs: 0),
            Medido("caso2", converged: true, iterations: 3, diffs: 0),
        };

        var s = RepairBatchRunner.Summarize(resultados);

        Assert.Equal(2, s.TotalCasos);
        Assert.Equal(2, s.Medidos);
        Assert.Equal(0, s.SemInstancia);
        Assert.Equal(2, s.Convergidos);
        Assert.Equal(1.0, s.TaxaConvergenciaReal);
        Assert.Equal(2.0, s.IteracoesMediasConvergidos); // média de (1, 3)
    }

    [Fact]
    public void Summarize_ComMistoConvergeENaoConverge_TaxaFracionaria()
    {
        var resultados = new[]
        {
            Medido("caso1", converged: true, iterations: 2, diffs: 0),
            Medido("caso2", converged: false, iterations: 5, diffs: 3),
            Medido("caso3", converged: true, iterations: 4, diffs: 0),
            Medido("caso4", converged: false, iterations: 5, diffs: 1),
        };

        var s = RepairBatchRunner.Summarize(resultados);

        Assert.Equal(4, s.Medidos);
        Assert.Equal(2, s.Convergidos);
        Assert.Equal(0.5, s.TaxaConvergenciaReal);
        Assert.Equal(3.0, s.IteracoesMediasConvergidos); // média de (2, 4), NÃO conta os que falharam
    }

    [Fact]
    public void Summarize_CasosSemInstancia_NaoEntramNoDenominadorDaTaxa()
    {
        // 1 medido (converge) + 3 sem instância: a taxa tem que ser 100% sobre o MEDIDO,
        // não 25% sobre o total — é a regra central que evita falso-negativo sistemático
        // (issue #352, "Taxa calculada sobre os casos MEDIDOS, não sobre o total").
        var resultados = new[]
        {
            Medido("caso1", converged: true, iterations: 1, diffs: 0),
            SemInstancia("caso2"),
            SemInstancia("caso3"),
            SemInstancia("caso4"),
        };

        var s = RepairBatchRunner.Summarize(resultados);

        Assert.Equal(4, s.TotalCasos);
        Assert.Equal(1, s.Medidos);
        Assert.Equal(3, s.SemInstancia);
        Assert.Equal(1, s.Convergidos);
        Assert.Equal(1.0, s.TaxaConvergenciaReal);
    }

    [Fact]
    public void Summarize_NenhumCasoMedido_TaxaENullNaoZero()
    {
        // Divisão por zero disfarçada de "0% de convergência" seria enganosa — null sinaliza
        // "não medido", diferente de "medido e falhou tudo".
        var resultados = new[] { SemInstancia("caso1"), SemInstancia("caso2") };

        var s = RepairBatchRunner.Summarize(resultados);

        Assert.Equal(0, s.Medidos);
        Assert.Null(s.TaxaConvergenciaReal);
        Assert.Null(s.IteracoesMediasConvergidos);
    }

    [Fact]
    public void Summarize_MedidoMasNenhumConverge_TaxaZeroIteracoesMediaNull()
    {
        var resultados = new[]
        {
            Medido("caso1", converged: false, iterations: 5, diffs: 2),
            Medido("caso2", converged: false, iterations: 5, diffs: 1),
        };

        var s = RepairBatchRunner.Summarize(resultados);

        Assert.Equal(2, s.Medidos);
        Assert.Equal(0, s.Convergidos);
        Assert.Equal(0.0, s.TaxaConvergenciaReal);
        Assert.Null(s.IteracoesMediasConvergidos); // nenhum convergiu — não há iteração média a reportar
    }

    [Fact]
    public void Summarize_ListaVazia_TudoZeroOuNull()
    {
        var s = RepairBatchRunner.Summarize(Array.Empty<RepairCaseResult>());

        Assert.Equal(0, s.TotalCasos);
        Assert.Equal(0, s.Medidos);
        Assert.Equal(0, s.SemInstancia);
        Assert.Equal(0, s.Convergidos);
        Assert.Null(s.TaxaConvergenciaReal);
        Assert.Null(s.IteracoesMediasConvergidos);
    }
}
