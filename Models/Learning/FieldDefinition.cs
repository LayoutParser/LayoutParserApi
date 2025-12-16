namespace LayoutParserApi.Models.Learning
{
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
}