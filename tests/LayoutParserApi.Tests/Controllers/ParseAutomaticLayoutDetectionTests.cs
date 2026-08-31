using System.Text;

using LayoutParserApi.Controllers;
using LayoutParserApi.Models.Database;
using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Parsing;
using LayoutParserApi.Models.Responses;
using LayoutParserApi.Models.Structure;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Learning;
using LayoutParserApi.Services.Parsing.Interfaces;
using LayoutParserApi.Services.Transformation.LowCode;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Tests.Controllers;

public sealed class ParseAutomaticLayoutDetectionTests
{
    private static readonly Guid FirstLayoutGuid = Guid.Parse("ad4fb6f4-9ff5-44fd-988b-3da5ed56b22c");
    private static readonly Guid SecondLayoutGuid = Guid.Parse("bd4fb6f4-9ff5-44fd-988b-3da5ed56b22c");

    [Fact]
    public async Task Auto_ReturnsRankedCandidatesWithoutParsingWhenDetectionIsAmbiguous()
    {
        var detectionResult = CreateAmbiguousDetection();
        var (controller, parser) = CreateController();

        var action = await controller.Auto(
            File("documento.mq_series", "HEADER".PadRight(600)),
            layoutGuidOverride: null,
            new FakeAutomaticLayoutDetectionService(detectionResult),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var response = Assert.IsType<AutomaticParseResponse>(ok.Value);
        Assert.True(response.Success);
        Assert.Equal(AutomaticLayoutDetectionStatus.Ambiguous, response.Detection.Status);
        Assert.Null(response.Detection.SelectedLayout);
        Assert.Null(response.ParseResult);
        Assert.Equal(0, parser.ParseCount);
        Assert.False(string.IsNullOrWhiteSpace(response.CorrelationId));
        Assert.Equal(response.CorrelationId, controller.Response.Headers["X-Correlation-ID"]);
    }

    [Fact]
    public async Task Auto_RejectsOverrideThatWasNotReturnedByCurrentDetection()
    {
        var detectionResult = CreateAmbiguousDetection();
        var (controller, parser) = CreateController();

        var action = await controller.Auto(
            File("documento.mq_series", "HEADER".PadRight(600)),
            Guid.NewGuid().ToString("D"),
            new FakeAutomaticLayoutDetectionService(detectionResult),
            CancellationToken.None);

        var error = Assert.IsType<UnprocessableEntityObjectResult>(action);
        var response = Assert.IsType<AutomaticParseResponse>(error.Value);
        Assert.False(response.Success);
        Assert.Contains("não pertence", response.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, parser.ParseCount);
    }

    [Fact]
    public async Task Auto_RevalidatesRankedOverrideAndReusesProtectedUploadPipeline()
    {
        var detectionResult = CreateAmbiguousDetection();
        var (controller, parser) = CreateController();

        var action = await controller.Auto(
            File("documento.mq_series", "HEADER".PadRight(600)),
            FirstLayoutGuid.ToString("D"),
            new FakeAutomaticLayoutDetectionService(detectionResult),
            CancellationToken.None);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(action);
        Assert.Equal(StatusCodes.Status200OK, objectResult.StatusCode);
        var response = Assert.IsType<AutomaticParseResponse>(objectResult.Value);
        Assert.True(response.Success);
        Assert.NotNull(response.ParseResult);
        Assert.Equal(FirstLayoutGuid.ToString("D"), response.Detection.SelectedLayout?.LayoutGuid);
        Assert.Equal(1, parser.ParseCount);
    }

    [Fact]
    public async Task Auto_RequiresNonEmptyDocument()
    {
        var detectionResult = CreateAmbiguousDetection();
        var (controller, parser) = CreateController();

        var action = await controller.Auto(
            null,
            null,
            new FakeAutomaticLayoutDetectionService(detectionResult),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(action);
        Assert.Equal(0, parser.ParseCount);
    }

    private static AutomaticLayoutDetectionResult CreateAmbiguousDetection()
    {
        var first = Candidate(1, FirstLayoutGuid, "LAY_TXT_MQSERIES_ENVNFE_4.00_NFe");
        var second = Candidate(2, SecondLayoutGuid, "LAY_FIAT_TXT_MQSERIES_ENVNFE_4.00_NFe");
        return new AutomaticLayoutDetectionResult
        {
            Detection = new AutomaticLayoutDetection
            {
                Status = AutomaticLayoutDetectionStatus.Ambiguous,
                DetectedType = "mqseries",
                AlgorithmVersion = "layout-probe-v1",
                CatalogVersion = "sha256:test",
                TotalCandidates = 2,
                Candidates = [first, second]
            },
            RankedLayouts = new Dictionary<string, LayoutRecord>(StringComparer.OrdinalIgnoreCase)
            {
                [FirstLayoutGuid.ToString("D")] = Record(FirstLayoutGuid, first.Name),
                [SecondLayoutGuid.ToString("D")] = Record(SecondLayoutGuid, second.Name)
            }
        };
    }

    private static AutomaticLayoutCandidate Candidate(int rank, Guid guid, string name) => new()
    {
        Rank = rank,
        LayoutGuid = guid.ToString("D"),
        Name = name,
        MatchScore = 100,
        IsTied = true,
        Evidence = ["records_matched:59/59"],
        Limitations = ["score_is_not_probability"]
    };

    private static LayoutRecord Record(Guid guid, string name) => new()
    {
        LayoutGuid = guid,
        Name = name,
        DecryptedContent = "<LayoutVO />",
        LastUpdateDate = DateTime.UtcNow
    };

    private static (ParseController Controller, FakeLayoutParserService Parser) CreateController()
    {
        var root = Path.Combine(Path.GetTempPath(), "lp-tests", "automatic-layout", Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TransformationPipeline:ExamplesPath"] = Path.Combine(root, "Examples"),
                ["ML:LowCodeTransformationsPath"] = Path.Combine(root, "LowCode")
            })
            .Build();
        var options = Options.Create(new LowCodeRunnerOptions { SyncDeliveryTimeoutSeconds = 1 });
        var store = new LowCodeTransformationStore(
            NullLogger<LowCodeTransformationStore>.Instance,
            config,
            options,
            redis: null);
        var lowCodeAuto = new LowCodeAutoTransformationService(
            NullLogger<LowCodeAutoTransformationService>.Instance,
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new LowCodeTransformationService(NullLogger<LowCodeTransformationService>.Instance, options, config),
            store,
            options);
        var parser = new FakeLayoutParserService();
        var controller = new ParseController(
            parser,
            NullLogger<ParseController>.Instance,
            new FakeLayoutDetector(),
            new FileStorageService(config, NullLogger<FileStorageService>.Instance),
            new LayoutLearningService(NullLogger<LayoutLearningService>.Instance),
            config,
            lowCodeAuto,
            options,
            store)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        return (controller, parser);
    }

    private static IFormFile File(string name, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "documentFile", name);
    }

    private sealed class FakeAutomaticLayoutDetectionService(AutomaticLayoutDetectionResult result)
        : IAutomaticLayoutDetectionService
    {
        public Task<AutomaticLayoutDetectionResult> DetectAsync(
            string documentContent,
            CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private sealed class FakeLayoutDetector : ILayoutDetector
    {
        public string DetectType(string content) => "mqseries";
    }

    private sealed class FakeLayoutParserService : ILayoutParserService
    {
        public int ParseCount { get; private set; }

        public Task<ParsingResult> ParseAsync(Stream layoutStream, Stream txtStream)
        {
            ParseCount++;
            return Task.FromResult(new ParsingResult
            {
                Success = true,
                Layout = new Layout
                {
                    LayoutGuid = FirstLayoutGuid.ToString("D"),
                    Name = "LAY_TXT_MQSERIES_ENVNFE_4.00_NFe",
                    LayoutType = "TextPositional",
                    LimitOfCaracters = 600,
                    WithBreakLines = false
                },
                RawText = "HEADER".PadRight(600),
                ParsedFields = [],
                ValidationErrors = []
            });
        }

        public Layout ReestruturarLayout(Layout layoutOriginal) => layoutOriginal;
        public Layout ReordenarSequences(Layout layout) => layout;
        public DocumentStructure BuildDocumentStructure(ParsingResult result) => new();
        public List<LineValidationInfo> CalculateLineValidations(Layout layout, int expectedLineLength) => [];
        public Task<Layout?> ParseLayoutFromXmlAsync(string xmlContent) => Task.FromResult<Layout?>(null);
    }
}
