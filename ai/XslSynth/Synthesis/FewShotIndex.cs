using System.Text.RegularExpressions;
using System.Xml.Linq;
using XslSynth.Core;
using XslSynth.Model;

namespace XslSynth.Synthesis;

/// <summary>
/// Traços estruturais de uma regra DSL Sysmiddle (ou de um fragmento XSLT análogo).
/// Espelham o LIMITE HONESTO do <see cref="DslBlockInterpreter"/>: os traços "difíceis"
/// (<see cref="CompostaAnd"/>, <see cref="Else"/>, <see cref="Aninhada"/>,
/// <see cref="TempNoBegin"/>) são exatamente os padrões que hoje viram stub.
/// </summary>
[Flags]
public enum DslTraits
{
    None = 0,
    Direta = 1,        // T.path = rhs; sem condicional
    GuardedEmit = 2,   // if(IsNullOrEmpty(x) != True()) begin T.path = ... end
    CompostaAnd = 4,   // condição composta com &&
    Else = 8,          // bloco else
    Aninhada = 16,     // if dentro de begin...end
    TempNoBegin = 32   // atribuição a #.temp dentro do begin
}

/// <summary>
/// Um exemplo recuperável do índice few-shot. Três formas possíveis:
///   • PAR DSL→XSLT   (Dsl + Xslt) — o mais valioso para o prompt;
///   • DSL análoga    (só Dsl)     — regra real do mesmo padrão, sem alvo conhecido;
///   • ESTILO XSLT    (só Xslt)    — fragmento de XSL REAL de produção do corpus.
/// </summary>
public sealed record FewShotExample(
    string Tipo,        // NFe | CTe | MDFe | NFSe | "-"
    string Versao,      // 4.00, 3.10, 2.01… | "-"
    DslTraits Traits,   // traços detectados
    string? Dsl,        // DSL Sysmiddle de origem (quando houver)
    string? Xslt,       // fragmento XSLT alvo/estilo (quando houver)
    string Origem)      // arquivo de onde o exemplo veio
{
    /// <summary>Chave de indexação: tipo × versão × padrão-primário.</summary>
    public string Chave => $"{Tipo}|{Versao}|{FewShotIndex.Rotulo(FewShotIndex.Primario(Traits))}";
}

/// <summary>
/// Índice LEVE, em memória e 100% determinístico (sem embeddings, sem pacote novo)
/// de exemplos few-shot para a tradução DSL→XSLT — o "P1: indexar o corpus G2KA"
/// de docs/architecture/poc-excel-generator.md §5.
///
/// Fontes indexadas a partir de UMA pasta de corpus:
///   1. <c>**/*.xsl</c> — XSLs REAIS de produção, classificados por tipo×versão
///      (derivados do caminho, ex.: <c>xsl/NFe/4.00/…</c>) e por padrão XSLT
///      (choose/otherwise = else; test com "and" = composta-&amp;&amp;; etc.).
///      O corpus real NÃO traz pares DSL→XSLT em claro (o exportContext.data do
///      Sysmiddle é criptografado) — estes fragmentos entram como ESTILO.
///   2. <c>**/*.decrypted.xml</c> — MapperVOs descriptografados: cada regra DSL é
///      classificada por traço; as que o <see cref="DslBlockInterpreter"/> resolve
///      geram PARES DSL→XSLT verificados (compilam); as difíceis entram como DSL
///      análoga (ajudam o LLM a ver variações reais do padrão).
///
/// Recuperação: dado um <see cref="MapperRule"/>, retorna até K exemplos ranqueados
/// por sobreposição de traços + similaridade lexical (Jaccard de tokens) +
/// preferência por pares e por tipo/versão. Determinismo é virtude: mesma entrada,
/// mesmos exemplos, sempre.
/// </summary>
public sealed class FewShotIndex
{
    private const int MaxSnippetChars = 700;  // fragmento de estilo maior que isso vira ruído no prompt
    private const int MaxPorChave = 12;       // teto por chave (índice leve por construção)

    private readonly List<FewShotExample> _exemplos;

    private FewShotIndex(List<FewShotExample> exemplos) => _exemplos = exemplos;

    public int Count => _exemplos.Count;

    /// <summary>Contagem de exemplos por chave (tipo|versão|padrão), ordenada — p/ --rag-stats.</summary>
    public IReadOnlyList<(string Chave, int Total, int Pares, int SoDsl, int SoXslt)> Stats()
        => _exemplos
            .GroupBy(e => e.Chave)
            .Select(g => (g.Key, g.Count(),
                g.Count(e => e.Dsl is not null && e.Xslt is not null),
                g.Count(e => e.Dsl is not null && e.Xslt is null),
                g.Count(e => e.Dsl is null && e.Xslt is not null)))
            .OrderBy(t => t.Key, StringComparer.Ordinal)
            .ToList();

    // ── Construção ──────────────────────────────────────────────────────────

    /// <summary>Constrói o índice varrendo a pasta de corpus (xsl + mapeadores descriptografados).</summary>
    public static FewShotIndex Build(string corpusDir, Action<string>? log = null)
    {
        var exemplos = new List<FewShotExample>();
        var vistos = new HashSet<string>(StringComparer.Ordinal); // dedupe por conteúdo normalizado
        var porChave = new Dictionary<string, int>(StringComparer.Ordinal);
        int xslLidos = 0, xslIlegiveis = 0, mappersLidos = 0;

        // 1. XSLs reais (estilo), em ordem determinística de caminho.
        foreach (var path in Directory.EnumerateFiles(corpusDir, "*.xsl", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            XDocument doc;
            try { doc = XDocument.Load(path); xslLidos++; }
            catch { xslIlegiveis++; continue; }

            var (tipo, versao) = TipoVersaoDoCaminho(corpusDir, path);
            if (doc.Root is not null)
                ColetaSnippets(doc.Root, tipo, versao, Path.GetFileName(path), exemplos, vistos, porChave);
        }

        // 2. MapperVOs descriptografados (DSL real; pares quando o interpretador resolve).
        var interpreter = new DslBlockInterpreter();
        foreach (var path in Directory.EnumerateFiles(corpusDir, "*.decrypted.xml", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            MapperVo mapper;
            try { mapper = new RealMapperParser().ParseFile(path); mappersLidos++; }
            catch (Exception ex) { log?.Invoke($"[rag] mapeador ilegível '{Path.GetFileName(path)}': {ex.Message}"); continue; }

            IndexaMapper(mapper, Path.GetFileName(path), interpreter, exemplos, vistos, porChave);
        }

        log?.Invoke($"[rag] índice: {exemplos.Count} exemplos ({xslLidos} XSL lidos, {xslIlegiveis} ilegíveis, "
                    + $"{mappersLidos} mapeador(es) DSL).");
        return new FewShotIndex(exemplos);
    }

    /// <summary>Acrescenta as regras de um MapperVO já carregado (ex.: o mapeador do fluxo real).</summary>
    public void AddMapper(MapperVo mapper, string origem)
    {
        var vistos = new HashSet<string>(_exemplos.Select(e => Normaliza(e.Dsl ?? e.Xslt ?? "")), StringComparer.Ordinal);
        var porChave = _exemplos.GroupBy(e => e.Chave).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        IndexaMapper(mapper, origem, new DslBlockInterpreter(), _exemplos, vistos, porChave);
    }

    private static void IndexaMapper(MapperVo mapper, string origem, DslBlockInterpreter interpreter,
        List<FewShotExample> exemplos, HashSet<string> vistos, Dictionary<string, int> porChave)
    {
        // Tipo/versão do mapeador real: convenção de nome (…_NFE → NFe 4.00 é o corrente).
        var tipo = TipoDoNome(mapper.Name ?? origem);
        foreach (var rule in mapper.Rules)
        {
            var dsl = StripMarkers(rule.ContentValue ?? "");
            if (dsl.Length == 0) continue;
            var traits = ClassifyTraits(dsl);
            if (traits == DslTraits.None) continue;

            // Par verificado SÓ para regras fáceis (guarded-emit/direta): nelas o
            // interpretador cobre a DSL inteira. Em regras difíceis ele consome só
            // os blocos que reconhece — indexar esse parcial como "par" ensinaria
            // o LLM a IGNORAR o else/&& (tradução errada com cara de verificada).
            const DslTraits dificeis = DslTraits.CompostaAnd | DslTraits.Else
                                       | DslTraits.Aninhada | DslTraits.TempNoBegin;
            string? xslt = null;
            if ((traits & dificeis) == 0)
            {
                var emissoes = interpreter.Interpret(rule);
                if (emissoes.Count > 0)
                    xslt = string.Join("\n", emissoes.Select(e => e.BodyXsl));
                if (xslt is { Length: > MaxSnippetChars }) xslt = null; // par gigante não cabe no prompt
            }

            Adiciona(new FewShotExample(tipo, "4.00", traits, dsl, xslt, origem), exemplos, vistos, porChave);
        }
    }

    private static void Adiciona(FewShotExample ex, List<FewShotExample> exemplos,
        HashSet<string> vistos, Dictionary<string, int> porChave)
    {
        if (ex.Dsl is { Length: > MaxSnippetChars }) return;
        var norm = Normaliza(ex.Dsl ?? ex.Xslt ?? "");
        if (norm.Length == 0 || !vistos.Add(norm)) return;

        var n = porChave.GetValueOrDefault(ex.Chave);
        if (n >= MaxPorChave) return;
        porChave[ex.Chave] = n + 1;
        exemplos.Add(ex);
    }

    // ── Classificação da DSL (regex — mesmas noções do DslBlockInterpreter) ──

    // if(IsNullOrEmpty(x) != True()) begin T.path = rhs; end  (padrão dominante)
    private static readonly Regex GuardedEmitRx = new(
        @"if\s*\(\s*IsNullOrEmpty\(\s*(#\.[A-Za-z0-9_]+|I\.[A-Za-z0-9_/]+)\s*\)\s*!=\s*True\(\)\s*\)\s*" +
        @"begin\s*T\.([A-Za-z0-9_/]+)\s*=\s*(.+?)\s*;\s*end",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex DirectEmitRx =
        new(@"T\.[A-Za-z0-9_/]+\s*=\s*.+?;", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex ElseRx = new(@"\belse\b", RegexOptions.Compiled);
    private static readonly Regex IfRx = new(@"\bif\s*\(", RegexOptions.Compiled);

    // if( … ) dentro de um begin ainda não fechado por end (aproximação suficiente p/ classificar).
    private static readonly Regex IfDentroDeBeginRx =
        new(@"\bbegin\b(?:(?!\bend\b)[\s\S])*?\bif\s*\(", RegexOptions.Compiled);

    // #.temp = … dentro de um begin ainda não fechado por end.
    private static readonly Regex TempDentroDeBeginRx =
        new(@"\bbegin\b(?:(?!\bend\b)[\s\S])*?#\.[A-Za-z0-9_]+\s*=", RegexOptions.Compiled);

    /// <summary>Detecta os traços estruturais de uma DSL (pode combinar vários).</summary>
    public static DslTraits ClassifyTraits(string? dsl)
    {
        var code = StripMarkers(dsl ?? "");
        if (code.Length == 0) return DslTraits.None;

        var t = DslTraits.None;
        if (code.Contains("&&", StringComparison.Ordinal)) t |= DslTraits.CompostaAnd;
        if (ElseRx.IsMatch(code)) t |= DslTraits.Else;
        if (IfDentroDeBeginRx.IsMatch(code)) t |= DslTraits.Aninhada;
        if (TempDentroDeBeginRx.IsMatch(code)) t |= DslTraits.TempNoBegin;
        if (GuardedEmitRx.IsMatch(code)) t |= DslTraits.GuardedEmit;
        if (t == DslTraits.None && !IfRx.IsMatch(code) && DirectEmitRx.IsMatch(code)) t |= DslTraits.Direta;
        return t;
    }

    /// <summary>Traço primário (para chave/estatística): o mais "difícil" presente.</summary>
    public static DslTraits Primario(DslTraits t)
    {
        foreach (var p in new[] { DslTraits.Else, DslTraits.Aninhada, DslTraits.CompostaAnd,
                     DslTraits.TempNoBegin, DslTraits.GuardedEmit, DslTraits.Direta })
            if (t.HasFlag(p)) return p;
        return DslTraits.None;
    }

    /// <summary>Rótulo kebab-case do padrão (para chave e relatórios).</summary>
    public static string Rotulo(DslTraits primario) => primario switch
    {
        DslTraits.Else => "else",
        DslTraits.Aninhada => "aninhada",
        DslTraits.CompostaAnd => "composta-&&",
        DslTraits.TempNoBegin => "temp-no-begin",
        DslTraits.GuardedEmit => "guarded-emit",
        DslTraits.Direta => "direta",
        _ => "desconhecido"
    };

    // ── Classificação de fragmentos XSLT (espelho dos traços da DSL) ─────────

    private static void ColetaSnippets(XElement root, string tipo, string versao, string origem,
        List<FewShotExample> exemplos, HashSet<string> vistos, Dictionary<string, int> porChave)
    {
        // Caminhada top-down: indexa o elemento interessante mais EXTERNO e não desce
        // nele de novo (evita contar o mesmo choose N vezes pelos filhos).
        foreach (var el in root.Elements())
        {
            var traits = TraitsDoXslt(el);
            if (traits != DslTraits.None)
            {
                var texto = el.ToString(SaveOptions.None).Trim();
                if (texto.Length <= MaxSnippetChars)
                {
                    Adiciona(new FewShotExample(tipo, versao, traits, null, texto, origem),
                        exemplos, vistos, porChave);
                    continue; // não desce: o snippet externo já carrega os internos
                }
                // grande demais: desce em busca de um fragmento interno que caiba
            }
            ColetaSnippets(el, tipo, versao, origem, exemplos, vistos, porChave);
        }
    }

    /// <summary>Traços de um elemento XSLT: choose/otherwise = else; test com and = composta; etc.</summary>
    private static DslTraits TraitsDoXslt(XElement el)
    {
        if (el.Name != Xslt.Ns + "if" && el.Name != Xslt.Ns + "choose") return DslTraits.None;

        var t = DslTraits.None;
        if (el.Name == Xslt.Ns + "choose" && el.Elements(Xslt.Ns + "otherwise").Any())
            t |= DslTraits.Else;

        var testes = el.DescendantsAndSelf()
            .Where(d => d.Name == Xslt.Ns + "if" || d.Name == Xslt.Ns + "when")
            .Select(d => d.Attribute("test")?.Value ?? "");
        foreach (var teste in testes)
        {
            if (Regex.IsMatch(teste, @"\band\b")) t |= DslTraits.CompostaAnd;
            if (teste.Contains("!=''", StringComparison.Ordinal) ||
                teste.Contains("!= ''", StringComparison.Ordinal)) t |= DslTraits.GuardedEmit;
        }

        if (el.Elements().SelectMany(c => c.DescendantsAndSelf())
                .Any(d => d.Name == Xslt.Ns + "if" || d.Name == Xslt.Ns + "choose"))
            t |= DslTraits.Aninhada;
        if (el.Descendants(Xslt.Ns + "variable").Any())
            t |= DslTraits.TempNoBegin;

        // um <xsl:if test="x!=''"> puro e sem nada difícil não vale indexar como estilo
        // (o DslBlockInterpreter já cobre guarded-emit sem ajuda do LLM)
        return t is DslTraits.GuardedEmit or DslTraits.None ? DslTraits.None : t;
    }

    // ── Recuperação ─────────────────────────────────────────────────────────

    /// <summary>
    /// Recupera até <paramref name="k"/> exemplos análogos à regra: mesma família de
    /// traços, ranqueados por sobreposição de traços, presença de par DSL→XSLT,
    /// similaridade lexical (Jaccard) e afinidade de tipo/versão. Determinístico.
    /// </summary>
    public IReadOnlyList<FewShotExample> Retrieve(MapperRule rule, int k = 3,
        string tipo = "NFe", string versao = "4.00")
    {
        var dsl = StripMarkers(rule.ContentValue ?? "");
        var traits = ClassifyTraits(dsl);
        if (traits == DslTraits.None || k <= 0) return Array.Empty<FewShotExample>();

        var tokens = Tokens(dsl);
        var normSelf = Normaliza(dsl);

        return _exemplos
            .Select((ex, i) => (Ex: ex, Ordem: i, Score: Score(ex, traits, tokens, tipo, versao)))
            .Where(c => c.Score > 0 && Normaliza(c.Ex.Dsl ?? "") != normSelf) // exclui a própria regra
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Ordem) // desempate determinístico pela ordem de indexação
            .Take(k)
            .Select(c => c.Ex)
            .ToList();
    }

    private static double Score(FewShotExample ex, DslTraits ruleTraits,
        IReadOnlySet<string> ruleTokens, string tipo, string versao)
    {
        var overlap = CountBits(ex.Traits & ruleTraits);
        if (overlap == 0) return 0;

        double score = overlap * 10;
        if (ex.Dsl is not null && ex.Xslt is not null) score += 6; // par completo vale mais
        else if (ex.Xslt is not null) score += 3;                  // estilo XSLT real
        if (string.Equals(ex.Tipo, tipo, StringComparison.OrdinalIgnoreCase)) score += 2;
        if (string.Equals(ex.Versao, versao, StringComparison.OrdinalIgnoreCase)) score += 1;
        if (ex.Dsl is not null && ruleTokens.Count > 0)
            score += 5 * Jaccard(ruleTokens, Tokens(ex.Dsl));
        return score;
    }

    private static int CountBits(DslTraits t)
    {
        int v = (int)t, n = 0;
        while (v != 0) { n += v & 1; v >>= 1; }
        return n;
    }

    // ── Similaridade lexical (determinística, sem embeddings) ────────────────

    private static readonly Regex TokenRx = new(@"[A-Za-z][A-Za-z0-9_]{2,}", RegexOptions.Compiled);
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        { "begin", "end", "else", "true", "isnullorempty", "linha" };

    private static HashSet<string> Tokens(string dsl)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in TokenRx.Matches(dsl))
            if (!StopWords.Contains(m.Value)) set.Add(m.Value);
        return set;
    }

    private static double Jaccard(IReadOnlySet<string> a, IReadOnlySet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var inter = a.Count(b.Contains);
        return (double)inter / (a.Count + b.Count - inter);
    }

    // ── Tipo × versão a partir do caminho/nome ───────────────────────────────

    private static readonly string[] Tipos = ["NFe", "CTe", "MDFe", "NFSe"];

    private static (string Tipo, string Versao) TipoVersaoDoCaminho(string corpusDir, string path)
    {
        var rel = Path.GetRelativePath(corpusDir, path);
        var partes = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        for (var i = 0; i < partes.Length; i++)
        {
            var tipo = Tipos.FirstOrDefault(t => string.Equals(t, partes[i], StringComparison.OrdinalIgnoreCase));
            if (tipo is null) continue;
            var versao = i + 2 < partes.Length && Regex.IsMatch(partes[i + 1], @"^\d")
                ? NormalizaVersao(partes[i + 1])
                : VersaoDoNome(partes[^1]);
            return (tipo, versao);
        }
        return (TipoDoNome(partes[^1]), VersaoDoNome(partes[^1]));
    }

    private static string TipoDoNome(string nome)
    {
        // Ordem do mais específico para o mais genérico (NFSe/MDFe antes de NFe/CTe).
        foreach (var t in new[] { "NFSe", "MDFe", "CTe", "NFe" })
            if (nome.Contains(t, StringComparison.OrdinalIgnoreCase)) return t;
        return "-";
    }

    private static string VersaoDoNome(string nome)
    {
        var m = Regex.Match(nome, @"(\d+\.\d{2}[a-z]?)");
        return m.Success ? NormalizaVersao(m.Groups[1].Value) : "-";
    }

    /// <summary>Normaliza variações do corpus: "4.000" → "4.00".</summary>
    private static string NormalizaVersao(string v)
    {
        var m = Regex.Match(v, @"^(\d+\.\d{2})0+$");
        return m.Success ? m.Groups[1].Value : v;
    }

    // ── Utilitários ──────────────────────────────────────────────────────────

    private static string StripMarkers(string dsl) =>
        dsl.Replace("%beginRuleContent;", "").Replace("%endRuleContent;", "").Trim();

    private static string Normaliza(string s) => Regex.Replace(s, @"\s+", " ").Trim();
}
