namespace LayoutParserApi.Models.Database
{
    public class LayoutRecord
    {
        public int Id { get; set; }
        public Guid LayoutGuid { get; set; }
        public Guid PackageGuid { get; set; }
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string LayoutType { get; set; } = "";
        public string ValueContent { get; set; } = ""; // Conteúdo criptografado
        public string XmlShemaValidatorPath { get; set; } = "";
        public int ProjectId { get; set; }
        public DateTime LastUpdateDate { get; set; }
        public string DecryptedContent { get; set; } = ""; // Conteúdo descriptografado
    }
}