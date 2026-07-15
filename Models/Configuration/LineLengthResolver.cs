using LayoutParserApi.Models.Entities;

namespace LayoutParserApi.Models.Configuration
{
    /// <summary>
    /// Resolver único de tamanho de linha por layout (fonte única de verdade estrutural).
    ///
    /// Precedência de resolução:
    /// 1. <c>Layout.LimitOfCaracters</c> quando &gt; 0 — fonte primária, vinda do XML do Connect Us.
    ///    Cresce sozinha para layouts novos bem cadastrados.
    /// 2. <see cref="LayoutLineSizeConfiguration.GetLineSizeForLayout"/> — allowlist manual por LayoutGuid,
    ///    override para dado sabidamente sujo. Existe porque há layout REAL no Connect Us com
    ///    <c>&lt;LimitOfCaracters&gt;0&lt;/LimitOfCaracters&gt;</c> (verificado no G0 — metadado zerado é
    ///    problema estrutural conhecido da base, não hipótese).
    /// 3. <c>null</c> — layout desconhecido: SEM validação estrutural de tamanho de linha
    ///    (mesmo comportamento de hoje para layouts fora da allowlist).
    ///
    /// Para call-sites historicamente incondicionais (que sempre assumiram 600), use
    /// <see cref="ResolveOrDefault"/>, que cai no default legado quando não há dado melhor.
    /// </summary>
    public static class LineLengthResolver
    {
        /// <summary>
        /// Default legado FIAT/MQSeries (600 posições por linha).
        /// Este é o ÚNICO ponto do código onde o literal 600 sobrevive como default.
        /// </summary>
        public const int LegacyDefaultLineLength = 600;

        /// <summary>
        /// Resolve o tamanho de linha esperado a partir do objeto <see cref="Layout"/> completo.
        /// Retorna <c>null</c> quando não há fonte confiável (= sem validação estrutural).
        /// </summary>
        public static int? Resolve(Layout? layout)
        {
            return Resolve(layout?.LimitOfCaracters ?? 0, layout?.LayoutGuid);
        }

        /// <summary>
        /// Resolve o tamanho de linha esperado a partir dos dados crus.
        /// </summary>
        /// <param name="limitOfCaracters">Valor de <c>Layout.LimitOfCaracters</c> (0 = não informado).</param>
        /// <param name="layoutGuid">LayoutGuid para consulta na allowlist manual.</param>
        public static int? Resolve(int limitOfCaracters, string? layoutGuid)
        {
            // ✅ Fonte primária: dado do próprio layout no Connect Us
            if (limitOfCaracters > 0)
                return limitOfCaracters;

            // ✅ Override manual (allowlist) para layouts com metadado zerado/sujo
            if (!string.IsNullOrWhiteSpace(layoutGuid))
                return LayoutLineSizeConfiguration.GetLineSizeForLayout(layoutGuid);

            // Sem fonte confiável: sem validação estrutural
            return null;
        }

        /// <summary>
        /// Resolve o tamanho de linha, caindo no default legado (600) quando não há dado melhor.
        /// Use apenas em call-sites que hoje são incondicionais (sempre assumiram 600).
        /// </summary>
        public static int ResolveOrDefault(Layout? layout)
        {
            return Resolve(layout) ?? LegacyDefaultLineLength;
        }
    }
}
