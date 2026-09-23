using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace XslSynth.Core;

// ─────────────────────────────────────────────────────────────────────────────
// ExampleCandidateRefiner — issue #438, caminho (a): loop RAG→Ollama (ai/XslSynth metrics-batch).
//
// O modelo (1.5B) acerta o MIOLO campo a campo mas omite (1) a casca do documento
// (<xsl:template match="/">, elemento raiz, namespace, versao) e (2) regras de campo que só
// aparecem no exemplo recuperado (ex.: xJust = prefixo + truncamento a 227). Este passo é
// DETERMINÍSTICO e só usa o que os exemplos recuperados DEMONSTRAM:
//
//   Casca   — raiz/namespace/atributos constantes vêm de exemplo(s) cuja estrutura casa com o
//             candidato (a raiz do exemplo tem o 1º elemento de conteúdo do candidato como filho
//             direto). Namespace só se os exemplos casados concordam; atributo (ex.: versao) só se
//             os exemplos DA MESMA FAMÍLIA DE VERSÃO do caso concordam. Senão: NÃO chuta, omite e
//             registra a limitação.
//   Regra   — folha `<N><xsl:value-of select="P"/></N>` do candidato, com P caminho simples, é
//             substituída pela expressão do exemplo SOMENTE se o exemplo tem o mesmo elemento N
//             (mesmo pai) cuja expressão referencia exatamente P (mesma origem → mesmo destino) e
//             nenhum exemplo discorda. Regra ambígua/sem suporte → registra, não muda.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Exemplo recuperado pelo RAG (par já validado/real).</summary>
/// <param name="Version">Versão do leiaute do exemplo (ex.: "2.06c") — define a "família" para consenso de atributos.</param>
public sealed record RefinerExample(string Id, string Version, double Similarity, string Xslt);

public sealed record RefineResult(string Xslt, IReadOnlyList<string> Actions, IReadOnlyList<string> Limitations)
{
    public bool Changed => Actions.Count > 0;
}

public static class ExampleCandidateRefiner
{
    private static readonly Regex PlainPath =
        new(@"^/?[A-Za-z_][\w.\-]*(?:/[A-Za-z_][\w.\-]*)*$", RegexOptions.Compiled);
    private static readonly Regex StringLiteral = new(@"'[^']*'|""[^""]*""", RegexOptions.Compiled);
    private static readonly Regex PathToken =
        new(@"(?<![\w.$@:/\-])/?[A-Za-z_][\w.\-]*(?:/[A-Za-z_][\w.\-]*)*", RegexOptions.Compiled);
    private static readonly Regex VersionFamilyRx = new(@"^\d+(?:\.\d+)?", RegexOptions.Compiled);

    public static RefineResult Refine(string candidateXslt, string caseVersion, IReadOnlyList<RefinerExample> examples)
    {
        var actions = new List<string>();
        var limitations = new List<string>();

        XDocument doc;
        try { doc = XDocument.Parse(candidateXslt); }
        catch (Exception ex)
        {
            limitations.Add($"Candidato não é XML bem-formado ({ex.Message}); nada refinado.");
            return new RefineResult(candidateXslt, actions, limitations);
        }

        var sheet = doc.Root;
        if (sheet is null || sheet.Name.Namespace != Xslt.Ns || (sheet.Name.LocalName != "stylesheet" && sheet.Name.LocalName != "transform"))
        {
            limitations.Add("Candidato não é um xsl:stylesheet; nada refinado.");
            return new RefineResult(candidateXslt, actions, limitations);
        }

        var parsedExamples = examples
            .OrderByDescending(e => e.Similarity)
            .Select(e => (Ex: e, Doc: TryParse(e.Xslt)))
            .Where(t => t.Doc is not null)
            .Select(t => (t.Ex, Doc: t.Doc!))
            .ToList();

        CompleteShell(sheet, caseVersion, parsedExamples, actions, limitations);
        TransplantFieldRules(sheet, parsedExamples, actions, limitations);

        if (actions.Count == 0)
            return new RefineResult(candidateXslt, actions, limitations);

        var text = doc.Declaration is null ? doc.ToString() : doc.Declaration + Environment.NewLine + doc.ToString();
        return new RefineResult(text, actions, limitations);
    }

    // ── Casca ────────────────────────────────────────────────────────────────

    private static void CompleteShell(
        XElement sheet, string caseVersion,
        List<(RefinerExample Ex, XDocument Doc)> examples,
        List<string> actions, List<string> limitations)
    {
        // Conteúdo literal solto direto sob o stylesheet (inválido em XSLT) + template raiz existente.
        var orphans = sheet.Elements().Where(e => e.Name.Namespace != Xslt.Ns).ToList();
        var template = sheet.Elements(Xslt.Ns + "template").FirstOrDefault(t => (string?)t.Attribute("match") == "/");

        var content = new List<XElement>(orphans);
        if (template is not null)
            content.AddRange(template.Elements().Where(e => e.Name.Namespace != Xslt.Ns));
        if (content.Count == 0) return; // nada literal para embrulhar (candidato só de instruções xsl)

        // Já tem raiz única no template? (e nenhum órfão)
        XElement? root = null;
        if (orphans.Count == 0 && template is not null)
        {
            var literals = template.Elements().Where(e => e.Name.Namespace != Xslt.Ns).ToList();
            if (literals.Count == 1) root = literals[0];
        }

        // Exemplos cuja estrutura casa: raiz tem o 1º elemento de conteúdo como filho direto
        // (ou, se o candidato já tem raiz, a raiz do exemplo tem o mesmo nome).
        var firstName = (root is null ? content[0] : root).Name.LocalName;
        var matching = new List<(RefinerExample Ex, XElement Root)>();
        foreach (var (ex, exDoc) in examples)
        {
            var exRoot = ReferenceRoot(exDoc);
            if (exRoot is null) continue;
            var ok = root is null
                ? exRoot.Elements().Any(c => c.Name.LocalName == firstName)
                : exRoot.Name.LocalName == firstName;
            if (ok) matching.Add((ex, exRoot));
        }

        if (matching.Count == 0)
        {
            limitations.Add("Casca do documento não determinada: nenhum exemplo recuperado tem estrutura compatível — candidato mantido sem raiz/namespace/versao.");
            return;
        }

        // Nome da raiz: consenso entre os exemplos casados.
        var rootNames = matching.Select(m => m.Root.Name.LocalName).Distinct().ToList();
        if (root is null && rootNames.Count > 1)
        {
            limitations.Add($"Raiz do documento ambígua entre exemplos ({string.Join(", ", rootNames)}) — não determinada.");
            return;
        }

        if (template is null)
        {
            template = new XElement(Xslt.Ns + "template", new XAttribute("match", "/"));
            sheet.Add(template);
            actions.Add("casca: criado <xsl:template match=\"/\"> (ausente no candidato)");
        }

        if (root is null)
        {
            root = new XElement(rootNames[0]);
            foreach (var c in content) { c.Remove(); root.Add(c); }
            template.Add(root);
            actions.Add(orphans.Count > 0
                ? $"casca: conteúdo solto sob xsl:stylesheet movido para <{rootNames[0]}> dentro do template raiz"
                : $"casca: elemento raiz <{rootNames[0]}> adicionado ao template");
        }

        // Namespace: só se o candidato não tem e os exemplos casados concordam.
        if (root.Name.Namespace == XNamespace.None)
        {
            var nss = matching.Select(m => m.Root.Name.NamespaceName).Distinct().ToList();
            if (nss.Count == 1 && nss[0].Length > 0)
            {
                var ns = XNamespace.Get(nss[0]);
                foreach (var el in root.DescendantsAndSelf().Where(e => e.Name.Namespace == XNamespace.None).ToList())
                    el.Name = ns + el.Name.LocalName;
                actions.Add($"casca: namespace '{nss[0]}' aplicado (exemplos casados concordam)");
            }
            else if (nss.Count > 1)
            {
                limitations.Add("Namespace do documento divergente entre exemplos — omitido (não chutado).");
            }
        }

        // Atributos constantes da raiz (ex.: versao): consenso na MESMA família de versão do caso.
        var family = Family(caseVersion);
        var sameFamily = matching.Where(m => family is not null && Family(m.Ex.Version) == family).ToList();
        var attrNames = matching.SelectMany(m => ConstantRootAttributes(m.Root).Keys).Distinct().ToList();
        foreach (var attr in attrNames)
        {
            if (root.Elements(Xslt.Ns + "attribute").Any(a => (string?)a.Attribute("name") == attr)
                || root.Attribute(attr) is not null)
                continue;

            var values = sameFamily
                .Select(m => ConstantRootAttributes(m.Root).GetValueOrDefault(attr))
                .Where(v => v is not null).Distinct().ToList();
            if (values.Count == 1)
            {
                var a = new XElement(Xslt.Ns + "attribute", new XAttribute("name", attr), values[0]);
                var last = root.Elements(Xslt.Ns + "attribute").LastOrDefault();
                if (last is not null) last.AddAfterSelf(a); else root.AddFirst(a);
                actions.Add($"casca: atributo {attr}=\"{values[0]}\" (exemplo(s) da família de versão {family} concordam)");
            }
            else
            {
                limitations.Add(sameFamily.Count == 0
                    ? $"Atributo '{attr}' da raiz omitido: nenhum exemplo recuperado é da família de versão {family ?? caseVersion} do caso (valor depende da versão)."
                    : $"Atributo '{attr}' da raiz omitido: exemplos da mesma família divergem no valor.");
            }
        }
    }

    /// <summary>Elemento raiz literal do template raiz de um XSLT de referência (null se não houver raiz única).</summary>
    private static XElement? ReferenceRoot(XDocument exDoc)
    {
        var tpl = exDoc.Root?.Elements(Xslt.Ns + "template").FirstOrDefault(t => (string?)t.Attribute("match") == "/");
        var lits = tpl?.Elements().Where(e => e.Name.Namespace != Xslt.Ns).ToList();
        return lits is { Count: 1 } ? lits[0] : null;
    }

    /// <summary>Atributos da raiz cujo valor é CONSTANTE (xsl:attribute com só texto/xsl:text, ou atributo literal).</summary>
    private static Dictionary<string, string> ConstantRootAttributes(XElement root)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in root.Elements(Xslt.Ns + "attribute"))
        {
            var name = (string?)a.Attribute("name");
            if (name is null) continue;
            var nodes = a.Nodes().ToList();
            string? val = null;
            if (nodes.Count == 1 && nodes[0] is XText t) val = t.Value.Trim();
            else if (nodes.Count == 1 && nodes[0] is XElement { Name: var n } te && n == Xslt.Ns + "text" && !te.HasElements) val = te.Value.Trim();
            if (!string.IsNullOrEmpty(val)) d[name] = val;
        }
        foreach (var a in root.Attributes().Where(a => !a.IsNamespaceDeclaration))
            d[a.Name.LocalName] = a.Value;
        return d;
    }

    private static string? Family(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var m = VersionFamilyRx.Match(version.Trim());
        return m.Success ? m.Value : null;
    }

    // ── Regras de campo demonstradas por exemplo ────────────────────────────

    private static void TransplantFieldRules(
        XElement sheet, List<(RefinerExample Ex, XDocument Doc)> examples,
        List<string> actions, List<string> limitations)
    {
        var template = sheet.Elements(Xslt.Ns + "template").FirstOrDefault(t => (string?)t.Attribute("match") == "/");
        if (template is null) return;

        foreach (var leaf in template.Descendants().Where(e => e.Name.Namespace != Xslt.Ns && !e.Elements().Any(c => c.Name.Namespace != Xslt.Ns)).ToList())
        {
            var vo = SoleValueOf(leaf);
            if (vo is null) continue;
            var p = ((string?)vo.Attribute("select"))?.Trim();
            if (p is null || !PlainPath.IsMatch(p)) continue;

            var name = leaf.Name.LocalName;
            var parent = leaf.Parent?.Name.LocalName;
            var pNorm = p.TrimStart('/');

            var wrapped = new List<string>();
            var conflict = false;
            foreach (var (_, exDoc) in examples)
            {
                foreach (var exLeaf in exDoc.Descendants().Where(e => e.Name.Namespace != Xslt.Ns
                             && e.Name.LocalName == name && e.Parent?.Name.LocalName == parent))
                {
                    var exVo = SoleValueOf(exLeaf);
                    var s = ((string?)exVo?.Attribute("select"))?.Trim();
                    if (s is null) continue;
                    var tokens = PathTokens(s);
                    if (tokens is null || tokens.Count != 1 || tokens[0] != pNorm) continue; // outra origem: não comparável

                    if (s.TrimStart('/') == pNorm) conflict = true;          // exemplo mostra a cópia simples
                    else if (!wrapped.Contains(s)) wrapped.Add(s);
                }
            }

            if (wrapped.Count == 0) continue;
            if (conflict || wrapped.Count > 1)
            {
                limitations.Add($"Regra do campo <{name}> ({pNorm}) ambígua nos exemplos ({(conflict ? "há exemplo com cópia simples" : $"{wrapped.Count} expressões distintas")}) — mantida a cópia simples.");
                continue;
            }

            vo.SetAttributeValue("select", wrapped[0]);
            actions.Add($"regra: <{name}> ← expressão demonstrada por exemplo recuperado (mesma origem {pNorm}): {wrapped[0]}");
        }
    }

    private static XElement? SoleValueOf(XElement leaf)
    {
        var kids = leaf.Nodes().Where(n => !(n is XText t && string.IsNullOrWhiteSpace(t.Value)) && n is not XComment).ToList();
        return kids.Count == 1 && kids[0] is XElement { Name: var n } e && n == Xslt.Ns + "value-of" ? e : null;
    }

    /// <summary>
    /// Caminhos referenciados por uma expressão XPath (literais removidos, nomes de função fora).
    /// null = expressão usa eixo/prefixo/variável (não comparável com segurança).
    /// </summary>
    internal static List<string>? PathTokens(string expr)
    {
        var stripped = StringLiteral.Replace(expr, "''");
        if (stripped.Contains(':') || stripped.Contains('$') || stripped.Contains('[') || stripped.Contains('@')) return null;

        var tokens = new List<string>();
        foreach (Match m in PathToken.Matches(stripped))
        {
            var after = stripped[(m.Index + m.Length)..].TrimStart();
            if (after.StartsWith('(')) continue; // nome de função
            tokens.Add(m.Value.TrimStart('/'));
        }
        return tokens;
    }

    private static XDocument? TryParse(string xml)
    {
        try { return XDocument.Parse(xml); }
        catch { return null; }
    }
}
