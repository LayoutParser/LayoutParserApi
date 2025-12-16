using LayoutParserApi.Services.Generation.TxtGenerator.Enum;
using LayoutParserApi.Services.Generation.TxtGenerator.Parsers;
using LayoutParserApi.Services.Generation.TxtGenerator.Validators;

namespace LayoutParserApi.Services.Generation.TxtGenerator
{
    /// <summary>
    /// Factory para criar TxtFileGeneratorService com o gerador correto baseado no modo
    /// </summary>
    public class TxtFileGeneratorFactory
    {
        private readonly IServiceProvider _serviceProvider;

        public TxtFileGeneratorFactory(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public TxtFileGeneratorService Create(GenerationMode mode)
        {
            var logger = _serviceProvider.GetRequiredService<ILogger<TxtFileGeneratorService>>();
            var xmlParser = _serviceProvider.GetRequiredService<XmlLayoutParser>();
            var excelParser = _serviceProvider.GetRequiredService<ExcelRulesParser>();
            var validator = _serviceProvider.GetRequiredService<LayoutValidator>();

            return new TxtFileGeneratorService(logger, xmlParser, excelParser, validator, _serviceProvider, mode);
        }
    }
}