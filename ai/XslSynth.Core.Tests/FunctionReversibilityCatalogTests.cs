using XslSynth.Prompting;

namespace XslSynth.Core.Tests;

/// <summary>Testes da Fase A da issue #151 (curadoria manual de reversibilidade por função).</summary>
public sealed class FunctionReversibilityCatalogTests
{
    [Fact]
    public void Funcao_naoCurada_degrada_para_falso_com_motivo_explicito()
    {
        var (reversible, reason) = FunctionReversibilityCatalog.Lookup("AlgumaFuncaoNovaNuncaVista");

        Assert.False(reversible);
        Assert.Equal(FunctionReversibilityCatalog.NaoCuradaReason, reason);
    }

    [Fact]
    public void CalculateVerifierDigit_curada_como_nao_reversivel()
    {
        // Citada explicitamente no critério de aceite da issue #151 como exemplo canônico de
        // função com perda — não pode virar true por acidente numa curadoria futura sem intenção.
        var (reversible, reason) = FunctionReversibilityCatalog.Lookup("CalculateVerifierDigitFunction");

        Assert.False(reversible);
        Assert.NotNull(reason);
    }

    [Fact]
    public void ConcatFunction_curada_como_nao_reversivel_por_ambiguidade_de_delimitador()
    {
        var (reversible, reason) = FunctionReversibilityCatalog.Lookup("ConcatFunction");

        Assert.False(reversible);
        Assert.Contains("delimitador", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ConvertToBase64StringFunction")]
    [InlineData("ConvertFromBase64StringFunction")]
    [InlineData("UriEscapeFunction")]
    [InlineData("UriUnescapeFunction")]
    public void Codificacoes_sem_perda_curadas_como_reversiveis(string className)
    {
        var (reversible, _) = FunctionReversibilityCatalog.Lookup(className);

        Assert.True(reversible);
    }

    [Fact]
    public void Toda_entrada_falsa_tem_motivo_nao_nulo()
    {
        // Mesmo padrão de FieldToXmlMapping.Limitations (design §7): nunca "false" silencioso.
        foreach (var className in KnownIrreversibleSample)
        {
            var (reversible, reason) = FunctionReversibilityCatalog.Lookup(className);
            Assert.False(reversible);
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }
    }

    private static readonly string[] KnownIrreversibleSample =
    {
        "CalculateVerifierDigitFunction", "ConcatFunction", "CreateNFeAcessKeyFunction",
        "ToUpperFunction", "ToLowerFunction", "GetHashCodeFunction", "NewGuidFunction",
        "SendEmailFunction", "ReadFileFunction", "RoundFunction"
    };

    [Fact]
    public void Curadoria_cobre_pelo_menos_as_173_classes_Function_reais_da_dll_vendorizada()
    {
        // Trava de regressão: se a curadoria diminuir sem querer (edição futura), o teste falha
        // aqui em vez de silenciosamente voltar pro default conservador "não curada". 173 é o
        // número de classes *Function reais confirmadas por reflection contra a DLL vendorizada
        // (ver SpikeReversibilidadeMeasurementTests) — a curadoria tem 175 entradas porque inclui
        // 2 nomes que não correspondem a nenhuma classe real encontrada (não fazem mal, só não
        // são usados).
        Assert.True(FunctionReversibilityCatalog.CuratedCount >= 173,
            $"esperava >= 173 entradas curadas, achou {FunctionReversibilityCatalog.CuratedCount}");
    }
}
