using XslSynth.Core.StructuralResolution;
using XslSynth.Model;

namespace XslSynth.Core.Tests.StructuralResolution;

/// <summary>
/// Testes do item 5 (issue #140, design §5, §6.1) — motor de composição <c>FieldToXmlMapping[]</c>
/// sobre a árvore sintética de <see cref="SyntheticXmlCatalogBuilder"/>. Cobre a matriz mínima
/// pedida (direct, N:1, 1:N, estático, concatenado, grupo repetido com ocorrência, mismatch de
/// repetição). Nenhum dado real de documento — só GUIDs/nomes/estrutura sintéticos.
/// </summary>
public sealed class FieldToXmlMappingComposerTests
{
    private readonly XmlLayoutCatalog _catalog = SyntheticXmlCatalogBuilder.Build();

    private static TxtFieldReference Src(string field, int occurrence = 0) =>
        new("LIN_LINHA01", "LINHA01", $"FLD_{field}", field, occurrence, StartPosition: 1, Length: 10);

    private FieldToXmlMappingComposer Composer() => new(_catalog);

    [Fact]
    public void Direct_CampoSimples_ResolveAuthoritative()
    {
        var candidate = new MappingCandidate(
            MappingId: "M1",
            Sources: new[] { Src("CAMPO_A") },
            Kind: MappingKind.Direct,
            TargetPath: "Doc/Cabecalho/CampoA",
            TargetPathIsFullPath: true,
            Functions: Array.Empty<string>(),
            LoopType: null,
            AllSourcesResolvedFromOriginLayout: true,
            SourcesHavePositionalGroupRepetition: false,
            KnownFunctions: new HashSet<string>());

        var result = Composer().Compose(candidate);

        Assert.Equal(Confidence.Authoritative, result.Confidence);
        Assert.Null(result.Limitations);
        Assert.Single(result.Targets);
        Assert.Equal("/ns0:Doc/ns0:Cabecalho/ns0:CampoA", result.Targets[0].Xpath);
        Assert.Equal(XmlNodeKind.Element, result.Targets[0].NodeKind);
    }

    [Fact]
    public void Static_SemOrigens_ResolveAuthoritative()
    {
        var candidate = new MappingCandidate(
            MappingId: "M2",
            Sources: Array.Empty<TxtFieldReference>(),
            Kind: MappingKind.Static,
            TargetPath: "Doc/Total",
            TargetPathIsFullPath: true,
            Functions: Array.Empty<string>(),
            LoopType: null,
            AllSourcesResolvedFromOriginLayout: false, // irrelevante quando Static (condição 1: OR)
            SourcesHavePositionalGroupRepetition: false,
            KnownFunctions: new HashSet<string>());

        var result = Composer().Compose(candidate);

        Assert.Equal(Confidence.Authoritative, result.Confidence);
        Assert.Empty(result.Sources);
    }

    [Fact]
    public void Concatenated_NOrigensParaUmDestino_ResolveAuthoritative()
    {
        var candidate = new MappingCandidate(
            MappingId: "M3",
            Sources: new[] { Src("CAMPO_A"), Src("CAMPO_B") },
            Kind: MappingKind.Concatenated,
            TargetPath: "Doc/Total",
            TargetPathIsFullPath: true,
            Functions: new[] { "ConcatString" },
            LoopType: null,
            AllSourcesResolvedFromOriginLayout: true,
            SourcesHavePositionalGroupRepetition: false,
            KnownFunctions: new HashSet<string> { "ConcatString" });

        var result = Composer().Compose(candidate);

        Assert.Equal(Confidence.Authoritative, result.Confidence);
        Assert.Equal(2, result.Sources.Count);
        Assert.Single(result.Targets);
    }

    [Fact]
    public void UmaOrigemParaDoisDestinos_DoisMappingsIndependentes()
    {
        // 1:N não é campo novo no modelo (design §3.1) — é resultado de iterar Rules/LinkMappings
        // e cada um produzir seu próprio FieldToXmlMapping com a mesma TxtFieldReference.
        var origem = Src("CAMPO_A");
        var composer = Composer();

        var m1 = composer.Compose(new MappingCandidate("M4a", new[] { origem }, MappingKind.Direct,
            "Doc/Cabecalho/CampoA", true, Array.Empty<string>(), null, true, false, new HashSet<string>()));
        var m2 = composer.Compose(new MappingCandidate("M4b", new[] { origem }, MappingKind.Direct,
            "Doc/Total", true, Array.Empty<string>(), null, true, false, new HashSet<string>()));

        Assert.Equal(Confidence.Authoritative, m1.Confidence);
        Assert.Equal(Confidence.Authoritative, m2.Confidence);
        Assert.Equal(origem, m1.Sources[0]);
        Assert.Equal(origem, m2.Sources[0]);
        Assert.NotEqual(m1.Targets[0].Xpath, m2.Targets[0].Xpath);
    }

    [Fact]
    public void GrupoRepetido_LineOccurrenceViraXmlOccurrence_Authoritative()
    {
        var candidate = new MappingCandidate(
            MappingId: "M5",
            Sources: new[] { Src("VALOR_ITEM", occurrence: 2) },
            Kind: MappingKind.Direct,
            TargetPath: "Doc/Itens/Item/Valor",
            TargetPathIsFullPath: true,
            Functions: Array.Empty<string>(),
            LoopType: null,
            AllSourcesResolvedFromOriginLayout: true,
            SourcesHavePositionalGroupRepetition: true, // LineElement.IsPositionalGroupRepetition == true
            KnownFunctions: new HashSet<string>());

        var result = Composer().Compose(candidate);

        Assert.Equal(Confidence.Authoritative, result.Confidence);
        Assert.Equal(2, result.Targets[0].XmlOccurrence);
    }

    [Fact]
    public void MismatchDeRepeticao_OrigemRepetidaDestinoNao_CaiEmBestEffort()
    {
        var candidate = new MappingCandidate(
            MappingId: "M6",
            Sources: new[] { Src("CAMPO_A", occurrence: 1) },
            Kind: MappingKind.Direct,
            TargetPath: "Doc/Total", // destino NÃO repete
            TargetPathIsFullPath: true,
            Functions: Array.Empty<string>(),
            LoopType: null,
            AllSourcesResolvedFromOriginLayout: true,
            SourcesHavePositionalGroupRepetition: true, // origem repete
            KnownFunctions: new HashSet<string>());

        var result = Composer().Compose(candidate);

        Assert.Equal(Confidence.BestEffort, result.Confidence);
        Assert.NotNull(result.Limitations);
        Assert.Contains(result.Limitations!, l => l.Contains("Repetição não confirmada"));
    }

    [Fact]
    public void LoopDinamico_CaiEmBestEffort()
    {
        var candidate = new MappingCandidate(
            MappingId: "M7",
            Sources: new[] { Src("CAMPO_A") },
            Kind: MappingKind.Transformed,
            TargetPath: "Doc/Itens/Item/Valor",
            TargetPathIsFullPath: true,
            Functions: Array.Empty<string>(),
            LoopType: "foreach",
            AllSourcesResolvedFromOriginLayout: true,
            SourcesHavePositionalGroupRepetition: false,
            KnownFunctions: new HashSet<string>());

        var result = Composer().Compose(candidate);

        Assert.Equal(Confidence.BestEffort, result.Confidence);
        Assert.Contains(result.Limitations!, l => l.Contains("Loop dinâmico"));
    }

    [Fact]
    public void FuncaoDesconhecidaNoCatalogo_CaiEmBestEffort()
    {
        var candidate = new MappingCandidate(
            MappingId: "M8",
            Sources: new[] { Src("CAMPO_A") },
            Kind: MappingKind.Transformed,
            TargetPath: "Doc/Chave",
            TargetPathIsFullPath: true,
            Functions: new[] { "CalculateVerifierDigit" },
            LoopType: null,
            AllSourcesResolvedFromOriginLayout: true,
            SourcesHavePositionalGroupRepetition: false,
            KnownFunctions: new HashSet<string>()); // catálogo vazio => função não catalogada

        var result = Composer().Compose(candidate);

        Assert.Equal(Confidence.BestEffort, result.Confidence);
        Assert.Contains(result.Limitations!, l => l.Contains("não catalogada"));
    }

    [Fact]
    public void FunctionCatalogIndisponivel_CaiEmBestEffort_SemQuebrar()
    {
        var candidate = new MappingCandidate(
            MappingId: "M9",
            Sources: new[] { Src("CAMPO_A") },
            Kind: MappingKind.Transformed,
            TargetPath: "Doc/Chave",
            TargetPathIsFullPath: true,
            Functions: new[] { "CalculateVerifierDigit" },
            LoopType: null,
            AllSourcesResolvedFromOriginLayout: true,
            SourcesHavePositionalGroupRepetition: false,
            KnownFunctions: null); // FunctionCatalog indisponível no host — degrada, não lança

        var result = Composer().Compose(candidate);

        Assert.Equal(Confidence.BestEffort, result.Confidence);
        Assert.Contains(result.Limitations!, l => l.Contains("FunctionCatalog indisponível"));
    }

    [Fact]
    public void DestinoNaoEncontradoNoCatalogo_NuncaLancaExcecao_CaiEmBestEffortSemTargets()
    {
        var candidate = new MappingCandidate(
            MappingId: "M10",
            Sources: new[] { Src("CAMPO_A") },
            Kind: MappingKind.Direct,
            TargetPath: "Doc/NaoExiste/Campo",
            TargetPathIsFullPath: true,
            Functions: Array.Empty<string>(),
            LoopType: null,
            AllSourcesResolvedFromOriginLayout: true,
            SourcesHavePositionalGroupRepetition: false,
            KnownFunctions: new HashSet<string>());

        var result = Composer().Compose(candidate);

        Assert.Equal(Confidence.BestEffort, result.Confidence);
        Assert.Empty(result.Targets);
        Assert.Contains(result.Limitations!, l => l.Contains("não encontrado"));
    }

    [Fact]
    public void ResolucaoPorNomeDeFolha_NuncaAuthoritative_MesmoQuandoUnica()
    {
        // LinkMappingItem só tem TargetLeafName (sem caminho completo) — mesmo resolvendo
        // sem ambiguidade, é heurística de nome, não caminho estrutural (design §5, condição 2).
        var candidate = new MappingCandidate(
            MappingId: "M11",
            Sources: new[] { Src("CAMPO_A") },
            Kind: MappingKind.Direct,
            TargetPath: "CampoA", // só o nome da folha
            TargetPathIsFullPath: false,
            Functions: Array.Empty<string>(),
            LoopType: null,
            AllSourcesResolvedFromOriginLayout: true,
            SourcesHavePositionalGroupRepetition: false,
            KnownFunctions: new HashSet<string>());

        var result = Composer().Compose(candidate);

        Assert.Equal(Confidence.BestEffort, result.Confidence);
        Assert.Single(result.Targets); // resolveu (nome único), mas não é authoritative
        Assert.Contains(result.Limitations!, l => l.Contains("heurística de nome"));
    }
}
