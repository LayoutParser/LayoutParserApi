using LayoutParserApi.Models.Parsing;

namespace LayoutParserApi.Tests.Parsing
{
    /// <summary>
    /// <c>documentHealth</c> é o campo que faz a UI escolher entre "documento limpo" e "documento
    /// anotado". Se ele disser <c>clean</c> num documento com defeito, o erro existe no payload mas
    /// nunca é mostrado — o defeito fica invisível, que é pior do que não tê-lo detectado.
    /// </summary>
    public class DocumentHealthTests
    {
        [Fact]
        public void Sem_erros_o_documento_e_limpo()
        {
            Assert.Equal("clean", DocumentHealth.Resolve(new List<DocumentValidationErrorInfo>()));
        }

        [Fact]
        public void Lista_nula_e_limpo()
        {
            Assert.Equal("clean", DocumentHealth.Resolve(null));
        }

        [Fact]
        public void Um_unico_erro_ja_marca_o_documento_como_defeituoso()
        {
            var erros = new List<DocumentValidationErrorInfo>
            {
                new() { LineIndex = 37, Sequence = "000037", ErrorMessage = "Linha excede 600 caracteres" }
            };

            Assert.Equal("has_defects", DocumentHealth.Resolve(erros));
        }

        /// <summary>Literais são contrato com o front — renomear quebra o outro lado.</summary>
        [Fact]
        public void Literais_sao_os_do_contrato()
        {
            Assert.Equal("clean", DocumentHealth.Clean);
            Assert.Equal("has_defects", DocumentHealth.HasDefects);
        }

        /// <summary>
        /// Identidade de campo (item 3): os campos existem no contrato e chegam NULOS enquanto o
        /// validador não souber resolver o elemento do layout. O front trata como opcionais.
        /// </summary>
        [Fact]
        public void Erro_de_validacao_nasce_sem_identidade_de_campo()
        {
            var erro = new DocumentValidationErrorInfo { LineIndex = 37 };

            Assert.Null(erro.FieldName);
            Assert.Null(erro.FieldGuid);
            Assert.Null(erro.TargetXPath);
        }
    }
}
