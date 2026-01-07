namespace LayoutParserApi.Models.Learning
{
    /// <summary>
    /// Resultado de análise de arquivo
    /// </summary>
    public class FileAnalysisResult
    {
        public string FileName { get; set; }
        public string FilePath { get; set; }
        public string FileType { get; set; }
        public long FileSize { get; set; }
        public int LineCount { get; set; }
        public string Content { get; set; }
        public string FormattedContent { get; set; } // Para XML formatado
        public LayoutModel LearnedModel { get; set; }
        public Dictionary<string, object> AnalysisData { get; set; } = new();
    }
}