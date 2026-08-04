using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Enums;

namespace LayoutParserApi.Models.Configuration
{
    /// <summary>
    /// Resolve o <see cref="PositionalFormat"/> de um documento posicional a partir do LAYOUT
    /// (contrato), não do conteúdo (variável).
    ///
    /// <para>Fonte canônica: <c>Layout.WithBreakLines</c>. As heurísticas de conteúdo
    /// (<c>EDI_DC40</c>/<c>ZRSDM_NFE</c>) sobrevivem apenas como <b>fallback deprecado</b> para
    /// layouts legados que não têm o elemento no XML — nunca como regra primária.</para>
    ///
    /// <para>Resolver PURO (sem I/O e sem logging), no mesmo espírito de
    /// <see cref="LineLengthResolver"/>: quem tem <c>ILogger</c> loga o resultado. Ver ADR-001
    /// (<c>docs/architecture/adr-001-discriminador-formato-posicional.md</c>).</para>
    /// </summary>
    public static class PositionalFormatResolver
    {
        /// <summary>
        /// Ordem de precedência (ADR-001 §5.1):
        /// <list type="number">
        /// <item><c>&lt;WithBreakLines&gt;</c> presente → usa o valor. Decisão final, sem migração pendente.</item>
        /// <item>Ausente + heurística de conteúdo conclusiva → heurística. Migração pendente.</item>
        /// <item>Ausente + heurística inconclusiva → <see cref="PositionalFormat.ContinuousStream"/>
        /// (comportamento histórico). Migração pendente.</item>
        /// </list>
        /// </summary>
        /// <param name="layout">Layout já carregado (pode ser nulo).</param>
        /// <param name="rawText">Conteúdo do documento — usado só no fallback e na validação cruzada.</param>
        /// <param name="lineLength">Tamanho de linha resolvido (ver <see cref="LineLengthResolver"/>).</param>
        public static PositionalFormatResolution Resolve(Layout? layout, string? rawText, int lineLength = LineLengthResolver.LegacyDefaultLineLength)
        {
            // 1. Fonte canônica: o próprio contrato do layout.
            if (layout?.WithBreakLines is bool withBreakLines)
            {
                var formatoDoContrato = withBreakLines ? PositionalFormat.RecordPerLine : PositionalFormat.ContinuousStream;

                return new PositionalFormatResolution
                {
                    Format = formatoDoContrato,
                    Source = PositionalFormatSource.LayoutMetadata,
                    LayoutName = layout?.Name,
                    LayoutGuid = layout?.LayoutGuid,
                    ContentDivergenceReason = DetectarDivergencia(formatoDoContrato, rawText, lineLength)
                };
            }

            // 2. Fallback deprecado: heurística de conteúdo (o comportamento de hoje).
            if (PareceRecordPerLine(rawText))
            {
                return new PositionalFormatResolution
                {
                    Format = PositionalFormat.RecordPerLine,
                    Source = PositionalFormatSource.LegacyContentHeuristic,
                    LayoutName = layout?.Name,
                    LayoutGuid = layout?.LayoutGuid
                };
            }

            // 3. Fallback deprecado: sem sinal nenhum, mantém o comportamento histórico.
            return new PositionalFormatResolution
            {
                Format = PositionalFormat.ContinuousStream,
                Source = PositionalFormatSource.LegacyDefault,
                LayoutName = layout?.Name,
                LayoutGuid = layout?.LayoutGuid
            };
        }

        /// <summary>
        /// Heurística legada, preservada com o MESMO critério do código anterior à ADR-001
        /// (<c>ParseTextWithSequenceValidation</c>): documento que começa com um segmento IDOC.
        /// </summary>
        private static bool PareceRecordPerLine(string? rawText)
        {
            if (string.IsNullOrEmpty(rawText))
                return false;

            var inicio = rawText.TrimStart();

            return inicio.StartsWith("EDI_DC40", StringComparison.OrdinalIgnoreCase)
                || inicio.StartsWith("ZRSDM_NFE", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Validação cruzada (ADR-001 §5.3): torna VISÍVEL a contradição entre o contrato e o dado.
        /// Não corrige nada — um layout com <c>WithBreakLines</c> errado precisa ser consertado na
        /// origem, não mascarado no runtime.
        /// </summary>
        private static string? DetectarDivergencia(PositionalFormat formato, string? rawText, int lineLength)
        {
            if (string.IsNullOrEmpty(rawText))
                return null;

            bool temQuebras = rawText.IndexOf('\n') >= 0;

            if (formato == PositionalFormat.RecordPerLine)
            {
                // RecordPerLine sem nenhuma quebra de linha = um único registro gigante. Suspeito.
                if (!temQuebras)
                    return "layout declara RecordPerLine mas o documento não tem nenhuma quebra de linha";

                return null;
            }

            // ContinuousStream: quebras só são esperadas em múltiplos exatos do tamanho de linha.
            if (!temQuebras || lineLength <= 0)
                return null;

            var texto = rawText.TrimEnd('\r', '\n');

            for (int i = 0; i < texto.Length; i++)
            {
                if (texto[i] != '\n')
                    continue;

                // Posição da quebra contando só os chars de dado que a antecedem no bloco.
                int posicaoNoStream = i - ContarCaracteresDeQuebraAte(texto, i);
                if (posicaoNoStream % lineLength != 0)
                    return $"layout declara ContinuousStream mas há quebra de linha fora de múltiplo de {lineLength} (posição {posicaoNoStream})";
            }

            return null;
        }

        private static int ContarCaracteresDeQuebraAte(string texto, int limite)
        {
            int total = 0;
            for (int i = 0; i < limite; i++)
                if (texto[i] == '\n' || texto[i] == '\r')
                    total++;

            return total;
        }
    }

    /// <summary>
    /// Resultado da resolução de formato: além do formato, carrega a PROCEDÊNCIA da decisão —
    /// é isso que permite medir a migração dos layouts legados.
    /// </summary>
    public class PositionalFormatResolution
    {
        public PositionalFormat Format { get; init; }

        public PositionalFormatSource Source { get; init; }

        public string? LayoutName { get; init; }

        public string? LayoutGuid { get; init; }

        /// <summary>
        /// Preenchido quando o conteúdo contradiz o formato declarado no layout. Sinaliza layout
        /// com <c>WithBreakLines</c> errado — não altera a decisão.
        /// </summary>
        public string? ContentDivergenceReason { get; init; }

        /// <summary>
        /// <c>true</c> quando a decisão NÃO veio do layout — ou seja, o layout precisa receber
        /// <c>&lt;WithBreakLines&gt;</c> explícito. Enquanto isso for verdade, há dívida de migração.
        /// </summary>
        public bool RequiresLayoutMigration => Source != PositionalFormatSource.LayoutMetadata;

        public bool DivergesFromContent => !string.IsNullOrEmpty(ContentDivergenceReason);
    }
}
