using System.Xml.Linq;

namespace XslSynth.Excel;

/// <summary>
/// Etapa B2: emite o envelope (`idLote`, `indSinc`) e o bloco proprietário
/// `&lt;dadosAdic&gt;` da integração FiatMQ. Estes campos NÃO estão no XSD do leiaute
/// SEFAZ (é extensão do integrador) e são dirigidos por REGRAS do mapeador —
/// constantes, config de servidor (`GetConfigParametersValue` → vazio), LinkMappings
/// e o acumulador `bloco290`. Traduzidos a partir da análise das regras reais.
///
/// Ordem dos filhos = a do gabarito de produção. Slugs do ROOT confirmados contra o
/// `generated.tcl` (bloco de controle LINHA000/LINHA004). `bloco290` (acumulador de
/// ~290 chars) é tratado à parte por <see cref="Bloco290Emitter"/>.
/// </summary>
public sealed class DadosAdicEmitter
{
    private static readonly XNamespace Xs = Core.Xslt.Ns;

    public void Aplicar(XElement enviNFe, SpecModel spec, XDocument rootTree, Action<string>? log = null)
    {
        var nfe = enviNFe.Elements("NFe").FirstOrDefault();
        if (nfe is null) return;

        // ── Envelope: idLote e indSinc ANTES do <NFe> (Rule_idLote / Rule_indSinc) ──
        enviNFe.AddFirst(new XElement("indSinc", "0"));   // Rule_indSinc: constante 0
        enviNFe.AddFirst(new XElement("idLote", "00001")); // Rule_idLote: constante '00001'

        // ── Selects do bloco de controle (slugs verificados no generated.tcl) ──
        const string endImpr = "normalize-space(ROOT/LINHA000/Endereco_de_Impressao)";
        const string cnpjEmit = "normalize-space(ROOT/LINHA004/Numero_do_CNPJ_do_Emitente)";
        const string idOverlay = "normalize-space(ROOT/LINHA000/ID_Overlay_Mascara_Impressao)";

        // dadosAdic/infCpl = MESMO conteúdo do infAdic/infCpl (as duas regras iteram
        // LINHA081 igual). Clonar o que já está correto no infNFe garante que batem.
        var infCplClone = new XElement("infCpl");
        var infAdicInfCpl = nfe.Descendants("infCpl").FirstOrDefault();
        if (infAdicInfCpl is not null)
            infCplClone.Add(infAdicInfCpl.Nodes().Select(Clonar));

        var dadosAdic = new XElement("dadosAdic",
            // GetConfigParametersValue(...) → vazio (config do servidor não setada aqui)
            new XElement("B2BDirectory", ""),
            new XElement("B2BPDFDirectory", ""),
            // Rule_CodigoImpressao sobrescreve o input (#.campo = 1) → sempre '1'
            new XElement("CodigoImpressao", "1"),
            // PrinterKey/ContingencyPrinterKey = LinkMappings do MESMO EnderecoDeImpressao
            Folha("PrinterKey", endImpr),
            Folha("ContingencyPrinterKey", endImpr),
            // Rule_BaseForm: CNPJ raiz (8) + '_' + IdOverlay  (ex.: 36519422_001)
            Folha("BaseForm", $"concat(substring({cnpjEmit},1,8),'_',{idOverlay})"),
            // (bloco290 inserido aqui por Bloco290Emitter)
            new XElement("Codigo_Connector", "MQ"),                 // Rule_Codigo_Connector: 'MQ'
            infCplClone,                                            // = infAdic/infCpl (clone)
            // Grupo Conv51/ST (impressão DANFE): ramo else das regras → '0,00' pt-BR
            // neste par (ICMS CST00, sem partilha). Fonte real = LINHA050 zerada.
            new XElement("baseICMSConv51", "0,00"),
            new XElement("vlrICMSConv51", "0,00"),
            new XElement("baseICMSST", "0,00"),
            new XElement("vlrICMSST", "0,00"),
            new XElement("vlrpbCop", "0,00"),
            new XElement("vlrPISST", "0,00"),
            new XElement("vlrCOFINSST", "0,00"));

        // bloco290 na posição 7 (após BaseForm, antes de Codigo_Connector).
        var bloco290 = new Bloco290Emitter().Emit(spec, log);
        if (bloco290 is not null)
            dadosAdic.Element("Codigo_Connector")!.AddBeforeSelf(bloco290);

        nfe.Add(dadosAdic);
        log?.Invoke($"   [B2] dadosAdic emitido: {dadosAdic.Elements().Count()} campos + idLote/indSinc.");
    }

    private static XElement Folha(string nome, string select) =>
        new(nome, new XElement(Xs + "value-of", new XAttribute("select", select)));

    /// <summary>Clona um nó (elementos XSLT como for-each/value-of são copiados fundo).</summary>
    private static XNode Clonar(XNode n) => n is XElement e ? new XElement(e) : new XText(((XText)n).Value);
}
