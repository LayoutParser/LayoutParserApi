using LayoutParserApi.Models.Generation;
using LayoutParserApi.Services.Interfaces;

namespace LayoutParserApi.Services.Logging
{
    public class DataGenerationLogger : IDataGenerationLogger
    {
        private readonly ConfigurableLogger _logger;

        public DataGenerationLogger(ConfigurableLogger logger)
        {
            _logger = logger;
        }

        public void LogGenerationStart(string layoutName, int recordCount, bool useAI)
        {
            _logger.LogInformation("DATA_GENERATION_START | Layout: {0} | Records: {1} | UseAI: {2}", layoutName, recordCount, useAI);
        }

        public void LogGenerationComplete(GeneratedDataResult result)
        {
            _logger.LogInformation("DATA_GENERATION_COMPLETE | Success: {0} | Records: {1} | Duration: {2}ms | Method: {3}", result.Success, result.TotalRecords, result.GenerationTime.TotalMilliseconds,result.GenerationMetadata.GetValueOrDefault("generationMethod", "unknown"));
        }

        public void LogGenerationError(string layoutName, Exception exception)
        {
            _logger.LogError(exception, "DATA_GENERATION_ERROR | Layout: {0} | Error: {1}", layoutName, exception.Message);
        }

        public void LogExcelProcessing(string fileName, int rowCount, int columnCount)
        {
            _logger.LogInformation("EXCEL_PROCESSING | File: {0} | Rows: {1} | Columns: {2}", fileName, rowCount, columnCount);
        }

        public void LogLayoutAnalysis(string layoutName, int fieldCount, int lineCount)
        {
            _logger.LogInformation("LAYOUT_ANALYSIS | Layout: {0} | Fields: {1} | Lines: {2}", layoutName, fieldCount, lineCount);
        }

        public void LogFieldPatternAnalysis(string fieldName, string pattern, string strategy)
        {
            _logger.LogInformation("FIELD_PATTERN_ANALYSIS | Field: {0} | Pattern: {1} | Strategy: {2}", fieldName, pattern, strategy);
        }
    }
}