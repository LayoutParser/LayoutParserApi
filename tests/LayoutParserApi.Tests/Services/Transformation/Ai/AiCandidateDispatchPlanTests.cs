using LayoutParserApi.Models.Transformation;
using LayoutParserApi.Services.Transformation.Ai;

namespace LayoutParserApi.Tests.Services.Transformation.Ai
{
    /// <summary>
    /// Issue #40 — regressão da decisão "disparar ou não o pathway IA" em
    /// <c>TransformationExecutionController.ExecuteTransformationCandidates</c>. O desenho aprovado
    /// (docs/architecture/pathway-ia-execute-candidates.md §2.1/§3.2) fecha que a IA dispara sempre
    /// que o pathway sysmiddle já produziu um gabarito — não é um fallback condicionado ao tcl-xsl
    /// estar vazio.
    /// </summary>
    public class AiCandidateDispatchPlanTests
    {
        private static readonly Guid CatalogLayoutGuid = Guid.Parse("11111111-1111-1111-1111-111111111111");

        [Fact]
        public void Sysmiddle_produziu_candidato_dispara_IA_mesmo_com_tclxsl_tambem_bem_sucedido()
        {
            var candidates = new List<TransformationCandidate>
            {
                new() { CandidateId = "sysmiddle-mapper-abc", Pathway = "sysmiddle", TransformedXml = "<nfe>ok</nfe>" },
                new() { CandidateId = "tclxsl-1", Pathway = "tcl-xsl", TransformedXml = "<nfe>outro</nfe>" }
            };

            var plan = AiCandidateDispatchPlan.TryBuild(
                requestLayoutGuid: null,
                catalogLayoutGuid: CatalogLayoutGuid,
                inputContent: "linha-posicional-de-teste",
                isXmlInput: false,
                candidates: candidates);

            Assert.NotNull(plan);
            Assert.Equal("mapper-abc", plan!.MapperGuid);
            Assert.Equal("<nfe>ok</nfe>", plan.GroundTruthXml);
            Assert.Equal(CatalogLayoutGuid, plan.LayoutGuid);
            Assert.False(string.IsNullOrWhiteSpace(plan.Ticket));
        }

        [Fact]
        public void Sem_candidato_sysmiddle_bem_sucedido_nao_dispara_IA()
        {
            // tcl-xsl sozinho não é gabarito (§2.1 do desenho: gabarito é SEMPRE sysmiddle).
            var candidates = new List<TransformationCandidate>
            {
                new() { CandidateId = "tclxsl-1", Pathway = "tcl-xsl", TransformedXml = "<nfe>outro</nfe>" }
            };

            var plan = AiCandidateDispatchPlan.TryBuild(
                requestLayoutGuid: null,
                catalogLayoutGuid: CatalogLayoutGuid,
                inputContent: "linha-posicional-de-teste",
                isXmlInput: false,
                candidates: candidates);

            Assert.Null(plan);
        }

        [Fact]
        public void Nenhum_candidato_nao_dispara_IA()
        {
            var plan = AiCandidateDispatchPlan.TryBuild(
                requestLayoutGuid: null,
                catalogLayoutGuid: CatalogLayoutGuid,
                inputContent: "linha-posicional-de-teste",
                isXmlInput: false,
                candidates: new List<TransformationCandidate>());

            Assert.Null(plan);
        }

        [Fact]
        public void Entrada_XML_nao_dispara_IA_mesmo_com_candidato_sysmiddle_presente()
        {
            // Sysmiddle só roda sobre TXT — um candidato "sysmiddle" com entrada XML não deveria
            // existir na prática, mas a decisão não deve confiar cegamente nisso: defesa em profundidade.
            var candidates = new List<TransformationCandidate>
            {
                new() { CandidateId = "sysmiddle-mapper-abc", Pathway = "sysmiddle", TransformedXml = "<nfe>ok</nfe>" }
            };

            var plan = AiCandidateDispatchPlan.TryBuild(
                requestLayoutGuid: null,
                catalogLayoutGuid: CatalogLayoutGuid,
                inputContent: "<nfe/>",
                isXmlInput: true,
                candidates: candidates);

            Assert.Null(plan);
        }

        [Fact]
        public void Candidato_sysmiddle_sem_xml_transformado_nao_dispara_IA()
        {
            var candidates = new List<TransformationCandidate>
            {
                new() { CandidateId = "sysmiddle-mapper-abc", Pathway = "sysmiddle", TransformedXml = "" }
            };

            var plan = AiCandidateDispatchPlan.TryBuild(
                requestLayoutGuid: null,
                catalogLayoutGuid: CatalogLayoutGuid,
                inputContent: "linha-posicional-de-teste",
                isXmlInput: false,
                candidates: candidates);

            Assert.Null(plan);
        }
    }
}
