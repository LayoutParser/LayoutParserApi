using System.Xml.Linq;
using XslSynth.Core;

namespace XslSynth.Metrics;

/// <summary>
/// O que sobrou de um caso depois da tentativa de virar candidato submissível.
/// <c>Xml</c> null ⇒ o caso NÃO entra no manifesto (o contrato garante que todo
/// <c>xmlPath</c> aponta para arquivo existente); <c>Motivo</c> explica o porquê no log.
/// </summary>
internal sealed record CandidateOutcome(
    string? Xml, bool Eligible, string? SourceFixture, bool? XsdValid, string? Notes, string? Motivo);

/// <summary>
/// Fecha o elo §A2 do handoff do Job 2: o Job 1 produz um XSLT, mas o Pollux consome um
/// XML de NF-e. Aqui o XSLT candidato é APLICADO sobre um ROOT montado a partir de um TXT
/// de instância REAL, e o resultado é o artefato que o Job 2 submete.
///
/// REGRA INEGOCIÁVEL (§A3): sem instância real compatível, NÃO se inventa documento.
/// Instância sintetizada seria rejeitada pela SEFAZ-fake por chave/DV/CNPJ inválidos, e aí
/// o cStat mediria o gerador de dados, não o XSLT — ruído no lugar de sinal. Sem instância,
/// o caso simplesmente não vira candidato e o gap vai para o log com o motivo exato.
/// </summary>
internal sealed class CandidateXmlFactory
{
    private readonly string? _instancesDir;
    private readonly string? _nfeXsdPath;
    private readonly Action<string> _log;
    private readonly List<string> _instancias;

    public CandidateXmlFactory(string? instancesDir, string? nfeXsdPath, Action<string> log)
    {
        _instancesDir = instancesDir is not null && Directory.Exists(instancesDir) ? instancesDir : null;
        _nfeXsdPath = nfeXsdPath is not null && File.Exists(nfeXsdPath) ? nfeXsdPath : null;
        _log = log;
        _instancias = _instancesDir is null
            ? []
            : Directory.EnumerateFiles(_instancesDir)
                .Where(f => !f.EndsWith(".gitkeep", StringComparison.Ordinal))
                .OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    public string DescribeSources() =>
        $"instâncias: {(_instancesDir is null ? "(diretório não configurado/inexistente)" : $"{_instancias.Count} arquivo(s) em {_instancesDir}")}"
        + $" · XSD NF-e: {(_nfeXsdPath is null ? "(indisponível — xsdValid ficará null)" : Path.GetFileName(_nfeXsdPath))}";

    /// <summary>
    /// Tenta transformar (caso do dataset + XSLT candidato) num XML de NF-e submissível.
    /// Cada etapa que falha devolve <c>Motivo</c> — nada aqui degrada em silêncio.
    /// </summary>
    public CandidateOutcome TryProduce(DatasetPair caso, string candidateXslt, string operation)
    {
        if (!PolluxEligibility.IsStructurallyEligible(caso.DocType, operation))
            return new CandidateOutcome(null, false, null, null, null,
                $"fora do escopo do Pollux (docType={caso.DocType}, operacao={operation})");

        var map = TclLayoutMap.TryParse(caso.InputMapTcl);
        if (map is null)
            return new CandidateOutcome(null, false, null, null, null, "schema TCL do par ilegível ou sem <LINE>");

        XDocument xslt;
        try { xslt = XDocument.Parse(candidateXslt); }
        catch (Exception ex)
        {
            return new CandidateOutcome(null, false, null, null, null, $"XSLT candidato não é XML bem-formado: {ex.Message}");
        }

        var (root, fixture, motivoInstancia) = ResolveInstancia(caso, map);
        if (root is null)
            return new CandidateOutcome(null, false, null, null, null, motivoInstancia);

        string saida;
        try
        {
            saida = new XsltApplier().Apply(xslt, root);
        }
        catch (Exception ex)
        {
            // Falha REAL de qualidade do candidato (não compila / não aplica) — vira motivo,
            // não candidato. O Serilog do lote já registra o caso como gerado; aqui só não há
            // XML para submeter.
            return new CandidateOutcome(null, false, fixture, null, null,
                $"XSLT candidato não compilou/aplicou: {ex.Message}");
        }

        XElement? raiz;
        try { raiz = XDocument.Parse(saida).Root; }
        catch (Exception ex)
        {
            return new CandidateOutcome(null, false, fixture, null, null, $"saída da transformação ilegível: {ex.Message}");
        }
        if (raiz is null)
            return new CandidateOutcome(null, false, fixture, null, null, "saída da transformação vazia");

        // Envelope de lote → desembrulha quando há exatamente uma NF-e dentro.
        string? notas = null;
        if (!PolluxEligibility.IsSubmittableNfe(raiz))
        {
            var interna = PolluxEligibility.TryUnwrap(raiz, out notas);
            if (interna is not null) raiz = interna;
        }

        var elegivel = PolluxEligibility.IsSubmittableNfe(raiz);
        if (!elegivel)
            notas ??= $"raiz <{raiz.Name.LocalName}> não é uma NF-e submissível";

        // ── Gate anti-ruído: o XML tem que DEPENDER da instância ──────────────────────
        // Aplica-se o MESMO XSLT sobre um ROOT vazio. Se a saída for idêntica, nada do
        // documento real entrou no resultado — os XPaths do XSLT não casaram com a estrutura
        // do ROOT e o que sobrou foram literais do stylesheet. Isso já produziu, num teste,
        // uma NF-e vazia classificada como elegível; submetê-la ao Pollux mediria o
        // stylesheet contra o nada. É um teste mais forte que "tem algum texto?".
        if (elegivel && !DependeDaInstancia(xslt, saida))
        {
            elegivel = false;
            notas = "XML gerado não depende da instância (saída idêntica à de um ROOT vazio) — "
                + "os XPaths do XSLT não casaram com a estrutura do ROOT montado a partir do TCL";
        }

        var xsdValido = elegivel ? ValidaXsd(raiz) : null;
        var xml = raiz.ToString(SaveOptions.None);
        return new CandidateOutcome(xml, elegivel, fixture, xsdValido, notas, null);
    }

    /// <summary>
    /// Procura, entre as instâncias configuradas, uma que CASE de fato com o schema TCL do
    /// caso. Preferência para arquivos cujo nome contenha o stem do caso; depois testa as
    /// demais. "Casar" é verificado (<see cref="TclRootBuilder"/>), nunca presumido pelo nome.
    /// </summary>
    private (XDocument? Root, string? Fixture, string Motivo) ResolveInstancia(DatasetPair caso, TclLayoutMap map)
    {
        if (_instancias.Count == 0)
            return (null, null, "sem TXT de instância disponível (nenhum diretório de instâncias configurado)");

        var stem = caso.Id.Split('\\', '/').LastOrDefault() ?? caso.Id;
        var ordenadas = _instancias
            .OrderByDescending(f => Path.GetFileNameWithoutExtension(f).Contains(stem, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var melhorTaxa = 0.0;
        string? melhorMotivo = null;
        foreach (var arquivo in ordenadas)
        {
            var r = TclRootBuilder.TryBuild(arquivo, map);
            if (r.Root is not null)
                return (r.Root, Path.GetFileName(arquivo), "");
            if (r.TaxaCasamento >= melhorTaxa)
            {
                melhorTaxa = r.TaxaCasamento;
                melhorMotivo = $"{Path.GetFileName(arquivo)}: {r.Motivo}";
            }
        }

        return (null, null,
            $"nenhuma das {_instancias.Count} instância(s) casa com o schema TCL deste par "
            + $"(melhor casamento {melhorTaxa:P0}) — {melhorMotivo}");
    }

    /// <summary>Roda o XSLT contra um <c>&lt;ROOT/&gt;</c> vazio e compara com a saída real.
    /// Iguais ⇒ a instância não influenciou nada. Se a transformação de controle falhar,
    /// assume-se que depende (não se rejeita candidato por causa de um teste auxiliar).</summary>
    private static bool DependeDaInstancia(XDocument xslt, string saidaReal)
    {
        try
        {
            var vazio = new XsltApplier().Apply(xslt, new XDocument(new XElement("ROOT")));
            return !string.Equals(vazio, saidaReal, StringComparison.Ordinal);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Oráculo XSD do leiaute NF-e. Erros de assinatura são esperados (o pipeline
    /// não assina) e não contam. Sem XSD plugado, devolve null — nunca "false" por ausência.</summary>
    private bool? ValidaXsd(XElement nfe)
    {
        if (_nfeXsdPath is null) return null;
        try
        {
            XNamespace ns = PolluxEligibility.NfeNamespace;
            var comNs = nfe.Name.NamespaceName == ns.NamespaceName ? nfe : ComNamespace(nfe, ns);
            var res = new XsdValidator().Validate(comNs.ToString(), _nfeXsdPath);
            var reais = res.Errors.Count(e => !e.Contains("Signature", StringComparison.Ordinal));
            return reais == 0;
        }
        catch (Exception ex)
        {
            _log($"   [xsd] validação indisponível para este candidato ({ex.Message}) — xsdValid=null.");
            return null;
        }
    }

    private static XElement ComNamespace(XElement el, XNamespace ns)
    {
        var novo = new XElement(ns + el.Name.LocalName);
        foreach (var a in el.Attributes().Where(a => !a.IsNamespaceDeclaration))
            novo.Add(new XAttribute(a.Name.LocalName, a.Value));
        foreach (var n in el.Nodes())
            novo.Add(n is XElement c ? ComNamespace(c, ns) : n);
        return novo;
    }
}
