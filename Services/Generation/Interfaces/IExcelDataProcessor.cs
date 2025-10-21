using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Generation;

namespace LayoutParserApi.Services.Generation.Interfaces
{
    public interface IExcelDataProcessor
    {
        Task<ExcelProcessingResult> ProcessExcelFileAsync(Stream excelStream, string fileName = null);
        Task<List<FieldMapping>> MapExcelColumnsToLayoutFieldsAsync(ExcelDataContext excelData, Layout layout);
        Task<Dictionary<string, string>> DetectColumnTypesAsync(ExcelDataContext excelData);
        Task<List<string>> ExtractSampleValuesAsync(ExcelDataContext excelData, string columnName, int maxSamples = 10);
    }
}
