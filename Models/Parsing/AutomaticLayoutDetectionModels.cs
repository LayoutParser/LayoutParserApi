using LayoutParserApi.Models.Database;

using System.Text.Json.Serialization;

namespace LayoutParserApi.Models.Parsing
{
    public static class AutomaticLayoutDetectionStatus
    {
        public const string Unique = "unique";
        public const string Ambiguous = "ambiguous";
        public const string NotFound = "not_found";
    }

    /// <summary>
    /// Candidato de layout ordenado por equivalência estrutural. O score é um índice
    /// determinístico de equivalência, não uma probabilidade e não autoriza unicidade.
    /// </summary>
    public sealed class AutomaticLayoutCandidate
    {
        public int Rank { get; set; }
        public string LayoutGuid { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public int MatchScore { get; init; }
        public bool IsTied { get; set; }
        public List<string> Evidence { get; init; } = [];
        public List<string> Conflicts { get; init; } = [];
        public List<string> Limitations { get; init; } = [];
    }

    public sealed class AutomaticLayoutDetection
    {
        public string Status { get; init; } = AutomaticLayoutDetectionStatus.NotFound;
        public string DetectedType { get; init; } = "unknown";
        public string AlgorithmVersion { get; init; } = string.Empty;
        public string CatalogVersion { get; init; } = string.Empty;
        public int TotalCandidates { get; init; }
        public bool Truncated { get; init; }
        public AutomaticLayoutCandidate? SelectedLayout { get; set; }
        public List<AutomaticLayoutCandidate> Candidates { get; init; } = [];
        public List<AutomaticLayoutCandidate>? SuggestedCandidates { get; init; }
    }

    /// <summary>
    /// Resultado do probe. O índice interno nunca é serializado: ele mantém o XML do layout
    /// exclusivamente dentro da API e permite validar o override contra o ranking recém-calculado.
    /// </summary>
    public sealed class AutomaticLayoutDetectionResult
    {
        public required AutomaticLayoutDetection Detection { get; init; }

        [JsonIgnore]
        public IReadOnlyDictionary<string, LayoutRecord> RankedLayouts { get; init; }
            = new Dictionary<string, LayoutRecord>(StringComparer.OrdinalIgnoreCase);

        public bool TryGetRankedLayout(string layoutGuid, out LayoutRecord? layout)
        {
            layout = null;
            if (!TryNormalizeGuid(layoutGuid, out var normalized))
                return false;

            return RankedLayouts.TryGetValue(normalized, out layout);
        }

        public static bool TryNormalizeGuid(string? value, out string normalized)
        {
            normalized = string.Empty;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var candidate = value.Trim();
            if (candidate.StartsWith("LAY_", StringComparison.OrdinalIgnoreCase))
                candidate = candidate[4..];

            if (!Guid.TryParse(candidate, out var guid) || guid == Guid.Empty)
                return false;

            normalized = guid.ToString("D");
            return true;
        }
    }

    public sealed class AutomaticParseResponse
    {
        public bool Success { get; init; }
        public string CorrelationId { get; init; } = string.Empty;
        public required AutomaticLayoutDetection Detection { get; init; }
        public object? ParseResult { get; init; }
        public string? Message { get; init; }
    }
}
