using System.Text.Json;

using LayoutParserApi.Services.Transformation;
using LayoutParserApi.Services.Transformation.Models;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Services.Transformation
{
    /// <summary>
    /// Cobre <c>TransformationLearningService.GetAllModelsSummaryAsync</c>, usado pelo endpoint
    /// GET /api/metrics/learning/summary (débito técnico antes marcado como TODO em
    /// MetricsController.cs:127).
    /// </summary>
    public class TransformationLearningServiceModelsSummaryTests
    {
        private static TransformationLearningService CreateService(string learningModelsPath)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TransformationPipeline:LearningModelsPath"] = learningModelsPath,
                })
                .Build();

            return new TransformationLearningService(
                NullLogger<TransformationLearningService>.Instance,
                configuration);
        }

        [Fact]
        public async Task GetAllModelsSummaryAsync_sem_modelos_retorna_zerado()
        {
            // Caminho feliz de degrade gracioso: pasta existe (o construtor já cria via
            // Directory.CreateDirectory) mas está vazia — não pode lançar, deve retornar zerado.
            var learningModelsPath = Directory.CreateTempSubdirectory("lp-learning-summary-").FullName;
            try
            {
                var service = CreateService(learningModelsPath);

                var summary = await service.GetAllModelsSummaryAsync();

                Assert.Equal(0, summary.TotalModels);
                Assert.Equal(0, summary.TotalPatterns);
                Assert.Equal(0, summary.TotalExamples);
                Assert.Equal(0.0, summary.AverageConfidence);
            }
            finally
            {
                Directory.Delete(learningModelsPath, recursive: true);
            }
        }

        [Fact]
        public async Task GetAllModelsSummaryAsync_agrega_modelos_tcl_e_xsl_e_ignora_json_corrompido()
        {
            var learningModelsPath = Directory.CreateTempSubdirectory("lp-learning-summary-").FullName;
            try
            {
                var tclModel = new LearnedTclModel
                {
                    LayoutName = "LAY_TESTE",
                    ExamplesCount = 3,
                    Patterns = new List<LearnedPattern>
                    {
                        new() { Type = "field", Name = "p1", Confidence = 0.8 },
                        new() { Type = "field", Name = "p2", Confidence = 0.6 },
                    },
                };
                var xslModel = new LearnedXslModel
                {
                    LayoutName = "LAY_TESTE",
                    ExamplesCount = 5,
                    Patterns = new List<LearnedPattern>
                    {
                        new() { Type = "transform", Name = "t1", Confidence = 1.0 },
                    },
                };

                await File.WriteAllTextAsync(
                    Path.Combine(learningModelsPath, "tcl_LAY_TESTE.json"),
                    JsonSerializer.Serialize(tclModel));
                await File.WriteAllTextAsync(
                    Path.Combine(learningModelsPath, "xsl_LAY_TESTE.json"),
                    JsonSerializer.Serialize(xslModel));

                // Falha graciosa embutida: um modelo com JSON corrompido no meio da pasta não
                // pode derrubar a agregação dos demais.
                await File.WriteAllTextAsync(
                    Path.Combine(learningModelsPath, "tcl_LAY_CORROMPIDO.json"),
                    "{ isto nao e json valido");

                var service = CreateService(learningModelsPath);

                var summary = await service.GetAllModelsSummaryAsync();

                Assert.Equal(2, summary.TotalModels);
                Assert.Equal(3, summary.TotalPatterns);
                Assert.Equal(8, summary.TotalExamples);
                Assert.Equal((0.8 + 0.6 + 1.0) / 3.0, summary.AverageConfidence, precision: 10);
            }
            finally
            {
                Directory.Delete(learningModelsPath, recursive: true);
            }
        }
    }
}
