using LayoutParserApi.Controllers;
using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Parsing;
using LayoutParserApi.Models.Responses;
using LayoutParserApi.Models.Structure;
using LayoutParserApi.Services.Generation;
using LayoutParserApi.Services.Generation.TxtGenerator;
using LayoutParserApi.Services.Generation.TxtGenerator.Enum;
using LayoutParserApi.Services.Generation.TxtGenerator.Generators;
using LayoutParserApi.Services.Interfaces;

using Microsoft.Extensions.DependencyInjection;

namespace LayoutParserApi.Tests.Controllers
{
    /// <summary>
    /// Trava a regressão de DI do grupo Generation (issue #33), que já aconteceu duas vezes.
    ///
    /// A primeira versão deste teste era VÁCUA: montava um <c>ServiceCollection</c> com os registros
    /// COPIADOS do <c>Program.cs</c>, então continuava verde depois que um merge apagou o bloco real —
    /// exatamente o cenário que ele deveria pegar. Aqui a composição é única
    /// (<see cref="GenerationServiceCollectionExtensions.AddGenerationServices"/>, chamada pelo
    /// <c>Program.cs</c> e por estes testes) e o par de garantias é:
    ///
    /// <list type="bullet">
    ///   <item>apagar um registro DENTRO de <c>AddGenerationServices</c> quebra os testes de resolução;</item>
    ///   <item>apagar a CHAMADA de <c>AddGenerationServices</c> no <c>Program.cs</c> quebra
    ///         <see cref="Program_cs_chama_AddGenerationServices"/>.</item>
    /// </list>
    ///
    /// Trade-off assumido: descartamos <c>WebApplicationFactory&lt;Program&gt;</c> (que exercitaria a
    /// composition root de verdade e dispensaria o teste de call-site). Motivo: ela sobe o host real,
    /// que lê o <c>appsettings.json</c> da API — cujo <c>Logging:File:Directory</c> aponta para o
    /// diretório de log do SERVIDOR — tenta conectar no Redis e liga os <c>IHostedService</c> de
    /// warm-up do catálogo (SQL). Seria um teste unitário dependente de infra externa e capaz de
    /// escrever em diretório de produção, além de exigir pacote de teste novo
    /// (<c>Microsoft.AspNetCore.Mvc.Testing</c>) e tornar <c>Program</c> público/parcial.
    /// </summary>
    public class DataGenerationControllerDiTests
    {
        [Fact]
        public void AddGenerationServices_resolve_o_DataGenerationController()
        {
            using var provider = MontarProvider();
            using var scope = provider.CreateScope();

            var controller = scope.ServiceProvider.GetRequiredService<DataGenerationController>();

            Assert.NotNull(controller);
        }

        /// <summary>
        /// O <see cref="TxtFileGeneratorFactory"/> recebe só o <c>IServiceProvider</c> e resolve
        /// XmlLayoutParser/ExcelRulesParser/LayoutValidator dentro do <c>Create()</c> — resolver o
        /// controller NÃO prova que essas dependências existem. Este teste fecha esse buraco:
        /// exercita o factory nos dois modos de geração.
        /// </summary>
        [Theory]
        [InlineData(GenerationMode.Deterministic)]
        [InlineData(GenerationMode.Random)]
        public void AddGenerationServices_permite_o_factory_criar_o_gerador(GenerationMode modo)
        {
            using var provider = MontarProvider();
            using var scope = provider.CreateScope();

            var factory = scope.ServiceProvider.GetRequiredService<TxtFileGeneratorFactory>();

            var generatorService = factory.Create(modo);

            Assert.NotNull(generatorService);
        }

        /// <summary>
        /// Os geradores de valor são resolvidos por <c>GetService</c> (nullable) dentro do
        /// <c>TxtFileGeneratorService</c>: sem registro, nada lança na criação — o campo fica null e
        /// o estouro só aparece como NullReferenceException no meio da geração do arquivo. Por isso
        /// a presença deles é assertada aqui, e não só o <c>Create()</c> acima.
        /// </summary>
        [Fact]
        public void AddGenerationServices_registra_os_geradores_de_valor()
        {
            using var provider = MontarProvider();
            using var scope = provider.CreateScope();

            Assert.NotNull(scope.ServiceProvider.GetService<DeterministicGenerator>());
            Assert.NotNull(scope.ServiceProvider.GetService<RandomGenerator>());
        }

        /// <summary>
        /// Guarda de call-site: o teste acima só prova que o grupo Generation, SE registrado, resolve.
        /// Quem garante que o <c>Program.cs</c> continua registrando é este — foi apagar essa chamada
        /// que produziu a regressão. Sem lib de mock e sem subir o host, a verificação é sobre o
        /// fonte real do <c>Program.cs</c> (linha ativa, comentário não conta).
        /// </summary>
        [Fact]
        public void Program_cs_chama_AddGenerationServices()
        {
            string programCs = Path.Combine(LocalizarRaizDoRepo(), "Program.cs");

            // Falha explícita (não "passa por omissão") se o fonte não for encontrado: um gate que
            // não consegue olhar o alvo é um gate que mente — foi assim que a regressão passou.
            Assert.True(File.Exists(programCs), $"Program.cs não encontrado para inspeção: {programCs}");

            bool chamaOGrupo = File.ReadLines(programCs)
                .Select(linha => linha.Trim())
                .Any(linha => !linha.StartsWith("//", StringComparison.Ordinal)
                              && linha.Contains("AddGenerationServices(", StringComparison.Ordinal));

            Assert.True(chamaOGrupo,
                "Program.cs não chama builder.Services.AddGenerationServices(): o grupo Generation " +
                "voltou a ficar fora do DI e o DataGenerationController quebra em runtime (issue #33).");
        }

        /// <summary>
        /// Mesma composição do <c>Program.cs</c> para o grupo Generation. Fora dele, só o mínimo:
        /// logging e um fake do <see cref="ILayoutParserService"/> (o projeto não tem lib de mock —
        /// fakes escritos à mão são o padrão da suíte).
        /// </summary>
        private static ServiceProvider MontarProvider()
        {
            var services = new ServiceCollection();
            services.AddLogging();

            services.AddGenerationServices();

            services.AddScoped<ILayoutParserService, FakeLayoutParserService>();
            services.AddScoped<DataGenerationController>();

            return services.BuildServiceProvider();
        }

        /// <summary>
        /// Sobe a partir do diretório de saída do teste até achar o .csproj da API — mesmo padrão já
        /// usado em PositionalFormatRegressionTests.
        /// </summary>
        private static string LocalizarRaizDoRepo()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LayoutParserApi.csproj")))
                dir = dir.Parent;

            return dir?.FullName ?? AppContext.BaseDirectory;
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
