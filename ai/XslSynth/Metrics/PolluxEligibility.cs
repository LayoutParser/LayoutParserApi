using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace XslSynth.Metrics;

/// <summary>
/// Classificação de um caso do dataset e a REGRA DE ELEGIBILIDADE ao Pollux — que mora
/// aqui, no produtor (handoff-job2-cypress-batch.md §2, regra 4). O Job 2 só lê a flag
/// <c>eligibleForPollux</c>; ampliar o escopo (cancelamento, CT-e) é mudança NESTE arquivo,
/// sem tocar na spec Cypress.
///
/// Escopo real medido contra o dataset (54 pares de dataset_pairs_filtered_v2.jsonl):
/// o Pollux (WSInserirDocumento) consome XML de NF-e. Dos 54 pares, apenas 4 são
/// "NFe + envio" — e destes, só 2 emitem raiz &lt;NFe&gt; direta; os outros 2
/// (…NeoGridPipelineToSefaz) emitem o envelope de lote &lt;NeoGridFiscalList&gt;, que NÃO é
/// submissível como está. O resto do dataset é retorno SEFAZ→ERP, consulta, cancelamento,
/// inutilização, CT-e e MDF-e — nada disso vai para o WSInserirDocumento.
/// </summary>
public static class PolluxEligibility
{
    /// <summary>Namespace do portal fiscal — a raiz submissível tem que estar nele.</summary>
    public const string NfeNamespace = "http://www.portalfiscal.inf.br/nfe";

    /// <summary>Envelope de lote do pipeline NeoGrid: não é um documento fiscal, é um
    /// contêiner de N notas. Ver <see cref="TryUnwrap"/>.</summary>
    private const string EnvelopeNeoGrid = "NeoGridFiscalList";

    // ── Classificação da operação a partir do Id do par ────────────────────────────
    // A ORDEM importa: "RetEnvNFe" é retorno, não envio; "evtCancNFe" é evento, não
    // cancelamento clássico. Casar o token errado aqui contamina o painel inteiro.
    // A caixa dos nomes de produção é inconsistente ("ConsSitNFe" mas "consSitCTe",
    // "ConsStatServ" mas "consStatServCTe") — daí IgnoreCase em tudo que é token de
    // operação. As duas EXCEÇÕES são as regras de "ret"/"evt", onde o [A-Z] seguinte é o
    // que separa o prefixo real ("_RetEnvNFe") de uma palavra qualquer ("Retirada"): ali
    // IgnoreCase destruiria a regra.
    private const RegexOptions Ci = RegexOptions.Compiled | RegexOptions.IgnoreCase;

    private static readonly (Regex Padrao, string Operacao)[] Regras =
    [
        (new Regex(@"(?:^|_)[Rr]et[A-Z]", RegexOptions.Compiled), "retorno"),
        (new Regex(@"evt[A-Z]", RegexOptions.Compiled), "evento"),
        (new Regex(@"envio|_env[A-Z]", Ci), "envio"),
        (new Regex(@"canc", Ci), "cancelamento"),
        (new Regex(@"inut", Ci), "inutilizacao"),
        (new Regex(@"conssit", Ci), "consulta-situacao"),
        (new Regex(@"consstatserv", Ci), "consulta-status"),
        (new Regex(@"conscad", Ci), "consulta-cadastro"),
        (new Regex(@"consnaoenc", Ci), "consulta-nao-encerrados"),
    ];

    /// <summary>Operação derivada do nome do mapa (envio, retorno, cancelamento…).
    /// "desconhecida" quando nenhum token conhecido casa — nunca chuta "envio".</summary>
    public static string ClassifyOperation(string layoutId)
    {
        var stem = layoutId.Split('\\', '/').LastOrDefault() ?? layoutId;
        foreach (var (padrao, operacao) in Regras)
            if (padrao.IsMatch(stem)) return operacao;
        return "desconhecida";
    }

    /// <summary>
    /// <c>candidateId</c> = <c>layout</c> com os separadores trocados por <c>_</c> (§2, regra 2).
    /// Lista fixa de caracteres proibidos (em vez de Path.GetInvalidFileNameChars) para o
    /// resultado ser IDÊNTICO em Linux (VM do job) e Windows (dev) — o id vira nome de arquivo
    /// e é usado pelo Job 2 para casar XML e resultado.
    /// </summary>
    public static string ToCandidateId(string layout)
    {
        var sb = new System.Text.StringBuilder(layout.Length);
        foreach (var c in layout)
            sb.Append(c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c);
        return sb.ToString();
    }

    /// <summary>Só "NFe + envio" tem chance de ser submissível — filtro BARATO, aplicado antes
    /// de tentar montar instância/aplicar XSLT (o resto do dataset nem chega a ser processado).</summary>
    public static bool IsStructurallyEligible(string docType, string operation) =>
        string.Equals(docType, "NFe", StringComparison.OrdinalIgnoreCase)
        && operation == "envio";

    /// <summary>
    /// Veredito final sobre o XML já produzido: é uma NF-e submissível?
    /// Exige raiz <c>&lt;NFe&gt;</c> no namespace do portal fiscal (ou um envelope com
    /// exatamente uma NF-e dentro, ver <see cref="TryUnwrap"/>).
    /// </summary>
    public static bool IsSubmittableNfe(XElement raiz) =>
        raiz.Name.LocalName == "NFe"
        && (raiz.Name.NamespaceName == NfeNamespace || raiz.Name.NamespaceName.Length == 0);

    /// <summary>
    /// Desembrulha o envelope de lote <c>&lt;NeoGridFiscalList&gt;</c> QUANDO ele contém
    /// exatamente uma NF-e. Isso é desembrulho determinístico de um documento real — não é
    /// síntese de dado. Com 0 ou mais de 1 nota, devolve null: escolher "qual das N" seria
    /// arbítrio, e submeter o envelope inteiro seria enviar algo que o WS não aceita.
    /// </summary>
    public static XElement? TryUnwrap(XElement raiz, out string? nota)
    {
        nota = null;
        if (raiz.Name.LocalName != EnvelopeNeoGrid) return null;

        var notas = raiz.Descendants().Where(e => e.Name.LocalName == "NFe").ToList();
        if (notas.Count == 1)
        {
            nota = $"desembrulhado de <{EnvelopeNeoGrid}> (envelope de lote com 1 NF-e)";
            return notas[0];
        }

        nota = $"envelope <{EnvelopeNeoGrid}> com {notas.Count} NF-e — não submissível como documento único";
        return null;
    }
}
