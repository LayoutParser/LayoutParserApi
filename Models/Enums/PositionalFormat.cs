namespace LayoutParserApi.Models.Enums
{
    /// <summary>
    /// Formato FÍSICO de um documento posicional.
    ///
    /// <para><c>LayoutType = "TextPositional"</c> é um tipo sobrecarregado: cobre dois formatos
    /// mutuamente incompatíveis. O discriminador canônico é <c>Layout.WithBreakLines</c>, resolvido
    /// por <see cref="Configuration.PositionalFormatResolver"/> — nunca por heurística de conteúdo
    /// nem por <c>LayoutType</c>.</para>
    ///
    /// Ver ADR-001 (<c>docs/architecture/adr-001-discriminador-formato-posicional.md</c>).
    /// </summary>
    public enum PositionalFormat
    {
        /// <summary>
        /// MQSeries: stream contínuo, sem quebra de linha, fatiado a cada N caracteres
        /// (legado 600). Cada registro carrega o campo <c>Sequencia</c> de 6 chars no início.
        /// </summary>
        ContinuousStream,

        /// <summary>
        /// IDOC SAP: um registro por linha física (LF), largura variável por segmento.
        /// <b>Não tem</b> o campo <c>Sequencia</c> — aplicar o salto de 6 chars aqui desloca
        /// 100% dos campos (defeito de 2026-08-03, ver ADR-001).
        /// </summary>
        RecordPerLine
    }

    /// <summary>
    /// De onde veio a decisão de <see cref="PositionalFormat"/>. Serve para instrumentar a migração
    /// dos layouts legados: enquanto houver resolução por fallback, há layout sem
    /// <c>&lt;WithBreakLines&gt;</c> no XML.
    /// </summary>
    public enum PositionalFormatSource
    {
        /// <summary>Fonte canônica: <c>&lt;WithBreakLines&gt;</c> presente no XML do layout.</summary>
        LayoutMetadata,

        /// <summary>
        /// Fallback DEPRECADO: campo ausente e heurística de conteúdo conclusiva
        /// (<c>EDI_DC40</c>/<c>ZRSDM_</c> no início do documento).
        /// </summary>
        LegacyContentHeuristic,

        /// <summary>
        /// Fallback DEPRECADO: campo ausente e heurística inconclusiva — assume o comportamento
        /// histórico (<see cref="PositionalFormat.ContinuousStream"/>).
        /// </summary>
        LegacyDefault
    }
}
