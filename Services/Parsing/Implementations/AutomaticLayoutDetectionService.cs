using LayoutParserApi.Models.Configuration;
using LayoutParserApi.Models.Database;
using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Parsing;
using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Parsing.Interfaces;

using Microsoft.Extensions.Caching.Memory;

using Newtonsoft.Json;

using System.Security.Cryptography;
using System.Text;

namespace LayoutParserApi.Services.Parsing.Implementations
{
    /// <summary>
    /// Probe estrutural puro: consulta o catálogo, mas não executa parse de documento,
    /// transformação, aprendizado ou persistência.
    /// </summary>
    public sealed class AutomaticLayoutDetectionService : IAutomaticLayoutDetectionService
    {
        public const string CurrentAlgorithmVersion = "layout-probe-v1";
        public const int MaximumRankedCandidates = 5;
        public const int MaximumCatalogLayouts = 200;

        private const string CatalogCachePrefix = "automatic-layout-catalog:";
        private static readonly SemaphoreSlim CatalogBuildLock = new(1, 1);

        private readonly ICachedLayoutService _catalogService;
        private readonly ILayoutParserService _parserService;
        private readonly ILayoutDetector _layoutDetector;
        private readonly IMemoryCache _memoryCache;
        private readonly ILogger<AutomaticLayoutDetectionService> _logger;

        public AutomaticLayoutDetectionService(
            ICachedLayoutService catalogService,
            ILayoutParserService parserService,
            ILayoutDetector layoutDetector,
            IMemoryCache memoryCache,
            ILogger<AutomaticLayoutDetectionService> logger)
        {
            _catalogService = catalogService;
            _parserService = parserService;
            _layoutDetector = layoutDetector;
            _memoryCache = memoryCache;
            _logger = logger;
        }

        public async Task<AutomaticLayoutDetectionResult> DetectAsync(
            string documentContent,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(documentContent);
            cancellationToken.ThrowIfCancellationRequested();

            var catalog = await GetCatalogAsync(cancellationToken);
            var detectedType = _layoutDetector.DetectType(documentContent).ToLowerInvariant();

            if (detectedType is not ("mqseries" or "idoc"))
            {
                return new AutomaticLayoutDetectionResult
                {
                    Detection = new AutomaticLayoutDetection
                    {
                        Status = AutomaticLayoutDetectionStatus.NotFound,
                        DetectedType = detectedType,
                        AlgorithmVersion = CurrentAlgorithmVersion,
                        CatalogVersion = catalog.Version,
                        TotalCandidates = 0,
                        Truncated = false,
                        Candidates = [],
                        SuggestedCandidates = []
                    }
                };
            }

            var probes = catalog.Layouts
                .Where(layout => string.Equals(layout.Fingerprint.Family, detectedType, StringComparison.Ordinal))
                .Select(layout =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return Probe(documentContent, detectedType, layout);
                })
                .OrderByDescending(probe => probe.Candidate.MatchScore)
                .ThenBy(probe => probe.Candidate.LayoutGuid, StringComparer.Ordinal)
                .ToList();

            ApplyRanksAndTies(probes);

            var compatible = probes.Where(probe => probe.IsCompatible).ToList();
            var rankedCompatible = compatible.Take(MaximumRankedCandidates).ToList();
            var rankedLayouts = rankedCompatible.ToDictionary(
                probe => probe.Candidate.LayoutGuid,
                probe => probe.Layout.Record,
                StringComparer.OrdinalIgnoreCase);

            if (compatible.Count == 1)
            {
                var selected = compatible[0].Candidate;
                return new AutomaticLayoutDetectionResult
                {
                    Detection = new AutomaticLayoutDetection
                    {
                        Status = AutomaticLayoutDetectionStatus.Unique,
                        DetectedType = detectedType,
                        AlgorithmVersion = CurrentAlgorithmVersion,
                        CatalogVersion = catalog.Version,
                        TotalCandidates = 1,
                        Truncated = false,
                        SelectedLayout = selected,
                        Candidates = [selected]
                    },
                    RankedLayouts = rankedLayouts
                };
            }

            if (compatible.Count > 1)
            {
                return new AutomaticLayoutDetectionResult
                {
                    Detection = new AutomaticLayoutDetection
                    {
                        Status = AutomaticLayoutDetectionStatus.Ambiguous,
                        DetectedType = detectedType,
                        AlgorithmVersion = CurrentAlgorithmVersion,
                        CatalogVersion = catalog.Version,
                        TotalCandidates = compatible.Count,
                        Truncated = compatible.Count > MaximumRankedCandidates,
                        Candidates = rankedCompatible.Select(probe => probe.Candidate).ToList()
                    },
                    RankedLayouts = rankedLayouts
                };
            }

            var suggestions = probes.Take(MaximumRankedCandidates).ToList();
            return new AutomaticLayoutDetectionResult
            {
                Detection = new AutomaticLayoutDetection
                {
                    Status = AutomaticLayoutDetectionStatus.NotFound,
                    DetectedType = detectedType,
                    AlgorithmVersion = CurrentAlgorithmVersion,
                    CatalogVersion = catalog.Version,
                    TotalCandidates = 0,
                    Truncated = probes.Count > MaximumRankedCandidates,
                    Candidates = [],
                    SuggestedCandidates = suggestions.Select(probe => probe.Candidate).ToList()
                }
            };
        }

        private async Task<CatalogSnapshot> GetCatalogAsync(CancellationToken cancellationToken)
        {
            var response = await _catalogService.SearchLayoutsAsync(new LayoutSearchRequest
            {
                SearchTerm = string.Empty,
                MaxResults = MaximumCatalogLayouts
            });

            if (!response.Success)
                throw new InvalidOperationException("O catálogo de layouts está indisponível para detecção automática.");

            var usableRecords = response.Layouts
                .Where(layout => !string.IsNullOrWhiteSpace(layout.DecryptedContent))
                .OrderBy(layout => ResolveLayoutGuid(layout), StringComparer.Ordinal)
                .Take(MaximumCatalogLayouts)
                .ToList();

            var version = ComputeCatalogVersion(usableRecords);
            var cacheKey = CatalogCachePrefix + version;

            if (_memoryCache.TryGetValue(cacheKey, out CatalogSnapshot? cached) && cached is not null)
                return cached;

            await CatalogBuildLock.WaitAsync(cancellationToken);
            try
            {
                if (_memoryCache.TryGetValue(cacheKey, out cached) && cached is not null)
                    return cached;

                var parsedLayouts = new List<CatalogLayout>();
                foreach (var record in usableRecords)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var parsed = await _parserService.ParseLayoutFromXmlAsync(record.DecryptedContent);
                    if (parsed is null)
                    {
                        _logger.LogWarning(
                            "Layout {LayoutGuid} foi excluído da detecção automática porque o XML interno é inválido.",
                            ResolveLayoutGuid(record));
                        continue;
                    }

                    var guid = ResolveLayoutGuid(record, parsed);
                    if (string.IsNullOrWhiteSpace(guid))
                    {
                        _logger.LogWarning("Layout {LayoutId} foi excluído da detecção automática porque não possui GUID válido.", record.Id);
                        continue;
                    }

                    parsedLayouts.Add(new CatalogLayout(record, parsed, BuildFingerprint(record, parsed, guid)));
                }

                var snapshot = new CatalogSnapshot(version, parsedLayouts);
                _memoryCache.Set(cacheKey, snapshot, TimeSpan.FromMinutes(30));
                LogFingerprintCollisions(snapshot);
                return snapshot;
            }
            finally
            {
                CatalogBuildLock.Release();
            }
        }

        private ProbeResult Probe(string documentContent, string detectedType, CatalogLayout catalogLayout)
        {
            var fingerprint = catalogLayout.Fingerprint;
            var evidence = new List<string> { $"family:{detectedType}" };
            var conflicts = new List<string>();
            var limitations = new List<string>
            {
                "minimal_occurrence_excluded_from_authoritative_gate",
                "cardinality_requires_validated_catalog_metadata"
            };

            if (!fingerprint.FamilyFromMetadata)
                limitations.Add("layout_family_inferred_from_markers");

            var familyMatches = string.Equals(fingerprint.Family, detectedType, StringComparison.Ordinal);
            if (!familyMatches)
                conflicts.Add($"family_mismatch:{fingerprint.Family}");

            IReadOnlyList<string> records;
            var widthMatches = true;
            var widthScore = 0;

            if (detectedType == "mqseries")
            {
                var clean = documentContent.Replace("\r", string.Empty).Replace("\n", string.Empty);
                var width = fingerprint.LineLength ?? LineLengthResolver.LegacyDefaultLineLength;

                if (fingerprint.LineLength.HasValue)
                {
                    widthMatches = clean.Length > 0 && clean.Length % width == 0;
                    if (widthMatches)
                    {
                        evidence.Add($"record_width:{width}");
                        widthScore = 15;
                    }
                    else
                        conflicts.Add($"record_width_mismatch:{width}");
                }
                else
                {
                    limitations.Add($"record_width_fallback:{LineLengthResolver.LegacyDefaultLineLength}");
                    widthScore = 8;
                }

                records = SplitFixedWidth(clean, width);
            }
            else
            {
                records = documentContent
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                evidence.Add("record_boundary:physical_line");
                widthScore = 15;
            }

            var matchedMarkers = new List<LayoutMarker>();
            var unmatchedRecords = 0;
            foreach (var record in records)
            {
                var marker = FindMatchingMarker(record, detectedType, fingerprint.Markers);
                if (marker is null)
                    unmatchedRecords++;
                else
                    matchedMarkers.Add(marker);
            }

            var matchedCount = records.Count - unmatchedRecords;
            var recordCoverage = records.Count == 0 ? 0d : (double)matchedCount / records.Count;
            evidence.Add($"records_matched:{matchedCount}/{records.Count}");

            if (unmatchedRecords > 0)
                conflicts.Add($"records_unmatched:{unmatchedRecords}/{records.Count}");

            if (fingerprint.Markers.Count == 0)
                conflicts.Add("layout_without_explicit_markers");

            var distinctMatched = matchedMarkers
                .Select(marker => marker.Token)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var distinctDeclared = fingerprint.Markers
                .Select(marker => marker.Token)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var declaredCoverage = distinctDeclared == 0 ? 0d : (double)distinctMatched / distinctDeclared;
            evidence.Add($"declared_markers_observed:{distinctMatched}/{distinctDeclared}");

            var orderMatches = IsFirstOccurrenceOrderCompatible(matchedMarkers);
            if (orderMatches)
                evidence.Add("marker_order:consistent");
            else
                conflicts.Add("marker_order:conflict");

            var score = (familyMatches ? 20 : 0)
                + widthScore
                + (int)Math.Round(recordCoverage * 50, MidpointRounding.AwayFromZero)
                + (int)Math.Round(declaredCoverage * 10, MidpointRounding.AwayFromZero)
                + (orderMatches ? 5 : 0);

            score = Math.Clamp(score, 0, 100);
            var compatible = familyMatches
                && widthMatches
                && records.Count > 0
                && fingerprint.Markers.Count > 0
                && unmatchedRecords == 0
                && orderMatches;

            return new ProbeResult(
                catalogLayout,
                compatible,
                new AutomaticLayoutCandidate
                {
                    LayoutGuid = fingerprint.LayoutGuid,
                    Name = catalogLayout.Record.Name,
                    MatchScore = score,
                    Evidence = evidence,
                    Conflicts = conflicts,
                    Limitations = limitations.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()
                });
        }

        private static LayoutFingerprint BuildFingerprint(LayoutRecord record, Layout layout, string guid)
        {
            var markers = new List<LayoutMarker>();
            CollectMarkers(layout.Elements, markers);

            var familyFromMetadata = layout.WithBreakLines.HasValue;
            var family = layout.WithBreakLines switch
            {
                true => "idoc",
                false => "mqseries",
                null when markers.Any(marker => IsIdocMarker(marker.Token)) => "idoc",
                _ => "mqseries"
            };

            var lineLength = LineLengthResolver.Resolve(layout.LimitOfCaracters, layout.LayoutGuid);
            if (!lineLength.HasValue)
                lineLength = LayoutLineSizeConfiguration.GetLineSizeForLayout(guid);

            var material = string.Join('|',
                family,
                lineLength?.ToString() ?? "unknown",
                string.Join('>', markers.Select(marker => marker.Token.ToUpperInvariant())));
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();

            return new LayoutFingerprint(guid, family, familyFromMetadata, lineLength, markers, hash);
        }

        private static void CollectMarkers(IEnumerable<LineElement>? lines, List<LayoutMarker> markers)
        {
            if (lines is null)
                return;

            foreach (var line in lines)
            {
                var token = line.Name?.Equals("LINHA999999", StringComparison.OrdinalIgnoreCase) == true
                    ? "999999"
                    : line.InitialValue?.Trim();

                if (!string.IsNullOrWhiteSpace(token))
                    markers.Add(new LayoutMarker(token, markers.Count));

                foreach (var serializedElement in line.Elements ?? [])
                {
                    try
                    {
                        var child = JsonConvert.DeserializeObject<LineElement>(serializedElement);
                        if (child is not null && string.Equals(child.Type, "LineElementVO", StringComparison.OrdinalIgnoreCase))
                            CollectMarkers([child], markers);
                    }
                    catch (JsonException)
                    {
                        // FieldElement e fragmento legado não são marcadores de registro.
                    }
                }
            }
        }

        private static LayoutMarker? FindMatchingMarker(
            string record,
            string detectedType,
            IReadOnlyList<LayoutMarker> markers)
        {
            if (detectedType == "idoc")
            {
                var actual = NormalizeIdocMarker(record);
                return markers.FirstOrDefault(marker =>
                    string.Equals(NormalizeIdocMarker(marker.Token), actual, StringComparison.OrdinalIgnoreCase));
            }

            foreach (var marker in markers)
            {
                if (marker.Token.Equals("HEADER", StringComparison.OrdinalIgnoreCase)
                    && record.StartsWith(marker.Token, StringComparison.OrdinalIgnoreCase))
                    return marker;

                if (marker.Token == "999999" && record.StartsWith("999999", StringComparison.Ordinal))
                    return marker;

                if (record.Length >= 6 + marker.Token.Length
                    && record.AsSpan(0, 6).ToString().All(char.IsDigit)
                    && record.AsSpan(6, marker.Token.Length).Equals(marker.Token, StringComparison.OrdinalIgnoreCase))
                    return marker;
            }

            return null;
        }

        private static string NormalizeIdocMarker(string value)
        {
            var token = value.TrimStart();
            var separator = token.IndexOfAny([' ', '\t']);
            if (separator >= 0)
                token = token[..separator];

            if (!token.StartsWith("ZRSDM_NFE_400_", StringComparison.OrdinalIgnoreCase))
                return token;

            var prefix = "ZRSDM_NFE_400_";
            var suffix = token[prefix.Length..].TrimEnd('0');
            return prefix + (suffix.Length == 0 ? token[prefix.Length..] : suffix);
        }

        private static bool IsIdocMarker(string marker)
        {
            return marker.StartsWith("EDI_", StringComparison.OrdinalIgnoreCase)
                || marker.StartsWith("ZRSDM_", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFirstOccurrenceOrderCompatible(IReadOnlyList<LayoutMarker> matchedMarkers)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var previousOrder = -1;

            foreach (var marker in matchedMarkers)
            {
                if (!seen.Add(marker.Token))
                    continue;

                if (marker.Order < previousOrder)
                    return false;

                previousOrder = marker.Order;
            }

            return true;
        }

        private static List<string> SplitFixedWidth(string content, int width)
        {
            if (string.IsNullOrEmpty(content) || width <= 0)
                return [];

            var records = new List<string>((content.Length + width - 1) / width);
            for (var offset = 0; offset < content.Length; offset += width)
                records.Add(content.Substring(offset, Math.Min(width, content.Length - offset)));

            return records;
        }

        private static void ApplyRanksAndTies(List<ProbeResult> probes)
        {
            for (var index = 0; index < probes.Count; index++)
            {
                var candidate = probes[index].Candidate;
                candidate.Rank = index + 1;
                candidate.IsTied = probes.Count(probe => probe.Candidate.MatchScore == candidate.MatchScore) > 1;
            }
        }

        private static string ComputeCatalogVersion(IReadOnlyList<LayoutRecord> layouts)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var layout in layouts)
            {
                var contentHash = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(layout.DecryptedContent ?? string.Empty)));
                var material = $"{ResolveLayoutGuid(layout)}|{layout.LastUpdateDate.ToUniversalTime().Ticks}|{contentHash}\n";
                hash.AppendData(Encoding.UTF8.GetBytes(material));
            }

            return "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        private static string ResolveLayoutGuid(LayoutRecord record, Layout? parsed = null)
        {
            if (record.LayoutGuid != Guid.Empty)
                return record.LayoutGuid.ToString("D");

            return AutomaticLayoutDetectionResult.TryNormalizeGuid(parsed?.LayoutGuid, out var normalized)
                ? normalized
                : string.Empty;
        }

        private void LogFingerprintCollisions(CatalogSnapshot snapshot)
        {
            var collisions = snapshot.Layouts
                .GroupBy(layout => layout.Fingerprint.Hash, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Count())
                .OrderDescending()
                .ToList();

            if (collisions.Count == 0)
            {
                _logger.LogInformation(
                    "Catálogo de detecção {CatalogVersion} carregado com {LayoutCount} layouts e sem colisões integrais de fingerprint.",
                    snapshot.Version,
                    snapshot.Layouts.Count);
                return;
            }

            _logger.LogWarning(
                "Catálogo de detecção {CatalogVersion} carregado com {LayoutCount} layouts e {CollisionGroupCount} grupos de colisão; tamanhos={CollisionGroupSizes}.",
                snapshot.Version,
                snapshot.Layouts.Count,
                collisions.Count,
                string.Join(',', collisions));
        }

        private sealed record CatalogSnapshot(string Version, IReadOnlyList<CatalogLayout> Layouts);
        private sealed record CatalogLayout(LayoutRecord Record, Layout Layout, LayoutFingerprint Fingerprint);
        private sealed record LayoutFingerprint(
            string LayoutGuid,
            string Family,
            bool FamilyFromMetadata,
            int? LineLength,
            IReadOnlyList<LayoutMarker> Markers,
            string Hash);
        private sealed record LayoutMarker(string Token, int Order);
        private sealed record ProbeResult(CatalogLayout Layout, bool IsCompatible, AutomaticLayoutCandidate Candidate);
    }
}
