using LayoutParserApi.Services.Generation.Implementations;
using LayoutParserApi.Services.Generation.Interfaces;
using LayoutParserApi.Services.Generation.TxtGenerator;
using LayoutParserApi.Services.Generation.TxtGenerator.Generators;
using LayoutParserApi.Services.Generation.TxtGenerator.Parsers;

namespace LayoutParserApi.Services.Generation
{
    /// <summary>
    /// Grupo "Generation Services" do DI — tudo que o <c>DataGenerationController</c> precisa para
    /// resolver e para gerar arquivo (geração sintética / TXT a partir do layout XML).
    ///
    /// Por que isso é um método de extensão e não um bloco solto no <c>Program.cs</c>: o grupo já foi
    /// perdido DUAS vezes. Na primeira (issue #33) ele nunca existiu; na segunda, a resolução de
    /// conflito do merge que removeu o Pathway 1 apagou o bloco inteiro como dano colateral e o
    /// teste que deveria travar a regressão continuou verde, porque ele COPIAVA os registros num
    /// <c>ServiceCollection</c> próprio em vez de exercitar a composição real. Com a composição num
    /// único lugar, o teste chama exatamente o que o <c>Program.cs</c> chama — não uma réplica que
    /// pode divergir.
    /// </summary>
    public static class GenerationServiceCollectionExtensions
    {
        /// <summary>
        /// Registra o grupo Generation (Scoped, como o resto dos serviços de request).
        /// </summary>
        public static IServiceCollection AddGenerationServices(this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            // Dependências diretas do construtor do DataGenerationController.
            services.AddScoped<ISyntheticDataGeneratorService, SyntheticDataGeneratorService>();
            services.AddScoped<IExcelDataProcessor, ExcelDataProcessor>();
            services.AddScoped<ILayoutAnalysisService, LayoutAnalysisService>();
            services.AddScoped<TxtFileGeneratorFactory>();

            // Dependências internas do TxtFileGeneratorFactory: ele recebe só o IServiceProvider e
            // resolve estas por GetRequiredService dentro do Create() — ou seja, a falta delas NÃO
            // aparece ao resolver o controller, só quando o endpoint chama o factory.
            services.AddScoped<XmlLayoutParser>();
            services.AddScoped<ExcelRulesParser>();
            // Nome totalmente qualificado de propósito: LayoutValidator existe em
            // Generation.TxtGenerator.Validators E em Parsing.Implementations (este último já
            // registrado como ILayoutValidator no grupo Parsing). Aqui é o de Generation.
            services.AddScoped<LayoutParserApi.Services.Generation.TxtGenerator.Validators.LayoutValidator>();

            // Geradores de valor escolhidos por GenerationMode dentro do TxtFileGeneratorService.
            // Lá a resolução é por GetService (nullable): sem registro NÃO lança na criação — o campo
            // fica null e o estouro vira NullReferenceException no meio da geração do arquivo.
            services.AddScoped<DeterministicGenerator>();
            services.AddScoped<RandomGenerator>();

            return services;
        }
    }
}
