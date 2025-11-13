using System;

namespace LayoutParserApi.Models.Entities
{
    /// <summary>
    /// Modelo para mapeadores de transformação
    /// </summary>
    public class Mapper
    {
        public int Id { get; set; }
        public string MapperGuid { get; set; }
        public string PackageGuid { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public bool IsXPathMapper { get; set; }
        public string InputLayoutGuid { get; set; } // Da coluna do banco
        public string TargetLayoutGuid { get; set; } // Da coluna do banco
        public string ValueContent { get; set; } // Conteúdo criptografado
        public string DecryptedContent { get; set; } = ""; // Conteúdo descriptografado
        public string ProjectId { get; set; }
        public DateTime LastUpdateDate { get; set; }
        
        // GUIDs extraídos do XML descriptografado (mais confiáveis)
        public string InputLayoutGuidFromXml { get; set; }
        public string TargetLayoutGuidFromXml { get; set; }
        
        // XSL extraído do XML descriptografado (prioridade sobre geração)
        public string XslContent { get; set; }
    }
}

