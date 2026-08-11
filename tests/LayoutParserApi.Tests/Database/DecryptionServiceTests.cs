using LayoutParserApi.Services.Database;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Database
{
    /// <summary>
    /// Trava o P1.1: descriptografia que NÃO ocorreu tem que FALHAR explícito, nunca devolver a
    /// cifra como se fosse texto claro. Antes, com o .exe ausente, o método retornava a entrada
    /// cifrada — o catálogo voltava vazio/parcial com 200 e o operador concluía "não há layouts".
    /// </summary>
    public class DecryptionServiceTests
    {
        private static DecryptionService ComExecutavelAusente()
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["LayoutParserDecrypt:Path"] = @"C:\caminho\inexistente\LayoutParserDecrypt.exe"
                })
                .Build();

            return new DecryptionService(NullLogger<DecryptionService>.Instance, config);
        }

        [Fact]
        public void IsDecryptorAvailable_falso_quando_executavel_nao_existe()
        {
            Assert.False(ComExecutavelAusente().IsDecryptorAvailable);
        }

        [Fact]
        public async Task Executavel_ausente_lanca_em_vez_de_ecoar_a_cifra()
        {
            var svc = ComExecutavelAusente();
            const string cifra = "conteudo-cifrado-qualquer";

            var ex = await Assert.ThrowsAsync<DecryptionException>(() => svc.DecryptContentAsync(cifra));

            // A cifra NUNCA pode voltar (nem na mensagem de erro) como se fosse resultado válido.
            Assert.DoesNotContain(cifra, ex.Message);
        }

        [Fact]
        public async Task Entrada_vazia_devolve_vazio_nao_e_falha()
        {
            var svc = ComExecutavelAusente();

            Assert.Equal(string.Empty, await svc.DecryptContentAsync(""));
        }
    }
}
