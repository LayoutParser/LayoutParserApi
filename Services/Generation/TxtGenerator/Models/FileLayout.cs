namespace LayoutParserApi.Services.Generation.TxtGenerator.Models
{
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