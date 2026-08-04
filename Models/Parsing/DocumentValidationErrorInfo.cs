namespace LayoutParserApi.Models.Parsing
{
    /// <summary>
    /// Informação de erro de validação do documento (versão simplificada para ParsingResult)
    /// </summary>
    public class DocumentValidationErrorInfo
    {
        public int LineIndex { get; set; }
        public string Sequence { get; set; } = "";
        public int ExpectedLength { get; set; }
        public int ActualLength { get; set; }
        public string ErrorMessage { get; set; } = "";
        public int StartPosition { get; set; }
        public int EndPosition { get; set; }

        // ── Identidade de campo (spec-taxonomia-de-falha-do-parse.md §2.1, item 3) ───────────────
        //
        // POR QUE ISSO EXISTE: os campos acima descrevem um INTERVALO DE BYTES ("linha 37, colunas
        // 100-140 está errada"). Um dataset rotulado assim não generaliza — noutro documento a
        // mesma tag está em outra posição, e o modelo aprenderia endereço, não semântica. Com
        // identidade de campo, cada documento processado vira par rotulado (campo, correto/incorreto).
        //
        // POR QUE ESTÃO NULOS HOJE: quem produz estes erros é DocumentValidationService, que recebe
        // APENAS (texto, tamanho-de-linha-esperado) — nunca vê o Layout. Todos os erros que ele
        // emite são de ENQUADRAMENTO DE LINHA (linha incompleta, linha excede N chars, sequência
        // inválida, HEADER fora da primeira linha), não de campo. Não existe, no dado de hoje, a
        // que elemento do layout o erro pertence. Preencher isso com um palpite ensinaria à IA uma
        // atribuição que o dado não sustenta — pior que deixar nulo.
        //
        // Ver o relatório de diagnóstico do item 3 para o caminho de habilitação.

        /// <summary>
        /// Nome do elemento do layout a que o erro pertence. <c>null</c> enquanto o validador
        /// não souber resolver o elemento (ver bloco acima).
        /// </summary>
        public string? FieldName { get; set; }

        /// <summary>
        /// Identidade estável do campo (<c>ElementGuid</c> do layout) — é o rótulo que serve de
        /// chave para a IA aprender atribuição por tag. <c>null</c> enquanto não resolvível.
        /// </summary>
        public string? FieldGuid { get; set; }

        /// <summary>
        /// Destino do campo no XML de saída. <c>null</c> por ora: depende da linhagem
        /// campo→XPath, lacuna conhecida do projeto (o catálogo GUID→XPath resolve a SAÍDA, não a
        /// entrada). Não bloqueia os demais campos.
        /// </summary>
        public string? TargetXPath { get; set; }
    }
}
