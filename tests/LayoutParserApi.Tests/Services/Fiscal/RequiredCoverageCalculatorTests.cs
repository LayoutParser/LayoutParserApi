using System.Xml;
using System.Xml.Schema;

using LayoutParserApi.Services.Fiscal;

using Xunit;

namespace LayoutParserApi.Tests.Services.Fiscal
{
    /// <summary>
    /// Issue #380 (#198.5) — cobertura ESTÁTICA de destinos obrigatórios do XSD alvo. Constrói o
    /// <see cref="XmlSchemaSet"/> em memória (sem I/O de disco), no mesmo espírito de
    /// <c>FiscalProfileResolverTests</c> (config em memória) — o carregamento real do arquivo XSD
    /// (<c>XsdValidationService.TryLoadSchemaSet</c>) é testado separadamente.
    /// </summary>
    public class RequiredCoverageCalculatorTests
    {
        private const string TargetNamespace = "http://teste.local/nfe";

        private static XmlSchemaSet BuildSchemaSet(string schemaXml)
        {
            var schemaSet = new XmlSchemaSet();
            using var reader = XmlReader.Create(new StringReader(schemaXml));
            var schema = XmlSchema.Read(reader, (_, e) => throw new InvalidOperationException(e.Message));
            schemaSet.Add(schema);
            schemaSet.Compile();
            return schemaSet;
        }

        private const string SimpleSchema = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="http://teste.local/nfe"
                       xmlns:t="http://teste.local/nfe" elementFormDefault="qualified">
              <xs:element name="NFe">
                <xs:complexType>
                  <xs:sequence>
                    <xs:element name="infNFe">
                      <xs:complexType>
                        <xs:sequence>
                          <xs:element name="ide">
                            <xs:complexType>
                              <xs:sequence>
                                <xs:element name="cUF" type="xs:string" minOccurs="1"/>
                                <xs:element name="natOp" type="xs:string" minOccurs="1"/>
                                <xs:element name="obs" type="xs:string" minOccurs="0"/>
                              </xs:sequence>
                            </xs:complexType>
                          </xs:element>
                        </xs:sequence>
                        <xs:attribute name="versao" type="xs:string" use="required"/>
                        <xs:attribute name="Id" type="xs:string" use="optional"/>
                      </xs:complexType>
                    </xs:element>
                  </xs:sequence>
              </xs:complexType>
              </xs:element>
            </xs:schema>
            """;

        [Fact]
        public void Calculate_TargetRefsCobremTudo_Retorna100Porcento()
        {
            var schemaSet = BuildSchemaSet(SimpleSchema);
            var calculator = new RequiredCoverageCalculator();

            var targetRefs = new[]
            {
                "/NFe/infNFe/@versao",
                "/NFe/infNFe/ide/cUF",
                "/NFe/infNFe/ide/natOp",
            };

            var result = calculator.Calculate(schemaSet, "NFe", TargetNamespace, targetRefs);

            Assert.NotNull(result);
            Assert.Equal(100, result!.Percent);
            Assert.Empty(result.Uncovered);
        }

        [Fact]
        public void Calculate_TargetRefsParciais_ListaOsFaltantes()
        {
            var schemaSet = BuildSchemaSet(SimpleSchema);
            var calculator = new RequiredCoverageCalculator();

            var targetRefs = new[] { "/NFe/infNFe/ide/cUF" };

            var result = calculator.Calculate(schemaSet, "NFe", TargetNamespace, targetRefs);

            Assert.NotNull(result);
            Assert.Contains("/NFe/infNFe/@versao", result!.Uncovered);
            Assert.Contains("/NFe/infNFe/ide/natOp", result.Uncovered);
            Assert.DoesNotContain("/NFe/infNFe/ide/cUF", result.Uncovered);
            Assert.DoesNotContain("/NFe/infNFe/ide/obs", result.Uncovered); // opcional — nunca listado.
            Assert.Equal(1.0 / 3.0 * 100, result.Percent, 3); // só cUF coberto de 3 obrigatórios (@versao, cUF, natOp).
        }

        [Fact]
        public void Calculate_TargetRefComIndicePosicional_EhNormalizadoParaComparar()
        {
            var schemaSet = BuildSchemaSet(SimpleSchema);
            var calculator = new RequiredCoverageCalculator();

            // Índice posicional ([1]) não existe no XSD (contrato de tipo, não de instância) — a
            // normalização deve permitir que "cUF[1]" cubra o "cUF" obrigatório do schema.
            var targetRefs = new[] { "/NFe/infNFe/ide/cUF[1]", "/NFe/infNFe/ide/natOp", "/NFe/infNFe/@versao" };

            var result = calculator.Calculate(schemaSet, "NFe", TargetNamespace, targetRefs);

            Assert.NotNull(result);
            Assert.Equal(100, result!.Percent);
        }

        [Fact]
        public void Calculate_SemTargetRefs_TudoFaltante()
        {
            var schemaSet = BuildSchemaSet(SimpleSchema);
            var calculator = new RequiredCoverageCalculator();

            var result = calculator.Calculate(schemaSet, "NFe", TargetNamespace, Array.Empty<string>());

            Assert.NotNull(result);
            Assert.Equal(0, result!.Percent);
            Assert.Equal(3, result.Uncovered.Count); // @versao, cUF, natOp.
        }

        [Fact]
        public void Calculate_ElementoRaizNaoExisteNoSchema_RetornaNull()
        {
            var schemaSet = BuildSchemaSet(SimpleSchema);
            var calculator = new RequiredCoverageCalculator();

            var result = calculator.Calculate(schemaSet, "CTe", TargetNamespace, Array.Empty<string>());

            Assert.Null(result);
        }

        private const string SchemaSemObrigatorios = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="http://teste.local/nfe"
                       xmlns:t="http://teste.local/nfe" elementFormDefault="qualified">
              <xs:element name="NFe">
                <xs:complexType>
                  <xs:sequence>
                    <xs:element name="obs" type="xs:string" minOccurs="0"/>
                  </xs:sequence>
                </xs:complexType>
              </xs:element>
            </xs:schema>
            """;

        [Fact]
        public void Calculate_XsdSemElementosObrigatorios_Retorna100PorcentoSemUncovered()
        {
            var schemaSet = BuildSchemaSet(SchemaSemObrigatorios);
            var calculator = new RequiredCoverageCalculator();

            var result = calculator.Calculate(schemaSet, "NFe", TargetNamespace, Array.Empty<string>());

            Assert.NotNull(result);
            Assert.Equal(100, result!.Percent);
            Assert.Empty(result.Uncovered);
        }

        private const string SchemaComChoice = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="http://teste.local/nfe"
                       xmlns:t="http://teste.local/nfe" elementFormDefault="qualified">
              <xs:element name="NFe">
                <xs:complexType>
                  <xs:choice>
                    <xs:element name="opcaoA" type="xs:string"/>
                    <xs:element name="opcaoB" type="xs:string"/>
                  </xs:choice>
                </xs:complexType>
              </xs:element>
            </xs:schema>
            """;

        [Fact]
        public void Calculate_ChoiceNaoForcaNenhumRamoComoObrigatorio()
        {
            var schemaSet = BuildSchemaSet(SchemaComChoice);
            var calculator = new RequiredCoverageCalculator();

            var result = calculator.Calculate(schemaSet, "NFe", TargetNamespace, Array.Empty<string>());

            Assert.NotNull(result);
            Assert.Equal(100, result!.Percent); // nenhum required — choice é conservador.
            Assert.Empty(result.Uncovered);
        }
    }
}
