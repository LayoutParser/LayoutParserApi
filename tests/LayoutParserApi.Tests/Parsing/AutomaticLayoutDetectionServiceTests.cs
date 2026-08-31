using LayoutParserApi.Models.Database;
using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Parsing;
using LayoutParserApi.Models.Responses;
using LayoutParserApi.Models.Structure;
using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Parsing.Implementations;
using LayoutParserApi.Services.Parsing.Interfaces;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Parsing;

public sealed class AutomaticLayoutDetectionServiceTests
{
    [Fact]
    public async Task DetectAsync_SelectsOnlyCompatibleLayoutAsUnique()
    {
        var compatible = CreateCatalogLayout(Guid.Parse("10000000-0000-0000-0000-000000000001"), "LAY_COMPATIVEL", "000");
        var incompatible = CreateCatalogLayout(Guid.Parse("20000000-0000-0000-0000-000000000002"), "LAY_INCOMPATIVEL", "001");
        var (service, parser) = CreateService([compatible, incompatible], "mqseries");

        var result = await service.DetectAsync(CreateMqDocument("000"));

        Assert.Equal(AutomaticLayoutDetectionStatus.Unique, result.Detection.Status);
        Assert.Equal(1, result.Detection.TotalCandidates);
        Assert.Equal(compatible.Record.LayoutGuid.ToString("D"), result.Detection.SelectedLayout?.LayoutGuid);
        Assert.Single(result.Detection.Candidates);
        Assert.True(result.TryGetRankedLayout(compatible.Record.LayoutGuid.ToString(), out var selected));
        Assert.Same(compatible.Record, selected);
        Assert.Equal(2, parser.ParseCount);
    }

    [Fact]
    public async Task DetectAsync_SelectsSingleIdocLayoutByPhysicalSegmentOrder()
    {
        var compatible = CreateIdocCatalogLayout(
            Guid.Parse("30000000-0000-0000-0000-000000000003"),
            "LAY_IDOC_COMPATIVEL",
            "ZRSDM_NFE_400_IDE");
        var incompatible = CreateIdocCatalogLayout(
            Guid.Parse("40000000-0000-0000-0000-000000000004"),
            "LAY_IDOC_INCOMPATIVEL",
            "ZRSDM_NFE_400_EMIT");
        var (service, _) = CreateService([compatible, incompatible], "idoc");

        var result = await service.DetectAsync("EDI_DC40  CABECALHO\r\nZRSDM_NFE_400_IDE  DETALHE");

        Assert.Equal(AutomaticLayoutDetectionStatus.Unique, result.Detection.Status);
        Assert.Equal("idoc", result.Detection.DetectedType);
        Assert.Equal(compatible.Record.LayoutGuid.ToString("D"), result.Detection.SelectedLayout?.LayoutGuid);
        var candidate = Assert.Single(result.Detection.Candidates);
        Assert.Contains("records_matched:2/2", candidate.Evidence);
        Assert.Contains("marker_order:consistent", candidate.Evidence);
    }

    [Fact]
    public async Task DetectAsync_FailsClosedAndReturnsOnlyTopFiveWhenSeveralLayoutsMatch()
    {
        var layouts = Enumerable.Range(1, 7)
            .Select(index => CreateCatalogLayout(
                Guid.Parse($"00000000-0000-0000-0000-{index:D12}"),
                $"LAY_EQUIVALENTE_{index}",
                "000"))
            .ToList();
        var (service, _) = CreateService(layouts, "mqseries");

        var result = await service.DetectAsync(CreateMqDocument("000"));

        Assert.Equal(AutomaticLayoutDetectionStatus.Ambiguous, result.Detection.Status);
        Assert.Null(result.Detection.SelectedLayout);
        Assert.Equal(7, result.Detection.TotalCandidates);
        Assert.True(result.Detection.Truncated);
        Assert.Equal(5, result.Detection.Candidates.Count);
        Assert.Equal([1, 2, 3, 4, 5], result.Detection.Candidates.Select(candidate => candidate.Rank));
        Assert.All(result.Detection.Candidates, candidate => Assert.True(candidate.IsTied));
        Assert.Equal(
            layouts.Take(5).Select(layout => layout.Record.LayoutGuid.ToString("D")),
            result.Detection.Candidates.Select(candidate => candidate.LayoutGuid));
    }

    [Fact]
    public async Task DetectAsync_ReturnsSeparateSuggestionsWhenNoLayoutPassesHardGates()
    {
        var layouts = Enumerable.Range(1, 6)
            .Select(index => CreateCatalogLayout(Guid.NewGuid(), $"LAY_APROXIMADO_{index}", $"{index:D3}"))
            .ToList();
        var (service, _) = CreateService(layouts, "mqseries");

        var result = await service.DetectAsync(CreateMqDocument("000"));

        Assert.Equal(AutomaticLayoutDetectionStatus.NotFound, result.Detection.Status);
        Assert.Equal(0, result.Detection.TotalCandidates);
        Assert.Empty(result.Detection.Candidates);
        Assert.NotNull(result.Detection.SuggestedCandidates);
        Assert.Equal(5, result.Detection.SuggestedCandidates!.Count);
        Assert.True(result.Detection.Truncated);
        Assert.All(result.Detection.SuggestedCandidates, candidate => Assert.NotEmpty(candidate.Conflicts));
        Assert.Empty(result.RankedLayouts);
    }

    [Fact]
    public async Task DetectAsync_ReusesVersionedCatalogFingerprintCache()
    {
        var layout = CreateCatalogLayout(Guid.NewGuid(), "LAY_CACHE", "000");
        var (service, parser) = CreateService([layout], "mqseries");

        var first = await service.DetectAsync(CreateMqDocument("000"));
        var second = await service.DetectAsync(CreateMqDocument("000"));

        Assert.Equal(first.Detection.CatalogVersion, second.Detection.CatalogVersion);
        Assert.StartsWith("sha256:", first.Detection.CatalogVersion);
        Assert.Equal(1, parser.ParseCount);
    }

    [Fact]
    public async Task DetectAsync_TreatsHierarchicalMarkerOrderAsInformationalForMqSeries()
    {
        var layout = CreateCatalogLayout(Guid.NewGuid(), "LAY_MQ_HIERARQUICO", "000");
        layout.Layout.Elements =
        [
            new LineElement { Name = "CABECALHO", InitialValue = "HEADER", Sequence = 1 },
            new LineElement { Name = "LINHA999999", Sequence = 2 },
            new LineElement { Name = "LINHA000", InitialValue = "000", Sequence = 3 }
        ];
        var (service, _) = CreateService([layout], "mqseries");

        var result = await service.DetectAsync(CreateMqDocument("000"));

        Assert.Equal(AutomaticLayoutDetectionStatus.Unique, result.Detection.Status);
        var candidate = Assert.Single(result.Detection.Candidates);
        Assert.Contains("marker_order:conflict", candidate.Conflicts);
        Assert.Contains("mqseries_marker_order_is_informational", candidate.Limitations);
    }

    [Fact]
    public async Task DetectAsync_DoesNotOfferLayoutsForUnsupportedDocumentType()
    {
        var layout = CreateCatalogLayout(Guid.NewGuid(), "LAY_MQ", "000");
        var (service, parser) = CreateService([layout], "xml");

        var result = await service.DetectAsync("<root />");

        Assert.Equal(AutomaticLayoutDetectionStatus.NotFound, result.Detection.Status);
        Assert.Equal("xml", result.Detection.DetectedType);
        Assert.Empty(result.Detection.Candidates);
        Assert.Empty(result.Detection.SuggestedCandidates!);
        Assert.Equal(1, parser.ParseCount);
    }

    [Fact]
    public async Task DetectAsync_DisablesAuthoritativeSelectionWhenCatalogHitsSafetyLimit()
    {
        var compatible = CreateCatalogLayout(
            Guid.Parse("50000000-0000-0000-0000-000000000005"),
            "LAY_COMPATIVEL_EM_CATALOGO_TRUNCADO",
            "000");
        var remaining = Enumerable.Range(1, AutomaticLayoutDetectionService.MaximumCatalogLayouts - 1)
            .Select(index => CreateCatalogLayout(
                Guid.Parse($"60000000-0000-0000-0000-{index:D12}"),
                $"LAY_INCOMPATIVEL_{index}",
                $"{index + 100:D3}"));
        var (service, _) = CreateService([compatible, .. remaining], "mqseries");

        var result = await service.DetectAsync(CreateMqDocument("000"));

        Assert.Equal(AutomaticLayoutDetectionStatus.NotFound, result.Detection.Status);
        Assert.Null(result.Detection.SelectedLayout);
        Assert.Empty(result.Detection.Candidates);
        var suggestion = Assert.Single(result.Detection.SuggestedCandidates!, candidate => candidate.LayoutGuid == compatible.Record.LayoutGuid.ToString("D"));
        Assert.Contains("catalog_incomplete", suggestion.Conflicts);
        Assert.Contains("authoritative_selection_disabled", suggestion.Limitations);
        Assert.Empty(result.RankedLayouts);
    }

    private static (AutomaticLayoutDetectionService Service, FakeLayoutParserService Parser) CreateService(
        IReadOnlyList<CatalogFixture> layouts,
        string detectedType)
    {
        var parser = new FakeLayoutParserService(layouts.ToDictionary(
            layout => layout.Record.DecryptedContent,
            layout => layout.Layout,
            StringComparer.Ordinal));
        var catalog = new FakeCachedLayoutService(layouts.Select(layout => layout.Record).ToList());
        var service = new AutomaticLayoutDetectionService(
            catalog,
            parser,
            new FakeLayoutDetector(detectedType),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<AutomaticLayoutDetectionService>.Instance);

        return (service, parser);
    }

    private static CatalogFixture CreateCatalogLayout(Guid guid, string name, string documentMarker)
    {
        var contentKey = $"xml-interno:{guid:D}";
        var layout = new Layout
        {
            LayoutGuid = guid.ToString("D"),
            Name = name,
            LimitOfCaracters = 20,
            WithBreakLines = false,
            Elements =
            [
                new LineElement { Name = "CABECALHO", InitialValue = "HEADER", Sequence = 1 },
                new LineElement { Name = "LINHA000", InitialValue = documentMarker, Sequence = 2 },
                new LineElement { Name = "LINHA999999", Sequence = 3 }
            ]
        };
        var record = new LayoutRecord
        {
            Id = guid.GetHashCode(),
            LayoutGuid = guid,
            Name = name,
            DecryptedContent = contentKey,
            LastUpdateDate = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc)
        };

        return new CatalogFixture(record, layout);
    }

    private static CatalogFixture CreateIdocCatalogLayout(Guid guid, string name, string detailMarker)
    {
        var contentKey = $"xml-interno:{guid:D}";
        var layout = new Layout
        {
            LayoutGuid = guid.ToString("D"),
            Name = name,
            WithBreakLines = true,
            Elements =
            [
                new LineElement { Name = "EDI_DC40", InitialValue = "EDI_DC40", Sequence = 1 },
                new LineElement { Name = detailMarker, InitialValue = detailMarker, Sequence = 2 }
            ]
        };
        var record = new LayoutRecord
        {
            Id = guid.GetHashCode(),
            LayoutGuid = guid,
            Name = name,
            DecryptedContent = contentKey,
            LastUpdateDate = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc)
        };

        return new CatalogFixture(record, layout);
    }

    private static string CreateMqDocument(string marker) => string.Concat(
        "HEADER".PadRight(20),
        $"000001{marker}".PadRight(20),
        "999999".PadRight(20));

    private sealed record CatalogFixture(LayoutRecord Record, Layout Layout);

    private sealed class FakeLayoutDetector(string detectedType) : ILayoutDetector
    {
        public string DetectType(string content) => detectedType;
    }

    private sealed class FakeLayoutParserService(IReadOnlyDictionary<string, Layout> layouts) : ILayoutParserService
    {
        public int ParseCount { get; private set; }

        public Task<Layout?> ParseLayoutFromXmlAsync(string xmlContent)
        {
            ParseCount++;
            return Task.FromResult(layouts.TryGetValue(xmlContent, out var layout) ? layout : null);
        }

        public Task<ParsingResult> ParseAsync(Stream layoutStream, Stream txtStream) => throw new NotSupportedException();
        public Layout ReestruturarLayout(Layout layoutOriginal) => layoutOriginal;
        public Layout ReordenarSequences(Layout layout) => layout;
        public DocumentStructure BuildDocumentStructure(ParsingResult result) => new();
        public List<LineValidationInfo> CalculateLineValidations(Layout layout, int expectedLineLength) => [];
    }

    private sealed class FakeCachedLayoutService(IReadOnlyList<LayoutRecord> layouts) : ICachedLayoutService
    {
        public Task<LayoutSearchResponse> SearchLayoutsAsync(LayoutSearchRequest request) => Task.FromResult(new LayoutSearchResponse
        {
            Success = true,
            Layouts = layouts.ToList(),
            TotalFound = layouts.Count
        });

        public Task<LayoutRecord?> GetLayoutByIdAsync(int id) => throw new NotSupportedException();
        public Task<LayoutRecord?> GetLayoutByGuidAsync(string layoutGuid) => throw new NotSupportedException();
        public Task RefreshCacheFromDatabaseAsync() => throw new NotSupportedException();
        public Task ClearCacheAsync() => throw new NotSupportedException();
        public ILayoutDatabaseService GetLayoutDatabaseService() => throw new NotSupportedException();
    }
}
