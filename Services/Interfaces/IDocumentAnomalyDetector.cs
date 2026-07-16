using LayoutParserApi.Services.Learning.Models;

namespace LayoutParserApi.Services.Interfaces
{
    /// <summary>
    /// Detector de anomalia de documentos posicionais: pontua um documento novo
    /// contra a distribuição histórica de features (MLData/DocumentPatterns) do mesmo LayoutGuid.
    /// </summary>
    public interface IDocumentAnomalyDetector
    {
        /// <summary>
        /// Calcula o score de anomalia do documento para o layout informado.
        /// Com menos amostras históricas que o mínimo, retorna resultado explícito
        /// de dados insuficientes (score null) — nunca inventa score.
        /// </summary>
        Task<DocumentAnomalyResult> DetectAsync(string documentContent, string layoutGuid);
    }
}
