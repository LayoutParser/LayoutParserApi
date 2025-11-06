using LayoutParserApi.Models.Entities;

namespace LayoutParserApi.Services.Generation.Interfaces
{
    /// <summary>
    /// Interface para validação incremental de linhas geradas
    /// </summary>
    public interface ILineValidator
    {
        /// <summary>
        /// Valida uma linha gerada contra o layout
        /// </summary>
        LineValidationResult ValidateLine(string generatedLine, LineElement lineConfig, int expectedLength = 600);

        /// <summary>
        /// Valida campos de uma linha gerada
        /// </summary>
        FieldValidationResult ValidateFields(string generatedLine, LineElement lineConfig);
    }

    public class LineValidationResult
    {
        public bool IsValid { get; set; }
        public int ActualLength { get; set; }
        public int ExpectedLength { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public string LineName { get; set; }
    }

    public class FieldValidationResult
    {
        public int TotalFields { get; set; }
        public int ValidFields { get; set; }
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }
        public bool HasErrors => ErrorCount > 0;
        public List<FieldValidationDetail> Details { get; set; } = new();
    }

    public class FieldValidationDetail
    {
        public string FieldName { get; set; }
        public int Start { get; set; }
        public int Length { get; set; }
        public string Value { get; set; }
        public string Status { get; set; }
        public string ErrorMessage { get; set; }
    }
}

