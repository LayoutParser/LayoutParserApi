using System.Xml.Linq;

using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Services.Fiscal;

using Xunit;

namespace LayoutParserApi.Tests.Services.Fiscal
{
    /// <summary>
    /// Slice 5 (issue #231) — transpiladores determinísticos MappingDraftRule → XSLT/TCL.
    /// Fixtures sintéticas com campos fiscais reais (CNPJ/CFOP), sem dado de produção.
    /// </summary>
    public class MappingDraftRuleTranspilerTests
    {
        private static readonly SchemaRef Source = new("NFeOrigem", "urn:origem");
        private static readonly SchemaRef Target = new("nfeProc", "urn:destino");

        private static MappingDraftRule Rule(
            string operation,
            string[] sourceRefs,
            string[] targetRefs,
            string status = MappingDraftRuleStatus.Accepted,
            string conditionsJson = "[]",
            string transformationsJson = "[]")
        {
            return new MappingDraftRule
            {
                RuleId = Guid.NewGuid(),
                DraftId = Guid.NewGuid(),
                SourceRefs = sourceRefs,
                TargetRefs = targetRefs,
                Operation = operation,
                Status = status,
                ConditionsJson = conditionsJson,
                TransformationsJson = transformationsJson,
                CreatedAt = DateTimeOffset.UtcNow
            };
        }

        [Fact]
        public void ToXslt_Copy_GeraValueOfESintaticamenteValido()
        {
            var rule = Rule("copy", new[] { "/nfe/emit/CNPJ" }, new[] { "/dest/cnpjEmitente" });

            var result = MappingDraftRuleTranspiler.ToXslt(new[] { rule }, Source, Target);

            Assert.False(result.HasDiagnostics);
            var doc = XDocument.Parse(result.Content); // deve parsear sem exceção — sintaticamente válido
            var valueOf = doc.Descendants().First(e => e.Name.LocalName == "value-of");
            Assert.Equal("/nfe/emit/CNPJ", valueOf.Attribute("select")!.Value);
            AssertRuleIdTraceable(doc, rule.RuleId, "cnpjEmitente");
        }

        [Fact]
        public void ToXslt_Concat_ConcatenaMultiplosSourceRefs()
        {
            var rule = Rule("concat",
                new[] { "/nfe/ide/serie", "/nfe/ide/nNF" },
                new[] { "/dest/chaveResumo" },
                transformationsJson: "[{\"type\":\"concat\",\"separator\":\"-\"}]");

            var result = MappingDraftRuleTranspiler.ToXslt(new[] { rule }, Source, Target);

            Assert.False(result.HasDiagnostics);
            var doc = XDocument.Parse(result.Content);
            var valueOf = doc.Descendants().First(e => e.Name.LocalName == "value-of");
            Assert.Contains("concat(", valueOf.Attribute("select")!.Value);
            Assert.Contains("/nfe/ide/serie", valueOf.Attribute("select")!.Value);
            Assert.Contains("/nfe/ide/nNF", valueOf.Attribute("select")!.Value);
        }

        [Fact]
        public void ToXslt_Lookup_GeraChooseComTabelaDeCfop()
        {
            var rule = Rule("lookup",
                new[] { "/nfe/det/CFOP" },
                new[] { "/dest/tipoOperacao" },
                transformationsJson: "[{\"type\":\"lookup\",\"table\":{\"5102\":\"Venda\",\"1102\":\"Devolucao\"},\"default\":\"Outros\"}]");

            var result = MappingDraftRuleTranspiler.ToXslt(new[] { rule }, Source, Target);

            Assert.False(result.HasDiagnostics);
            var doc = XDocument.Parse(result.Content);
            var whens = doc.Descendants().Where(e => e.Name.LocalName == "when").ToList();
            Assert.Equal(2, whens.Count);
            Assert.Contains(whens, w => w.Attribute("test")!.Value.Contains("5102"));
            var otherwise = doc.Descendants().First(e => e.Name.LocalName == "otherwise");
            Assert.Equal("Outros", otherwise.Value);
        }

        [Fact]
        public void ToXslt_Conditional_GeraChooseComBaseEmConditions()
        {
            var conditions = "[" +
                "{\"testXPath\":\"/nfe/ide/tpNF = '1'\",\"sourceRef\":\"/nfe/ide/dhSaiEnt\"}," +
                "{\"default\":true,\"value\":\"\"}" +
                "]";
            var rule = Rule("conditional", new[] { "/nfe/ide/dhSaiEnt" }, new[] { "/dest/dataSaida" }, conditionsJson: conditions);

            var result = MappingDraftRuleTranspiler.ToXslt(new[] { rule }, Source, Target);

            Assert.False(result.HasDiagnostics);
            var doc = XDocument.Parse(result.Content);
            var when = doc.Descendants().First(e => e.Name.LocalName == "when");
            Assert.Contains("tpNF", when.Attribute("test")!.Value);
        }

        [Fact]
        public void ToXslt_Constant_GeraTextoFixo()
        {
            var rule = Rule("constant", Array.Empty<string>(), new[] { "/dest/versao" },
                transformationsJson: "[{\"type\":\"constant\",\"value\":\"4.00\"}]");

            var result = MappingDraftRuleTranspiler.ToXslt(new[] { rule }, Source, Target);

            Assert.False(result.HasDiagnostics);
            var doc = XDocument.Parse(result.Content);
            var text = doc.Descendants().First(e => e.Name.LocalName == "versao").Elements().First(e => e.Name.LocalName == "text");
            Assert.Equal("4.00", text.Value);
        }

        [Theory]
        [InlineData(MappingDraftRuleStatus.Proposed)]
        [InlineData(MappingDraftRuleStatus.Rejected)]
        [InlineData(MappingDraftRuleStatus.NeedsInput)]
        public void ToXslt_RegraNaoDecidida_NaoAparaceNoOutput(string status)
        {
            var rule = Rule("copy", new[] { "/nfe/emit/CNPJ" }, new[] { "/dest/cnpjEmitente" }, status: status);

            var result = MappingDraftRuleTranspiler.ToXslt(new[] { rule }, Source, Target);

            Assert.False(result.HasDiagnostics);
            Assert.DoesNotContain("cnpjEmitente", result.Content);
        }

        [Fact]
        public void ToXslt_OperacaoNaoSuportada_GeraDiagnosticoSemExcecao()
        {
            var rule = Rule("regex_extract", new[] { "/nfe/x" }, new[] { "/dest/y" });

            var result = MappingDraftRuleTranspiler.ToXslt(new[] { rule }, Source, Target);

            Assert.True(result.HasDiagnostics);
            Assert.Equal(rule.RuleId, result.Diagnostics[0].RuleId);
            Assert.Contains("regex_extract", result.Diagnostics[0].Message);
            var doc = XDocument.Parse(result.Content);
            Assert.DoesNotContain(doc.Descendants(), e => e.Name.LocalName == "y");
        }

        [Fact]
        public void ToXslt_RastreabilidadeRuleId_RecuperaRuleIdDoElementoGerado()
        {
            var rule = Rule("copy", new[] { "/nfe/dest/CNPJ" }, new[] { "/dest/cnpjDestinatario" });

            var result = MappingDraftRuleTranspiler.ToXslt(new[] { rule }, Source, Target);

            var doc = XDocument.Parse(result.Content);
            AssertRuleIdTraceable(doc, rule.RuleId, "cnpjDestinatario");
        }

        // -----------------------------------------------------------------
        // TCL
        // -----------------------------------------------------------------

        [Fact]
        public void ToTcl_Copy_GeraFieldComSourceEOp()
        {
            var rule = Rule("copy", new[] { "/nfe/emit/CNPJ" }, new[] { "/dest/cnpjEmitente" });

            var result = MappingDraftRuleTranspiler.ToTcl(new[] { rule }, Source, Target);

            Assert.False(result.HasDiagnostics);
            var doc = XDocument.Parse(result.Content);
            Assert.Equal("MAP", doc.Root!.Name.LocalName);
            var line = doc.Root.Elements().Single(e => e.Name.LocalName == "LINE");
            var field = line.Elements().Single(e => e.Name.LocalName == "FIELD");
            Assert.Equal("cnpjEmitente", field.Attribute("name")!.Value);
            Assert.Equal("copy", field.Attribute("op")!.Value);
            Assert.Equal("/nfe/emit/CNPJ", field.Attribute("source")!.Value);
            Assert.Equal(rule.RuleId.ToString(), field.Attribute("ruleId")!.Value);
        }

        [Fact]
        public void ToTcl_Lookup_CarregaTabelaDeCfopSerializada()
        {
            var rule = Rule("lookup",
                new[] { "/nfe/det/CFOP" },
                new[] { "/dest/tipoOperacao" },
                transformationsJson: "[{\"type\":\"lookup\",\"table\":{\"5102\":\"Venda\"},\"default\":\"Outros\"}]");

            var result = MappingDraftRuleTranspiler.ToTcl(new[] { rule }, Source, Target);

            Assert.False(result.HasDiagnostics);
            Assert.Contains("5102=Venda", result.Content);
        }

        [Theory]
        [InlineData(MappingDraftRuleStatus.Proposed)]
        [InlineData(MappingDraftRuleStatus.Rejected)]
        [InlineData(MappingDraftRuleStatus.NeedsInput)]
        public void ToTcl_RegraNaoDecidida_NaoAparaceNoOutput(string status)
        {
            var rule = Rule("copy", new[] { "/nfe/emit/CNPJ" }, new[] { "/dest/cnpjEmitente" }, status: status);

            var result = MappingDraftRuleTranspiler.ToTcl(new[] { rule }, Source, Target);

            Assert.False(result.HasDiagnostics);
            Assert.DoesNotContain("cnpjEmitente", result.Content);
        }

        [Fact]
        public void ToTcl_OperacaoNaoSuportada_GeraDiagnosticoSemExcecao()
        {
            var rule = Rule("regex_extract", new[] { "/nfe/x" }, new[] { "/dest/y" });

            var result = MappingDraftRuleTranspiler.ToTcl(new[] { rule }, Source, Target);

            Assert.True(result.HasDiagnostics);
            Assert.Equal(rule.RuleId, result.Diagnostics[0].RuleId);
        }

        private static void AssertRuleIdTraceable(XDocument doc, Guid expectedRuleId, string targetElementName)
        {
            var element = doc.Descendants().First(e => e.Name.LocalName == targetElementName);
            var ruleIdAttr = element.Attributes().First(a => a.Name.LocalName == "ruleId");
            Assert.Equal(expectedRuleId.ToString(), ruleIdAttr.Value);
        }
    }
}
