using LayoutParserApi.Models.Validation;

namespace LayoutParserApi.Models.ML
{
    /// <summary>
    /// Amostra de treino para ML: documento (normalmente com erro) + metadados + erros detectados.
    /// Objetivo: permitir que a ML aprenda padrões de documentos inválidos e evolua para sugerir correções
    /// e futuramente gerar regras (TCL/XSLT) de transformação.
    /// </summary>
    public class DocumentTrainingSample
    {
        public string SampleId { get; set; } = Guid.NewGuid().ToString("N");

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        public string LayoutGuid { get; set; } = "";

        public string? LayoutName { get; set; }

        public string? DetectedType { get; set; } // ex: mqseries, idoc, xml

        public string? OriginalFileName { get; set; }

        public string Source { get; set; } = "upload"; // upload | api | batch | etc

        public bool IsValid { get; set; }

        public int? FirstErrorLineIndex { get; set; }

        public string? Sha256 { get; set; }

        public int DocumentLength { get; set; }

        public List<DocumentLineError>? Errors { get; set; }

        /// <summary>
        /// Caminho do arquivo .txt salvo em disco (conteúdo completo do documento).
        /// </summary>
        public string? SavedDocumentPath { get; set; }

        /// <summary>
        /// Caminho do arquivo .json salvo em disco (metadados da amostra).
        /// </summary>
        public string? SavedMetadataPath { get; set; }
    }
}