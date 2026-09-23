using LayoutParserApi.Models.Database;

using System.Xml.Linq;

namespace LayoutParserApi.Services.XmlAnalysis
{
    /// <summary>
    /// Resolve o tipo efetivo de um layout ("TextPositional"/"XML") a partir do valor cru vindo da
    /// coluna SQL [LayoutType] (<see cref="LayoutRecord.LayoutType"/>), com fallback defensivo.
    ///
    /// ✅ Issue #219 (gate FIAT recusando `LAY_TXT_MQSERIES_ENVNFE_4.00_NFe` com
    /// "Tipo de layout não suportado: 2"): o cadastro no banco (`tbLayout.LayoutType`) pode conter
    /// um código numérico legado do Sysmiddle em vez do texto esperado por este serviço. O próprio
    /// <see cref="LayoutParserApi.Services.Database.LayoutDatabaseService"/> já lida com essa
    /// divergência em <c>IsTextPositionalLayout</c>, que ignora a coluna SQL e lê o valor real em
    /// <c>/LayoutVO/LayoutType</c> dentro do XML descriptografado do layout — essa é a fonte
    /// comprovadamente autoritativa (é o que decide hoje se o layout entra no catálogo Redis).
    /// Reaproveitamos a mesma fonte aqui antes de recusar o layout.
    ///
    /// Extraído como classe estática independente (sem dependências de DI) para ser testável sem
    /// precisar construir <see cref="AutoTransformationGeneratorService"/> inteiro.
    /// </summary>
    public static class LayoutTypeNormalizer
    {
        /// <summary>
        /// Ordem de resolução:
        /// 1. `layout.LayoutType` já é "TextPositional"/"XML" (case-insensitive) → usa direto, sem log.
        /// 2. Senão, tenta ler `/LayoutVO/LayoutType` do XML descriptografado (mesma lógica usada por
        ///    `LayoutDatabaseService.IsTextPositionalLayout`) — se vier um valor reconhecido, usa e
        ///    loga Warning via <paramref name="onFallback"/> (a divergência SQL vs XML é sinal de
        ///    cadastro desatualizado, auditável).
        /// 3. Senão, tenta um mapa heurístico de códigos numéricos legados do Sysmiddle. **Este mapa
        ///    não tem confirmação documentada do dono do produto** — hoje só há evidência do caso real
        ///    "2" (layout MQSeries `LAY_TXT_MQSERIES_ENVNFE_4.00_NFe`, issue #219), que decrypta para
        ///    `TextPositional` no XML (isso é validado no passo 2, então o passo 3 só entra se nem o
        ///    conteúdo do XML puder ser lido). Loga Warning explícito pedindo confirmação.
        /// 4. Senão, devolve o valor cru original — cai no warning "não suportado" já existente no
        ///    chamador.
        /// </summary>
        /// <param name="layout">Registro do layout, com <c>LayoutType</c> cru e conteúdo (des)criptografado.</param>
        /// <param name="onFallback">
        /// Callback invocado quando a resolução não usa o valor cru diretamente (passos 2 ou 3), com
        /// uma mensagem pronta para log de Warning — auditabilidade do fallback heurístico.
        /// </param>
        public static string ResolveEffectiveLayoutType(LayoutRecord layout, Action<string>? onFallback = null)
        {
            var rawType = layout.LayoutType?.Trim() ?? "";

            if (string.Equals(rawType, "TextPositional", StringComparison.OrdinalIgnoreCase))
                return "TextPositional";
            if (string.Equals(rawType, "XML", StringComparison.OrdinalIgnoreCase))
                return "XML";

            // Passo 2: tentar a fonte autoritativa — o XML descriptografado do próprio layout.
            var fromXml = TryExtractLayoutTypeFromDecryptedXml(layout);
            if (!string.IsNullOrEmpty(fromXml))
            {
                onFallback?.Invoke(
                    $"Layout {layout.Name} (Guid: {layout.LayoutGuid}) tem LayoutType='{rawType}' cadastrado no banco " +
                    $"(tbLayout.LayoutType), mas o XML descriptografado do layout diz '{fromXml}'. Usando o valor do XML " +
                    "(fonte autoritativa). Cadastro no banco parece desatualizado — considerar corrigir na origem.");
                return fromXml;
            }

            // Passo 3: fallback heurístico para códigos numéricos legados — NÃO CONFIRMADO pelo dono.
            // Único caso com evidência real até agora: "2" em layout MQSeries (issue #219), que na
            // prática decripta para TextPositional (ver passo 2). Mantido aqui só como último recurso
            // para quando o XML não puder ser lido/descriptografado.
            if (rawType == "2")
            {
                onFallback?.Invoke(
                    $"Layout {layout.Name} (Guid: {layout.LayoutGuid}) com LayoutType='2' (código numérico legado) e " +
                    "sem XML legível para confirmar. Assumindo 'TextPositional' por heurística baseada no padrão " +
                    "observado na issue #219 — ESTA SUPOSIÇÃO NÃO FOI CONFIRMADA PELO DONO DO PRODUTO. Corrigir o " +
                    "cadastro do layout na origem (tbLayout.LayoutType) é a correção definitiva.");
                return "TextPositional";
            }

            return rawType;
        }

        /// <summary>
        /// Lê `/LayoutVO/LayoutType` do XML descriptografado do layout, com a mesma lógica de busca
        /// (root == LayoutVO, ou LayoutVO filho, ou LayoutType direto no root) usada por
        /// <see cref="LayoutParserApi.Services.Database.LayoutDatabaseService"/> — mantida aqui
        /// separada para não acoplar este serviço à implementação interna daquele (que expõe só um
        /// bool, não o valor).
        /// </summary>
        internal static string? TryExtractLayoutTypeFromDecryptedXml(LayoutRecord layout)
        {
            var xml = !string.IsNullOrEmpty(layout.DecryptedContent) ? layout.DecryptedContent : layout.ValueContent;
            if (string.IsNullOrEmpty(xml))
                return null;

            try
            {
                var doc = XDocument.Parse(xml);
                var root = doc.Root;
                if (root == null)
                    return null;

                XElement? layoutTypeElement;
                if (root.Name.LocalName == "LayoutVO")
                    layoutTypeElement = root.Element("LayoutType");
                else
                {
                    var layoutVo = root.Element("LayoutVO");
                    layoutTypeElement = layoutVo != null ? layoutVo.Element("LayoutType") : root.Element("LayoutType");
                }

                var value = layoutTypeElement?.Value?.Trim();
                return string.IsNullOrEmpty(value) ? null : value;
            }
            catch
            {
                // XML malformado/vazio: sem sinal confiável, deixa o chamador seguir para o próximo
                // passo de resolução (fallback heurístico ou valor cru original).
                return null;
            }
        }
    }
}
