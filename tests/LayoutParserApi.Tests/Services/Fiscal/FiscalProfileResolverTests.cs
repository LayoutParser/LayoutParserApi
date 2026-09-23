using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Services.Fiscal;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace LayoutParserApi.Tests.Services.Fiscal
{
    /// <summary>
    /// Issue #379 (ADR docs/architecture/adr-perfil-fiscal-draft-release-2026-09-10.md §2.4) —
    /// cascata de validação do <c>FiscalProfile</c> contra <c>XsdValidation:DocumentTypes</c>. Cada
    /// motivo de 422 é testado isoladamente (DoD da issue). Config em memória (sem I/O de disco) —
    /// o caso "arquivo XSD existe sob BasePath" é coberto separadamente com BasePath ausente
    /// (degrada para "não instalado", nunca lança).
    /// </summary>
    public class FiscalProfileResolverTests
    {
        private static IFiscalProfileResolver BuildResolver(params (string Key, string Value)[] configEntries)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(configEntries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
                .Build();
            return new FiscalProfileResolver(configuration, NullLogger<FiscalProfileResolver>.Instance);
        }

        private static IFiscalProfileResolver BuildResolverWithNfe() => BuildResolver(
            ("XsdValidation:DocumentTypes:NFe:XsdVersion", "PL_010b_NT2025_002_v1.30"),
            ("XsdValidation:DocumentTypes:NFe:Namespace", "http://www.portalfiscal.inf.br/nfe"),
            ("XsdValidation:DocumentTypes:NFe:RootElement", "NFe"));

        [Fact]
        public void Validate_DocumentTypeForaDoEnum_Retorna422EspecificoDeDocumentType()
        {
            var resolver = BuildResolverWithNfe();
            var result = resolver.Validate(new FiscalProfile("XML_INVALIDO", "v1", FiscalOperation.Outbound, "SP"));

            Assert.False(result.IsValid);
            Assert.Contains("documentType", result.Error);
        }

        [Fact]
        public void Validate_DocumentTypeValidoSemEntradaNoAppsettings_Retorna422DeConfigAusente()
        {
            // Config não tem NENHUMA entrada de DocumentTypes — CTe é válido no enum, mas sem XSD configurado.
            var resolver = BuildResolver();
            var result = resolver.Validate(new FiscalProfile(FiscalDocumentType.Cte, "PL_CTe_300", FiscalOperation.Outbound, "SP"));

            Assert.False(result.IsValid);
            Assert.Contains("sem XSD configurado", result.Error);
        }

        [Fact]
        public void Validate_SchemaVersionDivergenteSemArquivoNoDisco_Retorna422DeVersaoNaoInstalada()
        {
            var resolver = BuildResolverWithNfe();
            var result = resolver.Validate(new FiscalProfile(FiscalDocumentType.Nfe, "versao-inexistente", FiscalOperation.Outbound, "SP"));

            Assert.False(result.IsValid);
            Assert.Contains("não instalada", result.Error);
        }

        [Fact]
        public void Validate_OperationForaDoEnum_Retorna422DeOperation()
        {
            var resolver = BuildResolverWithNfe();
            var result = resolver.Validate(new FiscalProfile(FiscalDocumentType.Nfe, "PL_010b_NT2025_002_v1.30", "operacao-invalida", "SP"));

            Assert.False(result.IsValid);
            Assert.Contains("operation", result.Error);
        }

        [Fact]
        public void Validate_JurisdictionForaDaListaDeUfs_Retorna422DeJurisdiction()
        {
            var resolver = BuildResolverWithNfe();
            var result = resolver.Validate(new FiscalProfile(FiscalDocumentType.Nfe, "PL_010b_NT2025_002_v1.30", FiscalOperation.Outbound, "ZZ"));

            Assert.False(result.IsValid);
            Assert.Contains("jurisdiction", result.Error);
        }

        [Fact]
        public void Validate_JurisdictionFederalBR_EhAceita()
        {
            var resolver = BuildResolverWithNfe();
            var result = resolver.Validate(new FiscalProfile(FiscalDocumentType.Nfe, "PL_010b_NT2025_002_v1.30", FiscalOperation.Outbound, FiscalJurisdiction.Federal));

            Assert.True(result.IsValid);
        }

        [Fact]
        public void Validate_PerfilTotalmenteValido_RetornaResolvedXsdEcoDoAppsettings()
        {
            var resolver = BuildResolverWithNfe();
            var result = resolver.Validate(new FiscalProfile(FiscalDocumentType.Nfe, "PL_010b_NT2025_002_v1.30", FiscalOperation.Outbound, "SP"));

            Assert.True(result.IsValid);
            Assert.Null(result.Error);
            Assert.NotNull(result.ResolvedXsd);
            Assert.Equal("PL_010b_NT2025_002_v1.30", result.ResolvedXsd!.XsdVersion);
            Assert.Equal("http://www.portalfiscal.inf.br/nfe", result.ResolvedXsd.Namespace);
            Assert.Equal("NFe", result.ResolvedXsd.RootElement);
        }

        [Fact]
        public void Resolve_DocumentTypeComEntradaConfigurada_DevolveEcoDerivado()
        {
            var resolver = BuildResolverWithNfe();
            var resolved = resolver.Resolve(FiscalDocumentType.Nfe, "PL_010b_NT2025_002_v1.30");

            Assert.NotNull(resolved);
            Assert.Equal("NFe", resolved!.RootElement);
        }

        [Fact]
        public void Resolve_DocumentTypeSemEntradaConfigurada_DevolveNull()
        {
            var resolver = BuildResolver();
            var resolved = resolver.Resolve(FiscalDocumentType.Cte, "qualquer-versao");

            Assert.Null(resolved);
        }
    }
}
