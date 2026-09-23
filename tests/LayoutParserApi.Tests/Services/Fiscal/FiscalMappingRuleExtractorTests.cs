using LayoutParserApi.Services.Fiscal;

using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Services.Fiscal
{
    /// <summary>
    /// Issue #103 Passo 1 — extração determinística contra uma fixture SINTÉTICA que espelha a
    /// estrutura real confirmada em 2026-09-02 (aba "Regra-CST 40 41 e 50" de
    /// "Layout_NF-e_Mensageria_Envio_ReformaTritutária_v1 - NT 1.50.xlsx", fornecida pelo dono).
    ///
    /// O arquivo real NÃO foi copiado para o repo: a mesma pasta tem abas ocultas
    /// ("LogicaANFAVEA_Marelli"/"LogicaANFAVEA_Teksid") e uma tabela "Codigo-Plataforma" com nomes
    /// de cliente reais (Fiasa/Iveco/FPT/Marelli/Comau/Teksid/Grupo CNH). A tabela de decisão
    /// "Regra-CST 40 41 e 50" em si é conteúdo genérico de domínio fiscal público (tabela CST da
    /// NF-e) — reproduzida aqui verbatim (mesmos textos/valores) como fixture sintética.
    /// </summary>
    public class FiscalMappingRuleExtractorTests
    {
        private static string FixturePath => Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "Fiscal", "regra-cst-decision-table.xlsx");

        private static IFiscalMappingRuleExtractor CreateExtractor()
            => new FiscalMappingRuleExtractor(NullLogger<FiscalMappingRuleExtractor>.Instance);

        [Fact]
        public void Extract_DeveEncontrarATabelaDeDecisaoRegraCst()
        {
            var extractor = CreateExtractor();
            using var stream = File.OpenRead(FixturePath);

            var result = extractor.Extract(stream);

            Assert.Contains("Regra-CST 40 41 e 50", result.DecisionTableSheets);
            Assert.Equal(4, result.Rules.Count);
        }

        [Fact]
        public void Extract_DeveIgnorarAbaDeLayoutPosicionalSemLinhaRegra()
        {
            var extractor = CreateExtractor();
            using var stream = File.OpenRead(FixturePath);

            var result = extractor.Extract(stream);

            Assert.Contains("Layout-Emissao-XML-4.00", result.SkippedSheets);
            Assert.DoesNotContain(result.Rules, r => r.SheetName == "Layout-Emissao-XML-4.00");
        }

        [Fact]
        public void Extract_DeveEstruturarCondicoesNaOrdemDoCabecalho()
        {
            var extractor = CreateExtractor();
            using var stream = File.OpenRead(FixturePath);

            var result = extractor.Extract(stream);
            var regra1 = result.Rules.Single(r => r.RuleNumber == "1");

            Assert.Equal(4, regra1.Conditions.Count);
            Assert.Equal("orig", regra1.Conditions[0].Field);
            Assert.Equal("0 ou 1 ou 2", regra1.Conditions[0].RawValue);
            Assert.Equal("CST", regra1.Conditions[1].Field);
            Assert.Equal("40 ou 41 ou 50", regra1.Conditions[1].RawValue);
            Assert.Equal("vICMS", regra1.Conditions[2].Field);
            Assert.Equal("0", regra1.Conditions[2].RawValue);
            Assert.Equal("motDesICMS", regra1.Conditions[3].Field);
            Assert.Equal("0", regra1.Conditions[3].RawValue);
        }

        [Fact]
        public void Extract_DeveCapturarODesfechoEmTextoLivre()
        {
            var extractor = CreateExtractor();
            using var stream = File.OpenRead(FixturePath);

            var result = extractor.Extract(stream);
            var regra3 = result.Rules.Single(r => r.RuleNumber == "3");

            Assert.Equal("Erro - Operacao Invalida", regra3.Outcome);
            Assert.False(regra3.RequiresManualReview);
        }

        [Fact]
        public void Extract_ParaValorMaiorQue0_MantemComoTextoBrutoSemInterpretarOperador()
        {
            // Passo 1 é determinístico e não tenta inferir semântica de operador (">" em "Maior
            // 0,00") — isso fica para uma fase futura (estruturação com LLM revisável por humano,
            // fora do escopo desta fatia). Aqui só garantimos que o valor bruto é preservado.
            var extractor = CreateExtractor();
            using var stream = File.OpenRead(FixturePath);

            var result = extractor.Extract(stream);
            var regra3 = result.Rules.Single(r => r.RuleNumber == "3");

            var vIcms = regra3.Conditions.Single(c => c.Field == "vICMS");
            Assert.Equal("Maior 0,00", vIcms.RawValue);
        }

        [Fact]
        public void Extract_RegistraNumeroDaLinhaDeOrigemParaRastreabilidade()
        {
            var extractor = CreateExtractor();
            using var stream = File.OpenRead(FixturePath);

            var result = extractor.Extract(stream);
            var regra1 = result.Rules.Single(r => r.RuleNumber == "1");

            // Linha 5 = cabeçalho "Regra|orig|CST|..."; linha 6 = legenda "X/XX"; linha 7 = Regra 1.
            Assert.Equal(7, regra1.SourceRowNumber);
        }
    }
}
