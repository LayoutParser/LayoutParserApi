using LayoutParserApi.Models.Analysis;
using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Generation;

namespace LayoutParserApi.Services.Generation.Interfaces
{
    public interface IAIService
    {
        Task<string> GenerateSyntheticDataAsync(string prompt, int maxTokens = 2000);
        Task<FieldPatternAnalysis> AnalyzeFieldPatternsAsync(string fieldName, List<string> sampleValues);
        Task<List<FieldMapping>> SuggestFieldMappingsAsync(Layout layout, ExcelDataContext excelData);
        Task<string> GenerateFieldValueAsync(string fieldName, string dataType, int length, string context, List<string> sampleValues = null);
        Task<Dictionary<string, object>> AnalyzeDataPatternsAsync(ExcelDataContext excelData);
    }
}
