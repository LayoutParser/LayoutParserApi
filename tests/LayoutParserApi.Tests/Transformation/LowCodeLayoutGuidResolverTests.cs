using LayoutParserApi.Services.Transformation.LowCode;

namespace LayoutParserApi.Tests.Transformation
{
    public class LowCodeLayoutGuidResolverTests
    {
        [Fact]
        public void LayoutGuid_do_parse_com_prefixo_vence_catalogo_vazio()
        {
            const string parsedLayoutGuid = "LAY_4f7c625a-a089-4b42-a874-48da13b0d88b";

            var result = LowCodeLayoutGuidResolver.Resolve(parsedLayoutGuid, Guid.Empty);

            Assert.Equal(parsedLayoutGuid, result);
        }

        [Fact]
        public void Request_vazio_cai_no_layoutGuid_valido_do_catalogo()
        {
            var catalogLayoutGuid = Guid.Parse("f2963184-cc80-4e14-80f7-6d5d81ac9461");

            var result = LowCodeLayoutGuidResolver.Resolve("  ", catalogLayoutGuid);

            Assert.Equal(catalogLayoutGuid.ToString(), result);
        }

        [Fact]
        public void Request_ausente_e_catalogo_vazio_permanecem_inaplicaveis()
        {
            var result = LowCodeLayoutGuidResolver.Resolve(null, Guid.Empty);

            Assert.Null(result);
        }

        [Fact]
        public void Request_invalido_nao_substitui_layoutGuid_valido_do_catalogo()
        {
            var catalogLayoutGuid = Guid.Parse("6d3e994e-38b8-4a16-88f2-f8b37436d2cb");

            var result = LowCodeLayoutGuidResolver.Resolve("LAY_nao-e-guid", catalogLayoutGuid);

            Assert.Equal(catalogLayoutGuid.ToString(), result);
        }
    }
}
