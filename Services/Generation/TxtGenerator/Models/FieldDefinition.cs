namespace LayoutParserApi.Services.Generation.TxtGenerator.Models
{
    /// <summary>
    /// Definição de um campo do layout posicional
    /// </summary>
    public class FieldDefinition
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public int StartPosition { get; set; }
        public int EndPosition { get; set; }
        public int Length => EndPosition - StartPosition + 1;
        public string DataType { get; set; } // string, int, decimal, date, datetime, cnpj, cpf, etc.
        public string Alignment { get; set; } // Left, Right, Center
        public bool IsRequired { get; set; }
        public bool IsFixed { get; set; } // Se deve ser gerado fixo ou randomicamente
        public string FixedValue { get; set; } // Valor fixo se IsFixed = true
        public string Domain { get; set; } // Valores possíveis separados por vírgula
        public string Example { get; set; } // Exemplo de valor
        public string LineName { get; set; } // Nome da linha (HEADER, LINHA000, etc.)
        public int Sequence { get; set; } // Ordem do campo na linha
        public string InitialValue { get; set; } // Valor inicial da linha (ex: "HEADER")
        public Dictionary<string, string> BusinessRules { get; set; } = new(); // Regras de negócio adicionais
    }
}