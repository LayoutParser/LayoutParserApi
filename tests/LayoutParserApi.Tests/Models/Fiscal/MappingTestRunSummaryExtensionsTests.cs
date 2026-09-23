using LayoutParserApi.Models.Entities.Fiscal;

using Xunit;

namespace LayoutParserApi.Tests.Models.Fiscal
{
    /// <summary>
    /// Diff granular por regra (issue #367 / LayoutParserReact #228) —
    /// <see cref="MappingTestRunSummaryExtensions.GroupDivergencesByRule"/> agrupa
    /// <see cref="MappingTestRunSummary.Divergences"/> por <c>ruleId</c> sem alterar o agregado por
    /// release já consumido pelo front.
    /// </summary>
    public class MappingTestRunSummaryExtensionsTests
    {
        [Fact]
        public void GroupDivergencesByRule_MultiplasDivergenciasMesmaRegra_AgrupaSobAMesmaChave()
        {
            var ruleId = Guid.NewGuid();
            var evidence = new[] { new MappingDraftRuleEvidence("sample", "linha 12") };
            var summary = new MappingTestRunSummary(
                Passed: 0, Failed: 1, CoveragePercent: 100, RequiredGatesPassed: false, XsdValid: true,
                XsdErrors: Array.Empty<string>(),
                Divergences: new[]
                {
                    new MappingTestRunDivergence("changed", "/dest/cnpj", "111", "222", ruleId, new[] { "/nfe/emit/CNPJ" }, evidence),
                    new MappingTestRunDivergence("changed", "/dest/cnpj/@tipo", "A", "B", ruleId, new[] { "/nfe/emit/CNPJ" }, evidence),
                });

            var grouped = summary.GroupDivergencesByRule();

            var group = Assert.Single(grouped);
            Assert.Equal(ruleId, group.RuleId);
            Assert.Equal(new[] { "/nfe/emit/CNPJ" }, group.SourceRefs);
            Assert.Equal(2, group.Diffs.Count);
        }

        [Fact]
        public void GroupDivergencesByRule_DivergenciaSemProvenanceResolvida_FicaDeForaDoAgrupamento()
        {
            var ruleId = Guid.NewGuid();
            var summary = new MappingTestRunSummary(
                Passed: 0, Failed: 1, CoveragePercent: 0, RequiredGatesPassed: false, XsdValid: true,
                XsdErrors: Array.Empty<string>(),
                Divergences: new[]
                {
                    new MappingTestRunDivergence("added", "/dest/extra", null, "valor", ruleId, new[] { "/nfe/x" }, null),
                    new MappingTestRunDivergence("removed", "/dest/semRegra", "valor", null, null, null, null),
                });

            var grouped = summary.GroupDivergencesByRule();

            var group = Assert.Single(grouped);
            Assert.Equal(ruleId, group.RuleId);
            Assert.Single(group.Diffs);
        }

        [Fact]
        public void GroupDivergencesByRule_SemDivergencias_RetornaVazio()
        {
            var summary = new MappingTestRunSummary(
                Passed: 1, Failed: 0, CoveragePercent: 100, RequiredGatesPassed: true, XsdValid: true,
                XsdErrors: Array.Empty<string>(), Divergences: Array.Empty<MappingTestRunDivergence>());

            Assert.Empty(summary.GroupDivergencesByRule());
        }
    }
}
