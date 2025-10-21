using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Generation;

namespace LayoutParserApi.Services.Generation.Interfaces
{
    public interface ISyntheticDataGeneratorService
    {
        Task<GeneratedDataResult> GenerateSyntheticDataAsync(SyntheticDataRequest request);
        Task<string> GenerateFieldValueAsync(FieldElement field, string context, string dataType, ExcelDataContext excelContext = null);
        Task<List<string>> GenerateMultipleFieldValuesAsync(FieldElement field, int count, ExcelDataContext excelContext = null);
        Task<Dictionary<string, object>> AnalyzeFieldGenerationRequirements(FieldElement field, ExcelDataContext excelContext = null);
    }
}
