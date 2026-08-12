using LayoutParserApi.Services.Security;

namespace LayoutParserApi.Tests.Security
{
    /// <summary>
    /// Núcleo puro da extração de identidade: a decisão de confiança (a guarda em forma de função) e o
    /// parse do CSV de papéis. Aqui é onde os casos de borda do CSV e da flag ficam travados sem
    /// precisar montar um HttpContext.
    /// </summary>
    public class TrustedIdentityPolicyTests
    {
        [Theory]
        // (isLoopback, trustLoopbackOnly) -> confia?
        [InlineData(true, true, true)]    // loopback + guarda: confia (salto do BFF)
        [InlineData(false, true, false)]  // remoto + guarda: NÃO confia (o vetor fechado)
        [InlineData(true, false, true)]   // loopback + sem guarda: confia
        [InlineData(false, false, true)]  // remoto + sem guarda: confia (guarda desligada)
        public void ShouldTrust_decide_pela_guarda(bool isLoopback, bool trustLoopbackOnly, bool esperado)
        {
            Assert.Equal(esperado, TrustedIdentityPolicy.ShouldTrust(isLoopback, trustLoopbackOnly));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(",")]
        [InlineData(" , , ")]
        public void ParseRoles_vazio_ou_so_separadores_vira_lista_vazia(string? csv)
        {
            Assert.Empty(TrustedIdentityPolicy.ParseRoles(csv));
        }

        [Fact]
        public void ParseRoles_um_papel_so()
        {
            Assert.Equal(new[] { "admin" }, TrustedIdentityPolicy.ParseRoles("admin"));
        }

        [Fact]
        public void ParseRoles_apara_espacos_e_descarta_vazios()
        {
            Assert.Equal(
                new[] { "admin", "operador", "viewer" },
                TrustedIdentityPolicy.ParseRoles(" admin , operador ,, viewer "));
        }

        [Fact]
        public void ParseRoles_preserva_ordem_e_repeticao()
        {
            Assert.Equal(
                new[] { "admin", "operador", "admin" },
                TrustedIdentityPolicy.ParseRoles("admin,operador,admin"));
        }
    }
}
