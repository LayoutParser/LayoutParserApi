using System.Collections.Generic;

namespace LayoutParserApi.Services.Generation.TxtGenerator.Models
{
    /// <summary>
    /// Layout completo de um registro (linha)
    /// </summary>
    public class RecordLayout
    {
        public string Name { get; set; } // HEADER, LINHA000, etc.
        public string InitialValue { get; set; } // Valor inicial da linha
        public int TotalLength { get; set; } // Tamanho total da linha (ex: 600)
        public int MinimalOccurrence { get; set; } // Ocorrência mínima
        public int MaximumOccurrence { get; set; } // Ocorrência máxima
        public List<FieldDefinition> Fields { get; set; } = new();
        public int Sequence { get; set; } // Ordem da linha no layout
    }

    /// <summary>
    /// Layout completo do arquivo
    /// </summary>
    public class FileLayout
    {
        public string LayoutName { get; set; }
        public string LayoutType { get; set; } // TextPositional, Xml, IDOC
        public int LimitOfCharacters { get; set; } // Tamanho padrão das linhas
        public List<RecordLayout> Records { get; set; } = new();
    }
}

