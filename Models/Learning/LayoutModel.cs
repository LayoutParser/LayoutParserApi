using System.Collections.Generic;

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

    /// <summary>
    /// Definição de campo aprendida
    /// </summary>
    public class FieldDefinition
    {
        public string Name { get; set; }
        public int StartPosition { get; set; }
        public int EndPosition { get; set; }
        public int Length => EndPosition - StartPosition + 1;
        public string DataType { get; set; } // string, int, decimal, date, datetime, cnpj, cpf, etc.
        public string Alignment { get; set; } // Left, Right, Center
        public bool IsRequired { get; set; }
        public List<string> SampleValues { get; set; } = new();
        public string Pattern { get; set; } // Regex pattern detectado
        public double Confidence { get; set; } // Confiança do ML (0-1)
        public string LineName { get; set; } // Para arquivos com múltiplas linhas
        public int Sequence { get; set; }
    }

    /// <summary>
    /// Estatísticas de aprendizado
    /// </summary>
    public class LearningStatistics
    {
        public int TotalSamples { get; set; }
        public int ValidSamples { get; set; }
        public int InvalidSamples { get; set; }
        public double Accuracy { get; set; }
        public Dictionary<string, int> DataTypeDistribution { get; set; } = new();
        public Dictionary<string, double> FieldConfidence { get; set; } = new();
        public List<string> DetectedPatterns { get; set; } = new();
    }
}

