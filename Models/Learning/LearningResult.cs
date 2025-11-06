using System.Collections.Generic;

namespace LayoutParserApi.Models.Learning
{
    /// <summary>
    /// Resultado do processo de aprendizado
    /// </summary>
    public class LearningResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public LayoutModel LearnedModel { get; set; }
        public string ModelPath { get; set; }
        public List<string> Warnings { get; set; } = new();
        public List<string> Errors { get; set; } = new();
        public TimeSpan ProcessingTime { get; set; }
        public Dictionary<string, object> AdditionalData { get; set; } = new();
    }

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

