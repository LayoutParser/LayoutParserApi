using LayoutParserApi.Models.Entities;
using LayoutParserApi.Services.Transformation.StructuralResolution;

using Xunit;

// Ambíguo com LayoutParserApi.Models.Entities (mesmo racional do FieldMappingCompositionService).
using MappingKind = XslSynth.Model.MappingKind;
using Confidence = XslSynth.Model.Confidence;
using FieldToXmlMapping = XslSynth.Model.FieldToXmlMapping;
using XmlNodeReference = XslSynth.Model.XmlNodeReference;
using TxtFieldReference = XslSynth.Model.TxtFieldReference;
using XmlNodeKind = XslSynth.Model.XmlNodeKind;

namespace LayoutParserApi.Tests.StructuralResolution;

/// <summary>
/// Achado 2 do QA gate da issue #140 (<c>issue140-resolucao-estrutural-qa-gate.md</c>): o motor de
/// resolução estrutural não considerava <see cref="LineInfo.IsDeclaredEmpty"/>/
/// <see cref="LineInfo.PositionalAlignmentFailed"/> (contrato de degradação posicional de
/// 2026-08-27) ao classificar <c>Authoritative</c>/<c>BestEffort</c>. Esta suíte cobre a 6ª condição
/// adicionada em <see cref="FieldMappingCompositionService.DegradeForUnhealthySourceLines"/> —
/// testada isoladamente (sem depender de <c>FunctionCatalog</c>, que hoje é sempre <c>null</c> no
/// wiring real e por si só já força <c>BestEffort</c> via critério §5 condição 5, o que mascararia
/// o efeito desta 6ª condição num teste ponta a ponta).
/// </summary>
public sealed class FieldMappingCompositionServiceSourceHealthTests
{
    private static TxtFieldReference Source(string lineName, int occurrence) =>
        new("LIN_guid", lineName, "FLD_guid", "CAMPO", occurrence, 0, 10);

    private static FieldToXmlMapping AuthoritativeMapping(TxtFieldReference source) => new(
        MappingId: "m1",
        Sources: new[] { source },
        Targets: new[] { new XmlNodeReference("/NFe/det/prod/CFOP", XmlNodeKind.Element, 1) },
        Kind: MappingKind.Direct,
        Confidence: Confidence.Authoritative,
        Limitations: null);

    private static IReadOnlyDictionary<(string, int), LineInfo> LookupOf(params LineInfo[] infos)
    {
        var dict = new Dictionary<(string, int), LineInfo>();
        foreach (var info in infos)
            dict[(info.LineName, info.Occurrence)] = info;
        return dict;
    }

    [Fact]
    public void LinhaDeclaradaVazia_DegradaAuthoritativeParaBestEffort()
    {
        var source = Source("LINHA081", 1);
        var mapping = AuthoritativeMapping(source);
        var lookup = LookupOf(new LineInfo { LineName = "LINHA081", Occurrence = 1, IsDeclaredEmpty = true });

        var result = FieldMappingCompositionService.DegradeForUnhealthySourceLines(mapping, lookup);

        Assert.Equal(Confidence.BestEffort, result.Confidence);
        Assert.NotNull(result.Limitations);
        Assert.Contains(result.Limitations!, l => l.Contains("declarada vazia"));
    }

    [Fact]
    public void LinhaComDegradacaoPosicional_DegradaAuthoritativeParaBestEffort()
    {
        var source = Source("LINHA006", 2);
        var mapping = AuthoritativeMapping(source);
        var lookup = LookupOf(new LineInfo { LineName = "LINHA006", Occurrence = 2, PositionalAlignmentFailed = true });

        var result = FieldMappingCompositionService.DegradeForUnhealthySourceLines(mapping, lookup);

        Assert.Equal(Confidence.BestEffort, result.Confidence);
        Assert.NotNull(result.Limitations);
        Assert.Contains(result.Limitations!, l => l.Contains("degradação posicional"));
    }

    [Fact]
    public void LinhaSaudavel_MantemAuthoritative()
    {
        var source = Source("LINHA001", 1);
        var mapping = AuthoritativeMapping(source);
        var lookup = LookupOf(new LineInfo { LineName = "LINHA001", Occurrence = 1 }); // sem flags

        var result = FieldMappingCompositionService.DegradeForUnhealthySourceLines(mapping, lookup);

        Assert.Equal(Confidence.Authoritative, result.Confidence);
        Assert.Null(result.Limitations);
    }

    [Fact]
    public void SemLineInfoCorrespondente_NaoAlteraOResultado()
    {
        var source = Source("LINHA999", 1);
        var mapping = AuthoritativeMapping(source);
        var lookup = LookupOf(new LineInfo { LineName = "OUTRA_LINHA", Occurrence = 1, IsDeclaredEmpty = true });

        var result = FieldMappingCompositionService.DegradeForUnhealthySourceLines(mapping, lookup);

        Assert.Equal(Confidence.Authoritative, result.Confidence);
        Assert.Null(result.Limitations);
    }

    [Fact]
    public void JaBestEffort_AcrescentaLimitationSemPerderAsExistentes()
    {
        var source = Source("LINHA081", 1);
        var mapping = AuthoritativeMapping(source) with
        {
            Confidence = Confidence.BestEffort,
            Limitations = new[] { "Destino resolvido por heurística de nome (fallback), não por caminho completo." }
        };
        var lookup = LookupOf(new LineInfo { LineName = "LINHA081", Occurrence = 1, IsDeclaredEmpty = true });

        var result = FieldMappingCompositionService.DegradeForUnhealthySourceLines(mapping, lookup);

        Assert.Equal(Confidence.BestEffort, result.Confidence);
        Assert.Equal(2, result.Limitations!.Count);
        Assert.Contains(result.Limitations!, l => l.Contains("heurística de nome"));
        Assert.Contains(result.Limitations!, l => l.Contains("declarada vazia"));
    }
}
