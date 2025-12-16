using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Generation;

namespace LayoutParserApi.Services.Interfaces
{
    public interface IIADataGeneratorService
    {
        Task<GeneratedDataResult> GenerateSyntheticDataAsync(Layout layout, int numberOfRecords, string sampleRealData = null);
        Task<string> GenerateFieldValueAsync(FieldElement field, string context, string dataType);
    }
}
