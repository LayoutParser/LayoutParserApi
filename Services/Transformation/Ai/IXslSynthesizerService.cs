namespace LayoutParserApi.Services.Transformation.Ai
{
    /// <summary>
    /// Encapsula o <c>RepairOrchestrator</c> real de <c>ai/XslSynth.Core</c> (síntese de XSLT
    /// via Ollama, loop gerar → validar (diff canônico + XSD) → corrigir), pra uso in-process
    /// pelo runtime da API — não é mais um CLI standalone.
    ///
    /// Design: docs/architecture/design-integracao-repairorchestrator-runtime-2026-08-21.md
    /// (Opção B — referência direta a <c>ai/XslSynth.Core</c>, sem boundary Linux/WSL real).
    /// </summary>
    public interface IXslSynthesizerService
    {
        /// <summary>
        /// Sintetiza (ou repara) o XSLT para o <paramref name="mapperGuid"/> informado, a partir
        /// do MapeadorVO real (resolvido internamente via <c>ICachedMapperService</c>) e de um
        /// documento de entrada JÁ EM XML (o low-code intermediário — não o TXT posicional cru;
        /// o <c>RepairOrchestrator</c> aplica XSLT sobre XML, então o TXT precisa ter passado
        /// pela etapa de parsing/low-code antes de chegar aqui).
        /// </summary>
        /// <param name="mapperGuid">GUID do mapeador (resolve o MapeadorVO real via cache/banco).</param>
        /// <param name="inputXml">Documento de entrada, já em XML (low-code intermediário).</param>
        /// <param name="groundTruthXml">XML final esperado (gabarito), usado pelo diff canônico.</param>
        /// <param name="maxIterations">Teto de iterações do loop gerar→validar→corrigir.</param>
        /// <param name="layoutName">Nome do layout — usado só para persistir o XSLT convergido na
        /// convenção <c>{mapperName}_{layoutName}.xsl</c> já lida por <c>TransformationPipelineService</c>
        /// (issue #55). Persistência é best-effort: falha aqui não derruba a síntese.</param>
        Task<XslSynthesisResult> SynthesizeAsync(
            string mapperGuid,
            string inputXml,
            string groundTruthXml,
            int maxIterations,
            string? layoutName,
            CancellationToken cancellationToken);
    }

    /// <summary>Resultado do loop de síntese — mesmo vocabulário de <see cref="AiCandidateDiagnostics"/>
    /// (não duplica contrato).</summary>
    public sealed class XslSynthesisResult
    {
        public bool Success { get; init; }
        public bool Converged { get; init; }
        public string? GeneratedXslt { get; init; }
        public string? FinalOutputXml { get; init; }
        public int IterationsUsed { get; init; }
        public bool XsdValid { get; init; }
        public IReadOnlyList<string> ValidationErrors { get; init; } = Array.Empty<string>();
        public string? Error { get; init; }
    }
}
