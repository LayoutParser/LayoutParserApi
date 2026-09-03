using System.Xml.Linq;

using LayoutParserApi.Models.Entities;
using LayoutParserApi.Services.XmlAnalysis;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

// Mesmo racional de FieldMappingCompositionServiceSourceHealthTests: ambíguo com
// LayoutParserApi.Models.Entities.
using MappingKind = XslSynth.Model.MappingKind;
using Confidence = XslSynth.Model.Confidence;
using FieldToXmlMapping = XslSynth.Model.FieldToXmlMapping;
using XmlNodeReference = XslSynth.Model.XmlNodeReference;
using TxtFieldReference = XslSynth.Model.TxtFieldReference;
using XmlNodeKind = XslSynth.Model.XmlNodeKind;

namespace LayoutParserApi.Tests.Services.XmlAnalysis;

/// <summary>
/// Issue #151 (Fase 4) — reconstrução reversa best-effort XML→TXT. Cobre o contrato central: campo
/// 1:1 direto reconstrói; campo ausente no XML e campo derivado/multi-origem viram
/// <see cref="ReconstructionWarning"/>, nunca exceção.
/// </summary>
public sealed class ReverseReconstructionServiceTests
{
    private static ReverseReconstructionService CreateService() =>
        new(NullLogger<ReverseReconstructionService>.Instance);

    private static Layout SimpleLayout(int limitOfCaracters = 20) => new()
    {
        LayoutGuid = "LAY_1",
        Name = "LayoutTeste",
        LimitOfCaracters = limitOfCaracters,
        Elements = new List<LineElement>
        {
            new() { Name = "LINHA001", Sequence = 1 }
        }
    };

    private static TxtFieldReference Source(string lineName, int start, int length, int occurrence = 1) =>
        new("LIN_guid", lineName, "FLD_guid", "CAMPO", occurrence, start, length);

    [Fact]
    public void CampoDiretoComValorNoXml_ReconstroiNaPosicaoCorreta()
    {
        var mapping = new FieldToXmlMapping(
            MappingId: "m1",
            Sources: new[] { Source("LINHA001", start: 5, length: 4) },
            Targets: new[] { new XmlNodeReference("/NFe/det/prod/CFOP", XmlNodeKind.Element, null) },
            Kind: MappingKind.Direct,
            Confidence: Confidence.Authoritative);

        var xml = XDocument.Parse("<NFe><det><prod><CFOP>5102</CFOP></prod></det></NFe>");

        var result = CreateService().Reconstruct(SimpleLayout(), new[] { mapping }, xml);

        Assert.Equal(1, result.FieldsReconstructed);
        Assert.Equal(1, result.FieldsAttempted);
        Assert.Empty(result.Warnings);
        Assert.StartsWith("     5102", result.ReconstructedText);
    }

    [Fact]
    public void CampoAusenteNoXml_ViraWarningSemLancarExcecao()
    {
        var mapping = new FieldToXmlMapping(
            MappingId: "m1",
            Sources: new[] { Source("LINHA001", start: 0, length: 4) },
            Targets: new[] { new XmlNodeReference("/NFe/det/prod/CFOP", XmlNodeKind.Element, null) },
            Kind: MappingKind.Direct,
            Confidence: Confidence.Authoritative);

        var xml = XDocument.Parse("<NFe><det><prod/></det></NFe>");

        var result = CreateService().Reconstruct(SimpleLayout(), new[] { mapping }, xml);

        Assert.Equal(0, result.FieldsReconstructed);
        Assert.Single(result.Warnings);
        Assert.Equal(ReconstructionWarningKind.FieldNotFoundInXml, result.Warnings[0].Kind);
    }

    [Fact]
    public void ValorMaiorQueOTamanhoDeclarado_TruncaEAvisa()
    {
        var mapping = new FieldToXmlMapping(
            MappingId: "m1",
            Sources: new[] { Source("LINHA001", start: 0, length: 3) },
            Targets: new[] { new XmlNodeReference("/NFe/det/prod/CFOP", XmlNodeKind.Element, null) },
            Kind: MappingKind.Direct,
            Confidence: Confidence.Authoritative);

        var xml = XDocument.Parse("<NFe><det><prod><CFOP>5102</CFOP></prod></det></NFe>");

        var result = CreateService().Reconstruct(SimpleLayout(), new[] { mapping }, xml);

        Assert.Equal(1, result.FieldsReconstructed);
        Assert.Contains(result.Warnings, w => w.Kind == ReconstructionWarningKind.ValueTruncated);
        Assert.StartsWith("510", result.ReconstructedText);
    }

    [Fact]
    public void MapeamentoComMultiplasOrigens_NaoTemCaminhoReversoDeterministico()
    {
        var mapping = new FieldToXmlMapping(
            MappingId: "m1",
            Sources: new[] { Source("LINHA001", 0, 4), Source("LINHA001", 4, 4) },
            Targets: new[] { new XmlNodeReference("/NFe/det/prod/xProd", XmlNodeKind.Element, null) },
            Kind: MappingKind.Concatenated,
            Confidence: Confidence.BestEffort);

        var xml = XDocument.Parse("<NFe><det><prod><xProd>ABCEFGH</xProd></prod></det></NFe>");

        var result = CreateService().Reconstruct(SimpleLayout(), new[] { mapping }, xml);

        Assert.Equal(0, result.FieldsReconstructed);
        Assert.Single(result.Warnings);
        Assert.Equal(ReconstructionWarningKind.NotDeterministicallyReversible, result.Warnings[0].Kind);
    }

    [Fact]
    public void LayoutSemElementos_RetornaWarningDeProcessamentoSemLancar()
    {
        var xml = XDocument.Parse("<NFe/>");
        var result = CreateService().Reconstruct(new Layout { Elements = new List<LineElement>() }, Array.Empty<FieldToXmlMapping>(), xml);

        Assert.Single(result.Warnings);
        Assert.Equal(ReconstructionWarningKind.ProcessingError, result.Warnings[0].Kind);
    }
}
