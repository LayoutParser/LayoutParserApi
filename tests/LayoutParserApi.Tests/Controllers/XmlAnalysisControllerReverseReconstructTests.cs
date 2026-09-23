using LayoutParserApi.Controllers;
using LayoutParserApi.Models.Configuration;
using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Parsing;
using LayoutParserApi.Models.Responses;
using LayoutParserApi.Models.Structure;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.XmlAnalysis;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

using MappingKind = XslSynth.Model.MappingKind;
using Confidence = XslSynth.Model.Confidence;
using FieldToXmlMapping = XslSynth.Model.FieldToXmlMapping;
using XmlNodeReference = XslSynth.Model.XmlNodeReference;
using TxtFieldReference = XslSynth.Model.TxtFieldReference;
using XmlNodeKind = XslSynth.Model.XmlNodeKind;

namespace LayoutParserApi.Tests.Controllers;

/// <summary>
/// Issue #151 (Fase 4) — wiring do endpoint <c>POST api/xml-analysis/reverse-reconstruct</c>:
/// reconstrução best-effort XML→TXT (já coberta em <c>ReverseReconstructionServiceTests</c>) mais o
/// gap real da issue, validação contra o TXT original quando disponível (item 3 do critério de
/// aceite). Usa um <see cref="ILayoutParserService"/> falso (mesmo padrão de
/// <c>TransformationExecutionControllerFieldMappingsTests</c>) — não reimplementa o parser real.
/// </summary>
public sealed class XmlAnalysisControllerReverseReconstructTests
{
    private const string LayoutXml =
        "<LayoutVO xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
        "<LayoutGuid>LAY_1</LayoutGuid><Name>LayoutTeste</Name><LimitOfCaracters>20</LimitOfCaracters>" +
        "<Elements><Element xsi:type=\"LineElementVO\"><Name>LINHA001</Name><Sequence>1</Sequence></Element></Elements>" +
        "</LayoutVO>";

    private const string TargetXml = "<NFe><det><prod><CFOP>5102</CFOP></prod></det></NFe>";

    /// <summary>Um único FieldToXmlMapping direto, mesmo shape dos testes de
    /// <c>ReverseReconstructionServiceTests</c> — CFOP na posição 5..8 de LINHA001.</summary>
    private static List<FieldToXmlMapping> OneDirectMapping() => new()
    {
        new FieldToXmlMapping(
            MappingId: "m1",
            Sources: new[] { new TxtFieldReference("LIN_guid", "LINHA001", "FLD_guid", "CFOP", 1, 5, 4) },
            Targets: new[] { new XmlNodeReference("/NFe/det/prod/CFOP", XmlNodeKind.Element, null) },
            Kind: MappingKind.Direct,
            Confidence: Confidence.Authoritative)
    };

    private sealed class FakeLayoutParserService : ILayoutParserService
    {
        /// <summary>Valor que o parse do TXT original "encontraria" para LINHA001/CFOP — controlado
        /// por teste para simular bate/diverge/ausente.</summary>
        public string? CfopValueNoTxtOriginal { get; set; } = "5102";
        public bool Falhar { get; set; }

        public Task<ParsingResult> ParseAsync(Stream layoutStream, Stream txtStream)
        {
            if (Falhar)
                return Task.FromResult(new ParsingResult { Success = false, ErrorMessage = "parse sintético falhou de propósito" });

            var parsedFields = new List<ParsedField>();
            if (CfopValueNoTxtOriginal != null)
            {
                parsedFields.Add(new ParsedField
                {
                    LineName = "LINHA001",
                    FieldName = "CFOP",
                    Occurrence = 1,
                    OccurrenceCount = 1,
                    IsAggregatedOccurrence = false,
                    Value = CfopValueNoTxtOriginal,
                    Start = 5,
                    Length = 4
                });
            }

            return Task.FromResult(new ParsingResult { Success = true, ParsedFields = parsedFields });
        }

        public DocumentStructure BuildDocumentStructure(ParsingResult result) => throw new NotImplementedException();
        public Layout ReordenarSequences(Layout layout) => throw new NotImplementedException();
        public Layout ReestruturarLayout(Layout layoutOriginal) => throw new NotImplementedException();
        public List<LineValidationInfo> CalculateLineValidations(Layout layout, int expectedLineLength = LineLengthResolver.LegacyDefaultLineLength) => throw new NotImplementedException();
        public Task<Layout?> ParseLayoutFromXmlAsync(string xmlContent) => throw new NotImplementedException();
    }

    private static XmlAnalysisController CreateController(FakeLayoutParserService? layoutParser = null)
    {
        var configuration = new ConfigurationBuilder().Build();
        var xsdValidationService = new XsdValidationService(
            NullLogger<XsdValidationService>.Instance,
            configuration,
            new XmlDocumentTypeDetector(NullLogger<XmlDocumentTypeDetector>.Instance),
            new PdfOrientationReader(NullLogger<PdfOrientationReader>.Instance));

        return new XmlAnalysisController(
            new XmlAnalysisService(NullLogger<XmlAnalysisService>.Instance),
            xsdValidationService,
            new ReverseReconstructionService(NullLogger<ReverseReconstructionService>.Instance),
            layoutParser ?? new FakeLayoutParserService(),
            NullLogger<XmlAnalysisController>.Instance);
    }

    private static ReverseReconstructionResponse OkValue(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<ReverseReconstructionResponse>(ok.Value);
    }

    [Fact]
    public async Task SemOriginalTxt_ReconstroiSemSecaoDeValidacao()
    {
        var controller = CreateController();
        var request = new ReverseReconstructionRequest
        {
            LayoutXml = LayoutXml,
            FieldMappings = OneDirectMapping(),
            TargetXml = TargetXml
        };

        var result = await controller.ReverseReconstruct(request);

        var value = OkValue(result);
        Assert.True(value.Success);
        Assert.Equal(1, value.FieldsReconstructed);
        Assert.Null(value.Validation);
    }

    [Fact]
    public async Task ComOriginalTxt_ValidacaoBate100PorCento()
    {
        var fakeParser = new FakeLayoutParserService { CfopValueNoTxtOriginal = "5102" };
        var controller = CreateController(fakeParser);
        var request = new ReverseReconstructionRequest
        {
            LayoutXml = LayoutXml,
            FieldMappings = OneDirectMapping(),
            TargetXml = TargetXml,
            OriginalTxt = "     5102          "
        };

        var result = await controller.ReverseReconstruct(request);

        var value = OkValue(result);
        var validation = value.Validation;
        Assert.NotNull(validation);
        Assert.Equal(1, validation.MatchedFields);
        Assert.Equal(0, validation.MismatchedFields);
        Assert.Equal(100.0, validation.PercentMatch);
        Assert.True(validation.Fields.Single().Matched);
    }

    [Fact]
    public async Task ComOriginalTxt_DivergenciaParcialViraMismatch()
    {
        // XML tem CFOP=5102, mas o TXT original real tinha outro valor — reconstrução "bate" com o
        // XML, mas diverge do documento de origem de fato (o cenário que a issue #151 item 3 existe
        // para detectar).
        var fakeParser = new FakeLayoutParserService { CfopValueNoTxtOriginal = "6108" };
        var controller = CreateController(fakeParser);
        var request = new ReverseReconstructionRequest
        {
            LayoutXml = LayoutXml,
            FieldMappings = OneDirectMapping(),
            TargetXml = TargetXml,
            OriginalTxt = "     6108          "
        };

        var result = await controller.ReverseReconstruct(request);

        var value = OkValue(result);
        var validation = value.Validation;
        Assert.NotNull(validation);
        Assert.Equal(0, validation.MatchedFields);
        Assert.Equal(1, validation.MismatchedFields);
        Assert.Equal(0.0, validation.PercentMatch);
        Assert.False(validation.Fields.Single().Matched);
        Assert.Equal("6108", validation.Fields.Single().OriginalValue);
    }

    [Fact]
    public async Task OriginalTxtInformadoMasParseFalha_ScopeWarningSemValidation()
    {
        var fakeParser = new FakeLayoutParserService { Falhar = true };
        var controller = CreateController(fakeParser);
        var request = new ReverseReconstructionRequest
        {
            LayoutXml = LayoutXml,
            FieldMappings = OneDirectMapping(),
            TargetXml = TargetXml,
            OriginalTxt = "qualquer coisa"
        };

        var result = await controller.ReverseReconstruct(request);

        var value = OkValue(result);
        Assert.Null(value.Validation);
        Assert.Contains(value.ScopeWarnings, w => w.Contains("parse contra o layout falhou"));
    }

    [Fact]
    public async Task LayoutComWithBreakLinesFalse_ViraWarningNaoErro()
    {
        var layoutMqSeries =
            "<LayoutVO xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
            "<LayoutGuid>LAY_MQ</LayoutGuid><Name>LayoutMq</Name><LimitOfCaracters>20</LimitOfCaracters>" +
            "<WithBreakLines>false</WithBreakLines>" +
            "<Elements><Element xsi:type=\"LineElementVO\"><Name>LINHA001</Name><Sequence>1</Sequence></Element></Elements>" +
            "</LayoutVO>";
        var controller = CreateController();
        var request = new ReverseReconstructionRequest
        {
            LayoutXml = layoutMqSeries,
            FieldMappings = OneDirectMapping(),
            TargetXml = TargetXml
        };

        var result = await controller.ReverseReconstruct(request);

        var value = OkValue(result);
        Assert.True(value.Success);
        Assert.Contains(value.ScopeWarnings, w => w.Contains("WithBreakLines=false"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task LayoutXmlAusente_RetornaBadRequest(string? layoutXml)
    {
        var controller = CreateController();
        var request = new ReverseReconstructionRequest
        {
            LayoutXml = layoutXml,
            FieldMappings = OneDirectMapping(),
            TargetXml = TargetXml
        };

        var result = await controller.ReverseReconstruct(request);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task TargetXmlInvalido_RetornaBadRequest()
    {
        var controller = CreateController();
        var request = new ReverseReconstructionRequest
        {
            LayoutXml = LayoutXml,
            FieldMappings = OneDirectMapping(),
            TargetXml = "<not-well-formed"
        };

        var result = await controller.ReverseReconstruct(request);

        Assert.IsType<BadRequestObjectResult>(result);
    }
}
