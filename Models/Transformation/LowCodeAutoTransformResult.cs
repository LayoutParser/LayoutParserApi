namespace LayoutParserApi.Models.Transformation
{
    /// <summary>
    /// Resultado do pathway LowCode-auto (<see cref="LayoutParserApi.Services.Transformation.LowCode.LowCodeAutoTransformationService"/>)
    /// para UM documento, normalizado para consumo síncrono (ex.: resposta do <c>ParseController.Upload</c>).
    /// Independente de N==1 (caminho de sempre) ou N&gt;1 (multi-candidato), o resultado é sempre expresso
    /// como uma lista de candidatos — a diferença de shape persistido em disco (ver
    /// <c>LowCodeAutoTransformationService</c>) não vaza para este contrato de retorno.
    /// </summary>
    public class LowCodeAutoTransformResult
    {
        /// <summary>
        /// Indica se o pathway LowCode-auto é aplicável a este documento/layout: false quando não há
        /// mapper algum encontrado no banco para o layoutGuid (ou o próprio layoutGuid/rawText não
        /// permitiam sequer tentar) — cenário de "não aplicável", distinto de timeout ou erro.
        /// </summary>
        public bool Applicable { get; set; }

        /// <summary>True quando 2+ mapeadores (MapperGuid distintos) foram considerados genuinamente plausíveis.</summary>
        public bool MultiCandidate { get; set; }

        /// <summary>Um resultado por mapper candidato (1 item quando MultiCandidate==false).</summary>
        public List<LowCodeCandidateResult> Candidates { get; set; } = new();
    }
}
