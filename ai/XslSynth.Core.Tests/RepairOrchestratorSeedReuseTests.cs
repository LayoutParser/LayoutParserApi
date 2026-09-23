using System.Xml.Linq;

using XslSynth.Model;
using XslSynth.Synthesis;

namespace XslSynth.Core.Tests;

/// <summary>
/// Cobre F1 (reaproveitamento de um XSLT já convergido como seed do
/// <see cref="RepairOrchestrator"/>) e F2 (fallback anti-armadilha: descarta o seed se ele for
/// pior que o baseline determinístico) — ver
/// docs/architecture/adr-geracao-automatica-convergencia-tcl-xslt-2026-09-08.md, seções 3.2 e 5.5.
/// </summary>
public sealed class RepairOrchestratorSeedReuseTests
{
    /// <summary>
    /// <see cref="XsdValidator"/> exige um <c>xsdPath</c> resolvível (lança
    /// <see cref="ArgumentNullException"/> com string vazia) — grava um XSD mínimo e permissivo
    /// em disco só para os testes, imitando o "XSD real" que <c>XsdValidationService</c> resolveria
    /// em produção.
    /// </summary>
    private static readonly string PermissiveXsdPath = WritePermissiveXsd();

    private static string WritePermissiveXsd()
    {
        var path = Path.Combine(Path.GetTempPath(), $"repair-orchestrator-seed-reuse-tests-{Guid.NewGuid():N}.xsd");
        File.WriteAllText(path, """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:element name="Nota">
                <xs:complexType>
                  <xs:sequence>
                    <xs:any minOccurs="0" maxOccurs="unbounded" processContents="skip"/>
                  </xs:sequence>
                </xs:complexType>
              </xs:element>
            </xs:schema>
            """);
        return path;
    }

    private static MapperVo BuildMapper()
    {
        var mapper = new MapperVo { MapperGuid = "guid-teste", Name = "MAP_TESTE" };
        mapper.LinkMappings.Add(new LinkMappingItem
        {
            Name = "nNF",
            Sequence = 1,
            SourcePath = "/ROOT/Linha/nNF",
            TargetPath = "/Nota/nNF"
        });
        return mapper;
    }

    private static XDocument BuildInput(string nNfValor) =>
        new(new XElement("ROOT",
            new XElement("Linha", new XElement("nNF", nNfValor))));

    /// <summary>Documento 2 do mesmo layout: se já existe um XSLT convergido (seed) que já dá
    /// conta do documento novo, o loop converge em 0 iterações extras — sem chamar o
    /// sintetizador nenhuma vez (prova de que o transpilador do zero foi mesmo pulado).</summary>
    [Fact]
    public async Task Seed_convergido_que_ja_atende_o_documento_novo_converge_sem_chamar_o_sintetizador()
    {
        var mapper = BuildMapper();
        var input = BuildInput("123");
        var expectedXml = "<Nota><nNF>123</nNF></Nota>";

        // Seed "convergido anteriormente": já produz exatamente o esperado para este documento.
        var seedXslt = new XDocument(
            new XElement(Xslt.Ns + "stylesheet",
                new XAttribute("version", "1.0"),
                new XAttribute(XNamespace.Xmlns + "xsl", Xslt.Ns.NamespaceName),
                new XElement(Xslt.Ns + "output", new XAttribute("method", "xml")),
                new XElement(Xslt.Ns + "template",
                    new XAttribute("match", "/"),
                    new XElement("Nota",
                        new XElement("nNF",
                            new XElement(Xslt.Ns + "value-of",
                                new XAttribute("select", "/ROOT/Linha/nNF")))))));

        var synthesizer = new CountingSynthesizer();
        var orchestrator = new RepairOrchestrator();
        var logs = new List<string>();

        var report = await orchestrator.RunAsync(
            mapper, input, expectedXml, xsdPath: PermissiveXsdPath, synthesizer, logs.Add,
            maxIterations: 3, ct: default, seedXslt: seedXslt);

        Assert.True(report.Converged);
        Assert.Equal(0, report.Iterations);
        Assert.Equal(0, synthesizer.RepairCalls);
        Assert.Equal(0, synthesizer.RuleSynthesisCalls);
        Assert.Contains(logs, l => l.Contains("Seed reaproveitado", StringComparison.Ordinal));
    }

    /// <summary>F2: se o seed reaproveitado é pior que o baseline determinístico (mesmo depois de
    /// uma tentativa de reparo), o orquestrador descarta o seed e recomeça do baseline — nunca
    /// fica preso a um seed ruim nem derruba a execução.</summary>
    [Fact]
    public async Task Seed_pior_que_o_baseline_e_descartado_em_favor_do_baseline_deterministico()
    {
        var mapper = BuildMapper();
        var input = BuildInput("123");
        var expectedXml = "<Nota><nNF>123</nNF></Nota>";

        // Seed "ruim": raiz errada, produz saída totalmente divergente do esperado — pior que o
        // que o transpilador determinístico produziria (que ao menos mapeia nNF corretamente).
        var seedRuim = new XDocument(
            new XElement(Xslt.Ns + "stylesheet",
                new XAttribute("version", "1.0"),
                new XAttribute(XNamespace.Xmlns + "xsl", Xslt.Ns.NamespaceName),
                new XElement(Xslt.Ns + "output", new XAttribute("method", "xml")),
                new XElement(Xslt.Ns + "template",
                    new XAttribute("match", "/"),
                    new XElement("OutraRaizTotalmenteDiferente",
                        new XElement("campoQualquer", "valor fixo sem relação")))));

        // O sintetizador "de reparo" nunca converge um seed ruim (simula um LLM que não acha
        // solução em 1 tentativa) — o que importa aqui é que o baseline (melhor) prevaleça.
        var synthesizer = new NeverConvergesSynthesizer();
        var orchestrator = new RepairOrchestrator();
        var logs = new List<string>();

        var report = await orchestrator.RunAsync(
            mapper, input, expectedXml, xsdPath: PermissiveXsdPath, synthesizer, logs.Add,
            maxIterations: 1, ct: default, seedXslt: seedRuim);

        // O baseline determinístico mapeia nNF corretamente (0 diffs) — o seed ruim é descartado
        // antes mesmo de entrar no loop de reparo por diff.
        Assert.True(report.Converged);
        Assert.Contains(logs, l => l.Contains("seed pior que o baseline", StringComparison.Ordinal));
    }

    /// <summary>F2 / degradação graciosa: esgotar as iterações sem convergir nunca lança exceção
    /// — o relatório volta com <c>Converged == false</c> e o diff residual, permitindo que o
    /// chamador degrade (ex.: reportar Failed sem derrubar o parse do documento).</summary>
    [Fact]
    public async Task Esgotar_iteracoes_sem_convergir_nao_lanca_e_devolve_relatorio_com_diff_residual()
    {
        var mapper = BuildMapper();
        var input = BuildInput("123");
        // Esperado propositalmente diferente do que qualquer baseline/seed determinístico produz
        // — força o loop a nunca convergir.
        var expectedXml = "<Nota><nNF>NUNCA-VAI-BATER</nNF></Nota>";

        var synthesizer = new NeverConvergesSynthesizer();
        var orchestrator = new RepairOrchestrator();
        var logs = new List<string>();

        var report = await orchestrator.RunAsync(
            mapper, input, expectedXml, xsdPath: PermissiveXsdPath, synthesizer, logs.Add,
            maxIterations: 2, ct: default, seedXslt: null);

        Assert.False(report.Converged);
        Assert.Equal(2, report.Iterations);
        Assert.NotEmpty(report.FinalDiffs);
    }

    /// <summary>Sintetizador que conta chamadas — usado para provar que o seed já convergido não
    /// dispara síntese de regras nem reparo por diff.</summary>
    private sealed class CountingSynthesizer : IXslSynthesizer
    {
        public string Name => "Counting (teste)";
        public int RuleSynthesisCalls { get; private set; }
        public int RepairCalls { get; private set; }

        public Task<IReadOnlyList<RuleFragment>> SynthesizeRulesAsync(
            SynthesisBriefing briefing, CancellationToken ct = default)
        {
            RuleSynthesisCalls++;
            return Task.FromResult<IReadOnlyList<RuleFragment>>(new List<RuleFragment>());
        }

        public Task<string> RepairFromDiffAsync(
            string currentXsl, IReadOnlyList<NodeDiff> diffs, SynthesisBriefing briefing, CancellationToken ct = default)
        {
            RepairCalls++;
            return Task.FromResult(currentXsl);
        }
    }

    /// <summary>Sintetizador que nunca corrige nada — devolve o XSLT atual sem alteração,
    /// simulando um LLM que não encontra solução dentro do teto de iterações.</summary>
    private sealed class NeverConvergesSynthesizer : IXslSynthesizer
    {
        public string Name => "NeverConverges (teste)";

        public Task<IReadOnlyList<RuleFragment>> SynthesizeRulesAsync(
            SynthesisBriefing briefing, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RuleFragment>>(new List<RuleFragment>());

        public Task<string> RepairFromDiffAsync(
            string currentXsl, IReadOnlyList<NodeDiff> diffs, SynthesisBriefing briefing, CancellationToken ct = default)
            => Task.FromResult(currentXsl);
    }
}
