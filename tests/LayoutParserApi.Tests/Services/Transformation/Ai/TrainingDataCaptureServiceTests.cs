using System.Text.Json;

using LayoutParserApi.Services.Transformation.Ai;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Services.Transformation.Ai
{
    /// <summary>
    /// Cobre a captura best-effort do dataset de treino incremental (issue #338, F3 do ADR
    /// docs/architecture/adr-geracao-automatica-convergencia-tcl-xslt-2026-09-08.md).
    /// </summary>
    public class TrainingDataCaptureServiceTests
    {
        private static TrainingDataCaptureService CriarService(string trainingDataPath)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["XslSynth:TrainingDataPath"] = trainingDataPath,
                })
                .Build();

            return new TrainingDataCaptureService(
                NullLogger<TrainingDataCaptureService>.Instance,
                configuration);
        }

        [Fact]
        public void TryCapture_convergencia_bem_sucedida_grava_linha_jsonl_carregavel()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "lp-training-data-" + Guid.NewGuid().ToString("N"));
            try
            {
                var service = CriarService(tempDir);

                const string inputXml = "<ROOT><LINHA000><Campo>valor</Campo></LINHA000></ROOT>";
                const string groundTruth = "<nfe><infNFe><campo>valor</campo></infNFe></nfe>";
                const string finalXslt = "<xsl:stylesheet version=\"1.0\">fake</xsl:stylesheet>";

                service.TryCapture(
                    mapperGuid: "MAP_TESTE_GUID",
                    mapperName: "MAP_TESTE",
                    layoutName: "LAY_TESTE",
                    inputXml: inputXml,
                    groundTruthXml: groundTruth,
                    finalXslt: finalXslt,
                    iterationsUsed: 3);

                var files = Directory.GetFiles(tempDir, "*.jsonl");
                Assert.Single(files);

                var lines = File.ReadAllLines(files[0]);
                var line = Assert.Single(lines);

                // Mesma leitura que train_lora.py faz: json.loads(line) e depois
                // obj.get("instruction"/"input"/"output") — valida que as 3 chaves existem e que
                // o arquivo é JSON válido linha a linha (formato JSONL).
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                Assert.True(root.TryGetProperty("instruction", out var instructionProp));
                Assert.False(string.IsNullOrWhiteSpace(instructionProp.GetString()));

                Assert.True(root.TryGetProperty("input", out var inputProp));
                Assert.Equal(inputXml, inputProp.GetString());

                Assert.True(root.TryGetProperty("output", out var outputProp));
                Assert.Equal(finalXslt, outputProp.GetString());

                // Metadados extras — não exigidos por train_lora.py (usa obj.get), mas precisam
                // estar presentes pra auditoria/uso futuro (schema documentado no serviço).
                Assert.Equal(groundTruth, root.GetProperty("groundTruthXml").GetString());
                Assert.Equal("MAP_TESTE_GUID", root.GetProperty("mapperGuid").GetString());
                Assert.Equal("MAP_TESTE", root.GetProperty("mapperName").GetString());
                Assert.Equal("LAY_TESTE", root.GetProperty("layoutName").GetString());
                Assert.Equal(3, root.GetProperty("iterationsUsed").GetInt32());
                Assert.Equal("repair-orchestrator-runtime", root.GetProperty("source").GetString());
                Assert.True(root.TryGetProperty("capturedAtUtc", out _));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void TryCapture_falha_de_io_e_absorvida_sem_lancar()
        {
            // Path inválido em qualquer plataforma (caractere reservado do Windows) força
            // Directory.CreateDirectory/File.AppendAllText a lançar — o teste garante que o
            // método best-effort absorve a exceção em vez de propagar (não pode derrubar o loop
            // de síntese que já devolveu o candidato de IA).
            var caminhoInvalido = Path.Combine(Path.GetTempPath(), "lp-training-data-invalido", "con", "?", "*");
            var service = CriarService(caminhoInvalido);

            var exception = Record.Exception(() => service.TryCapture(
                mapperGuid: "MAP_TESTE_GUID",
                mapperName: "MAP_TESTE",
                layoutName: "LAY_TESTE",
                inputXml: "<ROOT/>",
                groundTruthXml: "<nfe/>",
                finalXslt: "<xsl:stylesheet/>",
                iterationsUsed: 1));

            Assert.Null(exception);
        }

        [Fact]
        public void BuildJsonlLine_produz_json_valido_com_as_tres_chaves_do_train_lora()
        {
            var line = TrainingDataCaptureServiceTestAccessor.BuildJsonlLine(
                mapperGuid: "GUID",
                mapperName: "NOME",
                layoutName: "LAYOUT",
                inputXml: "<a/>",
                groundTruthXml: "<b/>",
                finalXslt: "<c/>",
                iterationsUsed: 5,
                capturedAtUtc: new DateTime(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc));

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            Assert.Equal("<a/>", root.GetProperty("input").GetString());
            Assert.Equal("<c/>", root.GetProperty("output").GetString());
            Assert.Contains("NOME", root.GetProperty("instruction").GetString());
            Assert.Contains("LAYOUT", root.GetProperty("instruction").GetString());
        }
    }

    /// <summary>Acessa o método internal via InternalsVisibleTo (mesmo padrão já usado no projeto
    /// de testes) só pra testar a serialização isolada de I/O.</summary>
    internal static class TrainingDataCaptureServiceTestAccessor
    {
        public static string BuildJsonlLine(
            string mapperGuid,
            string? mapperName,
            string? layoutName,
            string inputXml,
            string groundTruthXml,
            string finalXslt,
            int iterationsUsed,
            DateTime capturedAtUtc)
            => (string)typeof(TrainingDataCaptureService)
                .GetMethod("BuildJsonlLine", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null, new object?[] { mapperGuid, mapperName, layoutName, inputXml, groundTruthXml, finalXslt, iterationsUsed, capturedAtUtc })!;
    }
}
