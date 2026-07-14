using System.Xml.Linq;

namespace XslSynth.Excel;

/// <summary>
/// Etapa B2.2: `dadosAdic/bloco290` — registro de largura FIXA (337 chars) que a
/// `Rule_bloco290` monta concatenando ~24 segmentos: prefixo '001', modelo (2, da
/// chave), e os campos do bloco de controle LINHA000 via `PadRight(campo, ' ', n)`.
///
/// Observação-chave: as larguras do PadRight IGUALAM as larguras dos campos LINHA000
/// → cada segmento = o slice cru do ROOT ajustado à largura n por
/// <c>substring(concat(campo, espaços), 1, n)</c> (right-pad determinístico). modelo
/// e tipoOperacao são os únicos ramos condicionais.
/// </summary>
public sealed class Bloco290Emitter
{
    private static readonly XNamespace Xs = Core.Xslt.Ns;
    private const string Pad = "                                                                "; // 64 espaços

    // (slug do ROOT/LINHA000, largura) na ORDEM da Rule_bloco290.
    private static readonly (string Slug, int W)[] Segmentos =
    [
        ("Serie_NF", 3), ("Sequencial_NF_Inicial", 9), ("Serie_NF_Contingencia_Scan", 3),
        // tipoOperacao (posição 6) é especial — inserido no meio via $tipoOp
        ("Chave_Acesso", 44), ("Nro_Protocolo_Autorizacao", 15),
        ("CNPJ_Emitente_NF_Canc_Inutilizacao", 14), ("Data_Emissao_NF_Canc_Inutilizacao", 14),
        ("Justificativa_Canc_Inutilizacao", 50), ("CNPJ_Emitente_NF", 14),
        ("Codigo_Plataforma", 3), ("Codigo_Conector", 3), ("Codigo_Impressao", 1),
        ("Vias_Impressao", 2), ("Codigo_Bandeja_Impressao", 2), ("Codigo_Box_Saida_Impressao", 2),
        ("Endereco_de_Impressao", 64), ("Controle_Time_out", 4), ("Controle_Sistema_Origem", 30),
        ("Identificacao_do_Ambiente", 1), ("Controle_do_Banco_NF_e", 50),
        ("ID_Overlay_Mascara_Impressao", 3)
    ];

    public XElement? Emit(SpecModel spec, Action<string>? log = null)
    {
        static string Seg(string slug, int w) =>
            $"substring(concat(ROOT/LINHA000/{slug},'{Pad}'),1,{w})";

        // Ordem: '001' + modelo(2) + serieNF + seqNfIni + serieContScan + tipoOp(1) + chave(44) + resto.
        var args = new List<string> { "'001'", "$modelo" };
        args.Add(Seg("Serie_NF", 3));
        args.Add(Seg("Sequencial_NF_Inicial", 9));
        args.Add(Seg("Serie_NF_Contingencia_Scan", 3));
        args.Add("$tipoOp");
        // do Chave_Acesso em diante, na ordem da tabela (pula os 3 já emitidos).
        foreach (var (slug, w) in Segmentos.SkipWhile(s => s.Slug != "Chave_Acesso"))
            args.Add(Seg(slug, w));

        var select = $"concat({string.Join(",", args)})";

        var el = new XElement("bloco290",
            // chave (44) e modelo (char 38 da chave: '1'→'00', senão '01')
            new XElement(Xs + "variable", new XAttribute("name", "chave"),
                new XAttribute("select", "normalize-space(ROOT/LINHA000/Chave_Acesso)")),
            Variavel("modelo", Escolha("substring($chave,38,1)='1'", "00", "01")),
            // tipoOperacao: constante '0' se preenchido, senão espaço (largura 1)
            Variavel("tipoOp", Escolha("normalize-space(ROOT/LINHA000/Tipo_Operacao_NF)!=''", "0", " ")),
            new XElement(Xs + "value-of", new XAttribute("select", select)));

        log?.Invoke($"   [B2] bloco290: {args.Count} segmentos concatenados (largura fixa).");
        return el;
    }

    private static XElement Variavel(string nome, XElement conteudo) =>
        new(Xs + "variable", new XAttribute("name", nome), conteudo);

    // Valor só-espaço vira <xsl:text> (senão o processador XSLT strippa o whitespace).
    private static object Conteudo(string v) =>
        string.IsNullOrWhiteSpace(v) ? new XElement(Xs + "text", v) : v;

    private static XElement Escolha(string teste, string quando, string senao) =>
        new(Xs + "choose",
            new XElement(Xs + "when", new XAttribute("test", teste), Conteudo(quando)),
            new XElement(Xs + "otherwise", Conteudo(senao)));
}
