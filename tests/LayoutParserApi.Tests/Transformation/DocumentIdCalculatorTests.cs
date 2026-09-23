using LayoutParserApi.Services.Transformation.LowCode;

namespace LayoutParserApi.Tests.Transformation
{
    /// <summary>
    /// Cobre o contrato do ADR docs/architecture/adr-contrato-correcao-guiada-humano-2026-09-08.md
    /// §4 (Gap 1): DocumentId precisa ser determinístico por (InputContent, resolvedLayoutGuid).
    /// </summary>
    public class DocumentIdCalculatorTests
    {
        [Fact]
        public void Mesmo_conteudo_e_layoutGuid_produz_sempre_o_mesmo_DocumentId()
        {
            var first = DocumentIdCalculator.Calculate("conteudo-do-documento", "LAY_4f7c625a-a089-4b42-a874-48da13b0d88b");
            var second = DocumentIdCalculator.Calculate("conteudo-do-documento", "LAY_4f7c625a-a089-4b42-a874-48da13b0d88b");

            Assert.Equal(first, second);
        }

        [Fact]
        public void DocumentId_tem_prefixo_doc_e_16_caracteres_hex_apos_o_prefixo()
        {
            var documentId = DocumentIdCalculator.Calculate("conteudo", "LAY_4f7c625a-a089-4b42-a874-48da13b0d88b");

            Assert.StartsWith("doc_", documentId);
            Assert.Equal(20, documentId.Length); // "doc_" (4) + 16 hex chars
            Assert.Matches("^doc_[0-9a-f]{16}$", documentId);
        }

        [Fact]
        public void InputContent_diferente_produz_DocumentId_diferente()
        {
            var first = DocumentIdCalculator.Calculate("conteudo-A", "LAY_4f7c625a-a089-4b42-a874-48da13b0d88b");
            var second = DocumentIdCalculator.Calculate("conteudo-B", "LAY_4f7c625a-a089-4b42-a874-48da13b0d88b");

            Assert.NotEqual(first, second);
        }

        [Fact]
        public void LayoutGuid_diferente_produz_DocumentId_diferente()
        {
            var first = DocumentIdCalculator.Calculate("conteudo", "LAY_4f7c625a-a089-4b42-a874-48da13b0d88b");
            var second = DocumentIdCalculator.Calculate("conteudo", "LAY_0000625a-a089-4b42-a874-48da13b0d88b");

            Assert.NotEqual(first, second);
        }
    }
}
