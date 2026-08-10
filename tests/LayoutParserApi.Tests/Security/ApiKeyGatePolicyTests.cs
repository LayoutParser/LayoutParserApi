using LayoutParserApi.Services.Filters;

namespace LayoutParserApi.Tests.Security
{
    /// <summary>
    /// Trava o casamento de rota anônima do <see cref="ApiKeyGateFilter"/>.
    ///
    /// <para>É a parte que precisa de teste: a chave em si é comparação de bytes, mas um prefixo
    /// frouxo demais libera endpoint que ninguém queria liberar — e o sintoma é silêncio, porque a
    /// requisição simplesmente passa.</para>
    /// </summary>
    public class ApiKeyGatePolicyTests
    {
        [Fact]
        public void Rota_exatamente_igual_ao_prefixo_e_anonima()
        {
            Assert.True(ApiKeyGatePolicy.RotaBateComAlgumPrefixo("/api/document", new[] { "/api/document" }));
        }

        [Fact]
        public void Rota_abaixo_do_prefixo_e_anonima()
        {
            Assert.True(ApiKeyGatePolicy.RotaBateComAlgumPrefixo("/api/document/layouts", new[] { "/api/document" }));
        }

        /// <summary>
        /// O defeito que a implementação ingênua (StartsWith cru) teria: "/api/document" liberaria
        /// "/api/documentos-confidenciais". Casamento é por SEGMENTO, não por substring.
        /// </summary>
        [Theory]
        [InlineData("/api/documentos-confidenciais")]
        [InlineData("/api/document-interno")]
        [InlineData("/api/documentX")]
        public void Prefixo_nao_vaza_para_rota_de_nome_parecido(string rota)
        {
            Assert.False(ApiKeyGatePolicy.RotaBateComAlgumPrefixo(rota, new[] { "/api/document" }));
        }

        [Fact]
        public void Barra_final_no_prefixo_nao_muda_o_resultado()
        {
            var comBarra = ApiKeyGatePolicy.RotaBateComAlgumPrefixo("/api/document/layouts", new[] { "/api/document/" });
            var semBarra = ApiKeyGatePolicy.RotaBateComAlgumPrefixo("/api/document/layouts", new[] { "/api/document" });

            Assert.True(comBarra);
            Assert.Equal(semBarra, comBarra);
        }

        [Fact]
        public void Comparacao_ignora_caixa()
        {
            Assert.True(ApiKeyGatePolicy.RotaBateComAlgumPrefixo("/API/Document/Layouts", new[] { "/api/document" }));
        }

        /// <summary>
        /// Sem allowlist, NADA é anônimo — o gate ligado tem que fechar por padrão. Um bug aqui
        /// transformaria "ligar a chave" em "achar que ligou".
        /// </summary>
        [Fact]
        public void Lista_ausente_nao_libera_nada()
        {
            Assert.False(ApiKeyGatePolicy.RotaBateComAlgumPrefixo("/api/document/layouts", null));
        }

        [Fact]
        public void Lista_vazia_nao_libera_nada()
        {
            Assert.False(ApiKeyGatePolicy.RotaBateComAlgumPrefixo("/api/document/layouts", Array.Empty<string>()));
        }

        /// <summary>
        /// Entrada vazia na lista é ignorada, não tratada como "prefixo que casa com tudo".
        /// Config em env var (<c>Security__AnonymousPaths__0</c>) erra fácil assim.
        /// </summary>
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Prefixo_vazio_na_lista_nao_libera_a_api_inteira(string prefixoVazio)
        {
            Assert.False(ApiKeyGatePolicy.RotaBateComAlgumPrefixo("/api/parse/upload", new[] { prefixoVazio }));
        }

        [Fact]
        public void Basta_um_prefixo_da_lista_casar()
        {
            var prefixos = new[] { "/api/monitoring", "/api/document", "/swagger" };

            Assert.True(ApiKeyGatePolicy.RotaBateComAlgumPrefixo("/api/document/layouts", prefixos));
            Assert.True(ApiKeyGatePolicy.RotaBateComAlgumPrefixo("/swagger/index.html", prefixos));
            Assert.False(ApiKeyGatePolicy.RotaBateComAlgumPrefixo("/api/parse/upload", prefixos));
        }

        [Fact]
        public void Rota_nula_ou_vazia_nao_e_anonima()
        {
            Assert.False(ApiKeyGatePolicy.RotaBateComAlgumPrefixo(null, new[] { "/api/document" }));
            Assert.False(ApiKeyGatePolicy.RotaBateComAlgumPrefixo("", new[] { "/api/document" }));
        }
    }
}
