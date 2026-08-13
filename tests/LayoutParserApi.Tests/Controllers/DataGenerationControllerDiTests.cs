using LayoutParserApi.Controllers;
using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Parsing;
using LayoutParserApi.Models.Responses;
using LayoutParserApi.Models.Structure;
using LayoutParserApi.Services.Generation.Implementations;
using LayoutParserApi.Services.Generation.Interfaces;
using LayoutParserApi.Services.Generation.TxtGenerator;
using LayoutParserApi.Services.Generation.TxtGenerator.Parsers;
using LayoutParserApi.Services.Interfaces;

using Microsoft.Extensions.DependencyInjection;

namespace LayoutParserApi.Tests.Controllers
{
    /// <summary>
    /// Issue #33: <see cref="ISyntheticDataGeneratorService"/>, <see cref="IExcelDataProcessor"/>,
    /// <see cref="ILayoutAnalysisService"/> e <see cref="TxtFileGeneratorFactory"/> não estavam
    /// registrados em <c>Program.cs</c> — qualquer chamada ao <see cref="DataGenerationController"/>
    /// quebrava com <c>InvalidOperationException</c> do container de DI em runtime, mesmo com o
    /// build passando (o erro só aparece na resolução, não na compilação). Este teste replica o
    /// mesmo grupo "Generation Services" adicionado a <c>Program.cs</c> e garante que o container
    /// resolve o controller sem lançar.
    /// </summary>
    public class DataGenerationControllerDiTests
    {
        [Fact]
        public void Container_resolve_DataGenerationController_com_o_grupo_Generation_registrado()
        {
            var services = new ServiceCollection();
            services.AddLogging();

            // Mesmo grupo "Generation Services" registrado em Program.cs.
            services.AddScoped<ISyntheticDataGeneratorService, SyntheticDataGeneratorService>();
            services.AddScoped<IExcelDataProcessor, ExcelDataProcessor>();
            services.AddScoped<ILayoutAnalysisService, LayoutAnalysisService>();
            services.AddScoped<XmlLayoutParser>();
            services.AddScoped<ExcelRulesParser>();
            services.AddScoped<LayoutParserApi.Services.Generation.TxtGenerator.Validators.LayoutValidator>();
            services.AddScoped<TxtFileGeneratorFactory>();

            // Única dependência do controller fora do grupo Generation — fake mínimo, o alvo
            // deste teste é a resolução do container, não o comportamento de parsing.
            services.AddScoped<ILayoutParserService, FakeLayoutParserService>();

            services.AddScoped<DataGenerationController>();

            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();

            var controller = scope.ServiceProvider.GetRequiredService<DataGenerationController>();

            Assert.NotNull(controller);
        }

        private sealed class FakeLayoutParserService : ILayoutParserService
        {
            public Task<ParsingResult> ParseAsync(Stream layoutStream, Stream txtStream) =>
                Task.FromResult(new ParsingResult { Success = true });

            public Layout ReestruturarLayout(Layout layoutOriginal) => layoutOriginal;

            public Layout ReordenarSequences(Layout layout) => layout;

            public DocumentStructure BuildDocumentStructure(ParsingResult result) => new();

            public List<LineValidationInfo> CalculateLineValidations(Layout layout, int expectedLineLength) => [];

            public Task<Layout?> ParseLayoutFromXmlAsync(string xmlContent) => Task.FromResult<Layout?>(null);
        }
    }
}
