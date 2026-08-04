using LayoutParserApi.Models.Configuration;
using LayoutParserApi.Models.Entities;

namespace LayoutParserApi.Services.Transformation.LowCode
{
    /// <summary>
    /// Metadados físicos do input persistidos no dataset low-code v2. É opcional na assinatura
    /// do serviço para manter compatibilidade; ausência vira fallback explícito e suspeito.
    /// </summary>
    public sealed record LowCodePositionalMetadata(
        string PositionalFormat,
        string PositionalFormatSource,
        bool? WithBreakLines,
        string LayoutType,
        bool Suspect,
        string? SuspectReason)
    {
        public const int DatasetSchemaVersion = 2;
        public const string InferredFormatSuspectReason = "positional-format-not-declared-by-layout";

        public static LowCodePositionalMetadata Resolve(Layout? layout, string? rawText, int lineLength)
        {
            var resolution = PositionalFormatResolver.Resolve(layout, rawText, lineLength);
            return FromResolution(resolution, layout?.WithBreakLines, layout?.LayoutType);
        }

        public static LowCodePositionalMetadata CreateDefault(string? layoutType = null)
            => new(
                LayoutParserApi.Models.Enums.PositionalFormat.ContinuousStream.ToString(),
                "default",
                null,
                NormalizeLayoutType(layoutType),
                true,
                InferredFormatSuspectReason);

        private static LowCodePositionalMetadata FromResolution(
            PositionalFormatResolution resolution,
            bool? withBreakLines,
            string? layoutType)
        {
            var source = resolution.Source switch
            {
                LayoutParserApi.Models.Enums.PositionalFormatSource.LayoutMetadata => "layout",
                LayoutParserApi.Models.Enums.PositionalFormatSource.LegacyContentHeuristic => "heuristic",
                _ => "default"
            };
            var suspect = source != "layout";

            return new LowCodePositionalMetadata(
                resolution.Format.ToString(),
                source,
                withBreakLines,
                NormalizeLayoutType(layoutType),
                suspect,
                suspect ? InferredFormatSuspectReason : null);
        }

        private static string NormalizeLayoutType(string? layoutType)
            => string.IsNullOrWhiteSpace(layoutType) ? "unknown" : layoutType.Trim();
    }

    /// <summary>
    /// Injeta o rótulo v2 no objeto raiz do meta sem alterar os campos legados. Usado igualmente
    /// pelos caminhos single e multi-candidato.
    /// </summary>
    public static class LowCodeDatasetMetaBuilder
    {
        public static Dictionary<string, object?> AddPositionalMetadata(
            IReadOnlyDictionary<string, object?> legacyFields,
            LowCodePositionalMetadata? positionalMetadata)
        {
            var metadata = positionalMetadata ?? LowCodePositionalMetadata.CreateDefault();
            var result = legacyFields.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);
            result.Add("datasetSchemaVersion", LowCodePositionalMetadata.DatasetSchemaVersion);
            result.Add("positionalFormat", metadata.PositionalFormat);
            result.Add("positionalFormatSource", metadata.PositionalFormatSource);
            result.Add("withBreakLines", metadata.WithBreakLines);
            result.Add("layoutType", metadata.LayoutType);
            result.Add("suspect", metadata.Suspect);
            result.Add("suspectReason", metadata.SuspectReason);

            return result;
        }
    }
}
