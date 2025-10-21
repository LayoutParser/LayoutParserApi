using LayoutParserApi.Models.Generation;

namespace LayoutParserApi.Services.Logging
{
    public interface IDataGenerationLogger
    {
        void LogGenerationStart(string layoutName, int recordCount, bool useAI);
        void LogGenerationComplete(GeneratedDataResult result);
        void LogGenerationError(string layoutName, Exception exception);
        void LogExcelProcessing(string fileName, int rowCount, int columnCount);
        void LogLayoutAnalysis(string layoutName, int fieldCount, int lineCount);
        void LogFieldPatternAnalysis(string fieldName, string pattern, string strategy);
    }
}
