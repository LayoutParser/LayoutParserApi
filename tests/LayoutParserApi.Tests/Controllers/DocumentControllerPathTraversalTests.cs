using LayoutParserApi.Controllers;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Controllers
{
    /// <summary>
    /// Regressão do P0 (leitura de arquivo arbitrário). O bug provado na instância viva:
    /// <c>GET /api/document/layout/C:%5CWindows%5Cwin.ini → 200</c> devolvia o win.ini real.
    ///
    /// <para>Estes testes passam o valor JÁ DECODIFICADO que chega na action (o pipeline HTTP
    /// decodifica <c>%5C</c>→<c>\</c>, <c>%2e%2e</c>→<c>..</c> antes do binding), que é onde a defesa
    /// mora. Como o <c>C:\Windows\win.ini</c> EXISTE nesta máquina Windows, antes da correção o
    /// endpoint responderia <c>200</c> com conteúdo — asseverar <c>NotFound</c> prova o fecho.</para>
    /// </summary>
    public class DocumentControllerPathTraversalTests
    {
        private static DocumentController CriarController()
            => new(NullLogger<DocumentController>.Instance, new ConfigurationBuilder().Build());

        // Variações do vetor, todas na forma decodificada que chega na action.
        [Theory]
        [InlineData(@"C:\Windows\win.ini")]        // caminho enraizado — o exploit provado
        [InlineData(@"..\..\appsettings.json")]    // traversal relativo (backslash)
        [InlineData("../../appsettings.json")]     // traversal relativo (forward slash)
        [InlineData(@"..\..\..\Windows\win.ini")]
        public void GetLayout_recusa_traversal_com_404(string fileName)
        {
            var resultado = CriarController().GetLayout(fileName);

            // NÃO pode ser Ok (200 com conteúdo). É 404 e não vaza se o arquivo existe.
            Assert.IsType<NotFoundObjectResult>(resultado);
        }

        [Theory]
        [InlineData(@"C:\Windows\win.ini")]
        [InlineData(@"..\..\appsettings.json")]
        [InlineData("../../appsettings.json")]
        public void GetDocument_recusa_traversal_com_404(string fileName)
        {
            Assert.IsType<NotFoundObjectResult>(CriarController().GetDocument(fileName));
        }

        [Theory]
        [InlineData(@"C:\Windows\win.ini")]
        [InlineData(@"..\..\appsettings.json")]
        [InlineData("../../appsettings.json")]
        public void GetExcelFile_recusa_traversal_com_404(string fileName)
        {
            // Pré-correção, um caminho enraizado para arquivo existente devolveria FileContentResult.
            Assert.IsType<NotFoundObjectResult>(CriarController().GetExcelFile(fileName));
        }

        /// <summary>
        /// Guard contra falso-positivo: um nome VÁLIDO (sem traversal) que apenas não existe também
        /// devolve 404 — sem exceção, sem 500. Assim o teste acima prova "recusou o traversal", não
        /// "quebrou tudo".
        /// </summary>
        [Fact]
        public void GetLayout_nome_valido_inexistente_e_404_sem_erro()
        {
            var resultado = CriarController().GetLayout("layout-que-nao-existe.xml");
            Assert.IsType<NotFoundObjectResult>(resultado);
        }
    }
}
