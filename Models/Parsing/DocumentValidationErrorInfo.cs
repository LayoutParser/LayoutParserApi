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

        // ── Identidade no erro (spec-taxonomia-de-falha-do-parse.md §2.1 e §5.1) ────────────────
        //
        // POR QUE ISSO EXISTE: os campos acima descrevem um INTERVALO DE BYTES ("linha 37, colunas
        // 100-140 está errada"). Um dataset rotulado assim não generaliza — noutro documento a
        // mesma tag está em outra posição, e o modelo aprenderia endereço, não semântica.
        //
        // DUAS GRANULARIDADES, e só uma existe hoje. Quem produz estes erros é o
        // DocumentValidationService, que recebe APENAS (texto, tamanho-de-linha-esperado) — nunca
        // vê o Layout. Todos os erros que ele emite são de ENQUADRAMENTO DE LINHA (linha
        // incompleta, linha excede N chars, sequência inválida, HEADER fora da primeira linha),
        // nenhum é escopado a campo. Logo: identidade de REGISTRO é resolvível, identidade de
        // CAMPO não é.

        /// <summary>
        /// Nome do registro/segmento do layout (<c>LineElement.Name</c>) a que o erro pertence.
        /// <c>null</c> quando a linha não casa com nenhum registro do layout — identidade ausente
        /// é preferível a identidade errada.
        /// </summary>
        public string? RecordName { get; set; }

        /// <summary>
        /// Identidade ESTÁVEL do registro (<c>LineElement.ElementGuid</c>, vindo do XML do layout).
        ///
        /// <para>É o que dá à IA um rótulo que <b>generaliza entre documentos</b>: o segmento é
        /// estável, enquanto <see cref="StartPosition"/>/<see cref="EndPosition"/> não generalizam
        /// nada. Sinal grosso (registro, não campo), mas estritamente mais do que tínhamos.</para>
        /// </summary>
        public string? RecordGuid { get; set; }

        /// <summary>
        /// Nome do CAMPO a que o erro pertence. <c>null</c> — e continua nulo por decisão, não por
        /// esquecimento: não existe validação escopada a campo (ver bloco acima).
        /// </summary>
        public string? FieldName { get; set; }

        /// <summary>
        /// Identidade estável do CAMPO. <c>null</c> até existir validação escopada a campo.
        ///
        /// <para><b>Não preencha isto com o GUID do registro</b> — a tentação óbvia, e uma
        /// armadilha: o campo diria "campo" e o conteúdo seria "registro". Um dataset assim ensina
        /// à IA que a granularidade da atribuição é o segmento, e quem consumir depois não tem como
        /// saber que o rótulo mente. Use <see cref="RecordGuid"/> para identidade de registro.
        /// Nulo é honesto; mal rotulado é armadilha (spec §5.1).</para>
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
