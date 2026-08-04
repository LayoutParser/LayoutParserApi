using System.Text;
using System.Xml;

using LayoutParserApi.Models.Enums;
using LayoutParserApi.Models.Parsing;

namespace LayoutParserApi.Tests.Parsing
{
    /// <summary>
    /// Taxonomia de falha do parse (<c>docs/architecture/spec-taxonomia-de-falha-do-parse.md</c> §3).
    ///
    /// <para>O que está sendo protegido aqui não é "o código roda": é a REGRA DE ATRIBUIÇÃO DE
    /// CULPA. Antes desta taxonomia, bug nosso e arquivo quebrado saíam como o mesmo 422 com uma
    /// string, então o usuário era mandado caçar defeito em arquivo bom e o sinal de que TEMOS um
    /// bug sumia. Uma inversão silenciosa de qualquer ramo abaixo restaura exatamente esse
    /// problema — por isso cada ramo tem caso próprio, incluindo o default.</para>
    /// </summary>
    public class ParseFailureTaxonomyTests
    {
        // ── Entrada ruim → 422 ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// O único XML lido no fluxo de parse é o do LAYOUT (o documento posicional é texto puro).
        /// Layout ilegível ⇒ aponta o layout, não o arquivo de dados do usuário.
        /// </summary>
        [Fact]
        public void Xml_malformado_e_falha_de_layout_nao_defeito_nosso()
        {
            var causa = ParseFailure.Classify(new XmlException("'<' inesperado na linha 3"));

            Assert.Equal(ParseFailureCause.LayoutInvalid, causa);
            Assert.Equal(422, ParseFailure.ToHttpStatusCode(causa));
        }

        /// <summary>Subtipo de XmlException também classifica como layout — o switch é por tipo,
        /// não por igualdade exata; um <c>is</c> trocado por comparação de tipo quebraria aqui.</summary>
        [Fact]
        public void Subtipo_de_XmlException_ainda_e_falha_de_layout()
        {
            Assert.Equal(ParseFailureCause.LayoutInvalid, ParseFailure.Classify(new XmlExceptionDerivada()));
        }

        [Fact]
        public void Encoding_invalido_e_falha_do_documento()
        {
            var causa = ParseFailure.Classify(new DecoderFallbackException("byte inválido"));

            Assert.Equal(ParseFailureCause.DocumentMalformed, causa);
            Assert.Equal(422, ParseFailure.ToHttpStatusCode(causa));
        }

        // ── Qualquer outra → 500 (o default culpa a NÓS) ────────────────────────────────────────

        public static TheoryData<Exception> ExcecoesNaoCatalogadas() => new()
        {
            new NullReferenceException(),
            new IndexOutOfRangeException(),
            new ArgumentOutOfRangeException("startIndex"),
            new InvalidOperationException(),
            new FormatException(),
            // Exception CRUA: o parser tem um `throw new Exception("XML root é nulo")`. Tipo
            // genérico é indistinguível de falha aleatória — cai no default, que culpa a nós.
            new Exception("XML root é nulo")
        };

        [Theory]
        [MemberData(nameof(ExcecoesNaoCatalogadas))]
        public void Excecao_nao_catalogada_e_defeito_nosso_com_500(Exception excecao)
        {
            var causa = ParseFailure.Classify(excecao);

            Assert.Equal(ParseFailureCause.ParserDefect, causa);
            Assert.Equal(500, ParseFailure.ToHttpStatusCode(causa));
        }

        /// <summary>
        /// Falha SEM exceção registrada (ex.: <c>Layout</c> nulo com <c>Success=true</c>) também é
        /// nossa. É o coração da regra: na dúvida, a culpa é nossa.
        /// </summary>
        [Fact]
        public void Falha_sem_excecao_e_defeito_nosso()
        {
            Assert.Equal(ParseFailureCause.ParserDefect, ParseFailure.Classify(null));
        }

        // ── Contrato de wire (o front-end coda contra estes literais) ───────────────────────────

        [Theory]
        [InlineData(ParseFailureCause.DocumentMalformed, "document_malformed", 422)]
        [InlineData(ParseFailureCause.LayoutInvalid, "layout_invalid", 422)]
        [InlineData(ParseFailureCause.ParserDefect, "parser_defect", 500)]
        public void Codigo_de_wire_e_status_sao_os_do_contrato(ParseFailureCause causa, string codigoEsperado, int statusEsperado)
        {
            Assert.Equal(codigoEsperado, ParseFailure.ToWireCode(causa));
            Assert.Equal(statusEsperado, ParseFailure.ToHttpStatusCode(causa));
        }

        // ── Mensagem segura no 500 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// O motivo interno carrega texto de exceção (e, se alguém mudar a origem, pode carregar
        /// stack). No <c>parser_defect</c> ele é DESCARTADO — o cliente recebe literal fixo e o
        /// detalhe fica no log, alcançável pelo correlationId.
        /// </summary>
        [Fact]
        public void Mensagem_do_500_descarta_o_motivo_interno()
        {
            const string motivoInterno =
                "Erro no parsing: Object reference not set to an instance of an object.\n   em LayoutParserService.ParseLineFields()";

            var mensagem = ParseFailure.ResolveClientMessage(ParseFailureCause.ParserDefect, motivoInterno);

            Assert.Equal(ParseFailure.ParserDefectSafeMessage, mensagem);
            Assert.DoesNotContain("Object reference", mensagem, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("LayoutParserService", mensagem, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("   em ", mensagem, StringComparison.Ordinal);
        }

        /// <summary>
        /// No 422 o motivo real É a informação útil (o usuário precisa saber o que há de errado no
        /// arquivo dele). Não regride o comportamento introduzido em <c>7f54e28</c>.
        /// </summary>
        [Fact]
        public void Mensagem_do_422_preserva_o_motivo_real()
        {
            const string motivo = "Erro no parsing: 'x' é um caractere inesperado na linha 3.";

            Assert.Equal(motivo, ParseFailure.ResolveClientMessage(ParseFailureCause.LayoutInvalid, motivo));
            Assert.Equal(motivo, ParseFailure.ResolveClientMessage(ParseFailureCause.DocumentMalformed, motivo));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Mensagem_do_422_sem_motivo_cai_no_fallback_generico(string? motivo)
        {
            Assert.Equal(
                ParseFailure.GenericClientMessage,
                ParseFailure.ResolveClientMessage(ParseFailureCause.LayoutInvalid, motivo));
        }

        private sealed class XmlExceptionDerivada : XmlException
        {
        }
    }
}
