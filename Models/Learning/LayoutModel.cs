namespace LayoutParserApi.Models.Learning
{
    /// <summary>
    /// Modelo de layout aprendido pelo sistema de ML
    /// </summary>
    public class LayoutModel
    {
        public string FileName { get; set; }
        public string FilePath { get; set; }
        public string FileType { get; set; } // txt, xml, mqseries
        public int TotalLines { get; set; }
        public int TotalFields { get; set; }
        public int LineLength { get; set; } // Para arquivos posicionais
        public List<FieldDefinition> Fields { get; set; } = new();
        public Dictionary<string, object> Metadata { get; set; } = new();
        public LearningStatistics Statistics { get; set; } = new();
        public DateTime LearnedAt { get; set; }
        public string ModelVersion { get; set; } = "1.0";
    }
}