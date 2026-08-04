using System.Text.Json;

using LayoutParserApi.Models.Entities;
using LayoutParserApi.Services.Transformation.LowCode;

namespace LayoutParserApi.Tests.Transformation
{
    public class LowCodePositionalMetadataTests
    {
        [Fact]
        public void Layout_com_quebra_explicita_produz_rotulo_record_per_line_confiavel()
        {
            var layout = new Layout
            {
                Name = "LAYOUT_SINTETICO",
                LayoutGuid = "LAY_4f7c625a-a089-4b42-a874-48da13b0d88b",
                LayoutType = "TextPositional",
                WithBreakLines = true
            };

            var metadata = LowCodePositionalMetadata.Resolve(
                layout,
                "EDI_DC40 SYNTHETIC\nZRSDM_NFE_ITEM SYNTHETIC",
                lineLength: 600);

            Assert.Equal("RecordPerLine", metadata.PositionalFormat);
            Assert.Equal("layout", metadata.PositionalFormatSource);
            Assert.True(metadata.WithBreakLines);
            Assert.Equal("TextPositional", metadata.LayoutType);
            Assert.False(metadata.Suspect);
            Assert.Null(metadata.SuspectReason);
        }

        [Fact]
        public void Layout_sem_discriminador_usa_heuristica_e_marca_amostra_suspeita()
        {
            var layout = new Layout
            {
                Name = "LAYOUT_SINTETICO",
                LayoutGuid = "LAY_a698cc41-fbb0-4131-80e7-cbe97dd802f5",
                LayoutType = "TextPositional",
                WithBreakLines = null
            };

            var metadata = LowCodePositionalMetadata.Resolve(
                layout,
                "EDI_DC40 SYNTHETIC\nZRSDM_NFE_ITEM SYNTHETIC",
                lineLength: 600);

            Assert.Equal("RecordPerLine", metadata.PositionalFormat);
            Assert.Equal("heuristic", metadata.PositionalFormatSource);
            Assert.Null(metadata.WithBreakLines);
            Assert.True(metadata.Suspect);
            Assert.Equal("positional-format-not-declared-by-layout", metadata.SuspectReason);
        }

        [Fact]
        public void Meta_single_preserva_campos_legados_e_serializa_schema_v2_na_raiz()
        {
            var metadata = LowCodePositionalMetadata.Resolve(
                new Layout { LayoutType = "TextPositional", WithBreakLines = false },
                rawText: "STREAM_SINTETICO",
                lineLength: 600);
            var root = LowCodeDatasetMetaBuilder.AddPositionalMetadata(
                new Dictionary<string, object?>
                {
                    ["mapperGuid"] = "MAP_SYNTHETIC",
                    ["outputLength"] = 42
                },
                metadata);

            using var document = JsonDocument.Parse(JsonSerializer.Serialize(root));
            var json = document.RootElement;

            Assert.Equal("MAP_SYNTHETIC", json.GetProperty("mapperGuid").GetString());
            Assert.Equal(42, json.GetProperty("outputLength").GetInt32());
            Assert.Equal(2, json.GetProperty("datasetSchemaVersion").GetInt32());
            Assert.Equal("ContinuousStream", json.GetProperty("positionalFormat").GetString());
            Assert.Equal("layout", json.GetProperty("positionalFormatSource").GetString());
            Assert.False(json.GetProperty("withBreakLines").GetBoolean());
            Assert.Equal("TextPositional", json.GetProperty("layoutType").GetString());
            Assert.False(json.GetProperty("suspect").GetBoolean());
            Assert.Equal(JsonValueKind.Null, json.GetProperty("suspectReason").ValueKind);
        }

        [Fact]
        public void Meta_multi_sem_layout_serializa_default_suspeito_na_raiz()
        {
            var root = LowCodeDatasetMetaBuilder.AddPositionalMetadata(
                new Dictionary<string, object?>
                {
                    ["multiCandidate"] = true,
                    ["candidates"] = new[] { new { mapperGuid = "MAP_SYNTHETIC" } }
                },
                positionalMetadata: null);

            using var document = JsonDocument.Parse(JsonSerializer.Serialize(root));
            var json = document.RootElement;

            Assert.True(json.GetProperty("multiCandidate").GetBoolean());
            Assert.Equal(JsonValueKind.Array, json.GetProperty("candidates").ValueKind);
            Assert.Equal(2, json.GetProperty("datasetSchemaVersion").GetInt32());
            Assert.Equal("ContinuousStream", json.GetProperty("positionalFormat").GetString());
            Assert.Equal("default", json.GetProperty("positionalFormatSource").GetString());
            Assert.Equal(JsonValueKind.Null, json.GetProperty("withBreakLines").ValueKind);
            Assert.Equal("unknown", json.GetProperty("layoutType").GetString());
            Assert.True(json.GetProperty("suspect").GetBoolean());
            Assert.Equal(
                "positional-format-not-declared-by-layout",
                json.GetProperty("suspectReason").GetString());
        }
    }
}
