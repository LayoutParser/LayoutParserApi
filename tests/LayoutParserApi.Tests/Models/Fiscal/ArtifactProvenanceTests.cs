using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Services.Llm;

using Xunit;

namespace LayoutParserApi.Tests.Models.Fiscal
{
    /// <summary>
    /// Issue #341 (F2 do ADR docs/architecture/adr-llm-provider-plugavel-2026-09-08.md): garante que
    /// a AUSÊNCIA de proveniência declarada nunca resolve para <see cref="DataSensitivity.SyntheticOrAnonymized"/>
    /// por omissão — só o valor explícito <see cref="ArtifactProvenance.Synthetic"/> resolve assim.
    /// </summary>
    public class ArtifactProvenanceTests
    {
        [Fact]
        public void ResolveSensitivity_SemValorPreenchido_ResolveComoRealFiscalDocument()
        {
            Assert.Equal(DataSensitivity.RealFiscalDocument, ArtifactProvenance.ResolveSensitivity(null));
        }

        [Fact]
        public void ResolveSensitivity_StringVazia_ResolveComoRealFiscalDocument()
        {
            Assert.Equal(DataSensitivity.RealFiscalDocument, ArtifactProvenance.ResolveSensitivity(string.Empty));
        }

        [Fact]
        public void ResolveSensitivity_ValorDesconhecido_ResolveComoRealFiscalDocument()
        {
            // Valor fora do vocabulário conhecido (typo, versão futura etc.) — fail-closed, nunca sintético por engano.
            Assert.Equal(DataSensitivity.RealFiscalDocument, ArtifactProvenance.ResolveSensitivity("qualquer-coisa"));
        }

        [Fact]
        public void ResolveSensitivity_RealCustomerSampleExplicito_ResolveComoRealFiscalDocument()
        {
            Assert.Equal(DataSensitivity.RealFiscalDocument, ArtifactProvenance.ResolveSensitivity(ArtifactProvenance.RealCustomerSample));
        }

        [Fact]
        public void ResolveSensitivity_SyntheticExplicito_ResolveComoSyntheticOrAnonymized()
        {
            Assert.Equal(DataSensitivity.SyntheticOrAnonymized, ArtifactProvenance.ResolveSensitivity(ArtifactProvenance.Synthetic));
        }

        [Theory]
        [InlineData(ArtifactProvenance.Synthetic, true)]
        [InlineData(ArtifactProvenance.RealCustomerSample, true)]
        [InlineData(null, false)]
        [InlineData("", false)]
        [InlineData("invalido", false)]
        public void IsValid_RespeitaVocabularioExplicito(string? value, bool expected)
        {
            Assert.Equal(expected, ArtifactProvenance.IsValid(value));
        }
    }
}
