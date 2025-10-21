using LayoutParserApi.Models.Analysis;
using LayoutParserApi.Models.Entities;

namespace LayoutParserApi.Services.Generation.Interfaces
{
    public interface ILayoutAnalysisService
    {
        Task<LayoutAnalysisResult> AnalyzeLayoutForAIAsync(Layout layout);
        Task<FieldPatternAnalysis> AnalyzeFieldPatternsAsync(string fieldName, List<string> sampleValues);
        Task<List<FieldAnalysis>> ExtractFieldMetadataAsync(Layout layout);
        Task<Dictionary<string, object>> ExtractLayoutMetadataAsync(Layout layout);
    }
}
