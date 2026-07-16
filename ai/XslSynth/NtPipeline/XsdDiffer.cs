using System.Globalization;
using System.Xml;
using System.Xml.Schema;

namespace XslSynth.NtPipeline;

// ─────────────────────────────────────────────────────────────────────────────
// XsdDiffer — S1 do pipeline "NT nova": snapshot diffável de um pacote XSD
// (arquivo principal + includes do MESMO diretório) e delta por XPath.
// 100% determinístico — sem LLM, sem rede além do filesystem local.
//
// DECISÃO (avaliada contra reusar XsdLeiauteIndex.Load — nt-pipeline-design §5 P-1):
//   o XsdLeiauteIndex é perfeito para o CATÁLOGO (ordem de documento + docs
//   PT-BR), mas para o DIFF tem 3 lacunas estruturais:
//     1. não resolve xs:include/xs:import — os facets de tamanho/pattern/enum
//        vivem em tiposBasico_v4.00.xsd / DFeTiposBasicos_v1.00.xsd;
//     2. descarta facets INLINE (guarda só a base da restrição) — mudança de
//        maxLength num elemento como ide/natOp seria invisível;
//     3. estendê-lo mexeria numa classe calibrada pelos fluxos --catalog e
//        --generate (recém-tocados pela B4) sem ganho: o diff não usa as
//        camadas semânticas (documentation/âncoras).
//   Portanto: System.Xml.Schema (XmlSchemaSet) — includes resolvidos a partir
//   da pasta do arquivo, tipos/ocorrências COMPILADOS e facets completos
//   (inclusive inline). O XsdLeiauteIndex permanece intocado.
//
// GRANULARIDADE (dedupe por construção):
//   • cada TIPO GLOBAL do namespace-alvo é caminhado UMA vez, com XPath
//     enraizado no nome do tipo ("TNFe/infNFe/ide/cUF");
//   • elemento que referencia tipo COMPLEXO nomeado vira folha (tipo
//     registrado, sem descida) — o conteúdo é diffado sob a raiz do próprio
//     tipo. Mudança em TNFe gera UM delta, sem eco em TEnviNFe/TNfeProc;
//   • tipos SIMPLES nomeados (TSerie, TDec_1302…) são nós próprios com
//     assinatura de facets — mudar tiposBasico gera UM FacetChanged no tipo,
//     não centenas nos campos que o usam. (S3 expande raiz-de-tipo → caminhos
//     de instância do catálogo.)
//
// LIMITE HONESTO: XPaths repetidos em ramos distintos de um xs:choice (ex.:
// IPI nos dois ramos de imposto) colapsam na 1ª ocorrência — mesmo critério
// do XsdLeiauteIndex; mudança SÓ no ramo sombreado não aparece. A quantidade
// colapsada fica visível em DuplicadosIgnorados.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Snapshot "diffável" de um pacote XSD (principal + includes).</summary>
public sealed class XsdSchemaSnapshot
{
    internal XsdSchemaSnapshot(
        string origem, List<XsdDiffNode> nodes, int duplicados, IReadOnlyList<string> avisos)
    {
        Origem = origem;
        Nodes = nodes;
        DuplicadosIgnorados = duplicados;
        Avisos = avisos;
        var byPath = new Dictionary<string, XsdDiffNode>(nodes.Count, StringComparer.Ordinal);
        foreach (var n in nodes)
            byPath.TryAdd(n.XPath, n);
        ByPath = byPath;
    }

    public string Origem { get; }
    public IReadOnlyList<XsdDiffNode> Nodes { get; }
    public IReadOnlyDictionary<string, XsdDiffNode> ByPath { get; }

    /// <summary>XPaths repetidos (ramos de choice) colapsados na 1ª ocorrência.</summary>
    public int DuplicadosIgnorados { get; }

    /// <summary>Avisos de validação do XmlSchemaSet (não derrubam o snapshot).</summary>
    public IReadOnlyList<string> Avisos { get; }
}

/// <summary>Carrega snapshots de pacotes XSD e computa o delta por XPath.</summary>
public static class XsdDiffer
{
    private const string DsNs = "http://www.w3.org/2000/09/xmldsig#";

    /// <summary>
    /// Compila o pacote (includes/imports resolvidos a partir da pasta do arquivo)
    /// e materializa os nós em ordem de documento (principal → includes, DFS).
    /// </summary>
    public static XsdSchemaSnapshot LoadSnapshot(string xsdPath)
    {
        if (!File.Exists(xsdPath))
            throw new FileNotFoundException($"XSD não encontrado: {xsdPath}", xsdPath);

        var avisos = new List<string>();
        var set = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
        set.ValidationEventHandler += (_, e) => avisos.Add($"{e.Severity}: {e.Message}");

        var main = set.Add(null, Path.GetFullPath(xsdPath))
            ?? throw new InvalidOperationException("XmlSchemaSet devolveu schema nulo.");
        set.Compile();

        var walker = new Walker(main.TargetNamespace ?? "");
        foreach (var doc in DocsEmOrdem(main))
            walker.WalkGlobais(doc);

        return new XsdSchemaSnapshot(Path.GetFullPath(xsdPath), walker.Nodes, walker.Duplicados, avisos);
    }

    /// <summary>Delta por XPath: Added/Removed/TypeChanged/OccurrenceChanged/FacetChanged.</summary>
    public static XsdDelta Diff(XsdSchemaSnapshot velho, XsdSchemaSnapshot novo)
    {
        var entradas = new List<XsdDeltaEntry>();

        // Ordem de documento do NOVO primeiro (Added/alterações), removidos ao final.
        foreach (var n in novo.Nodes)
        {
            if (!velho.ByPath.TryGetValue(n.XPath, out var v))
            {
                entradas.Add(new(XsdDeltaKind.Added, n.XPath, null, Descreve(n)));
                continue;
            }

            var mesmoTipo = string.Equals(v.TypeName, n.TypeName, StringComparison.Ordinal);
            if (!mesmoTipo)
                entradas.Add(new(XsdDeltaKind.TypeChanged, n.XPath, v.TypeName, n.TypeName));
            if (!string.Equals(v.Occurs, n.Occurs, StringComparison.Ordinal))
                entradas.Add(new(XsdDeltaKind.OccurrenceChanged, n.XPath, v.Occurs, n.Occurs));
            // Tipo mudou ⇒ facets mudam por consequência; FacetChanged só com tipo estável.
            if (mesmoTipo && !string.Equals(v.Facets, n.Facets, StringComparison.Ordinal))
            {
                var (antes, depois) = DiffFacets(v.Facets, n.Facets);
                entradas.Add(new(XsdDeltaKind.FacetChanged, n.XPath, antes, depois));
            }
        }

        foreach (var v in velho.Nodes)
            if (!novo.ByPath.ContainsKey(v.XPath))
                entradas.Add(new(XsdDeltaKind.Removed, v.XPath, Descreve(v), null));

        var resumo = new Dictionary<string, int>();
        foreach (var k in Enum.GetValues<XsdDeltaKind>())
            resumo[k.ToString()] = entradas.Count(e => e.Kind == k);

        return new XsdDelta
        {
            XsdVelho = velho.Origem,
            XsdNovo = novo.Origem,
            NosVelho = velho.Nodes.Count,
            NosNovo = novo.Nodes.Count,
            Resumo = resumo,
            Entradas = entradas,
        };
    }

    // ── Helpers do diff ───────────────────────────────────────────────────────

    private static string Descreve(XsdDiffNode n) =>
        $"{n.Kind} tipo={n.TypeName} occurs={n.Occurs}"
        + (n.Facets.Length > 0 ? $" facets[{n.Facets}]" : "");

    /// <summary>Compacta o FacetChanged: só os facets que saíram/entraram, não a assinatura toda
    /// (evita listar 400 enumerações de um TCListServ quando só uma mudou).</summary>
    private static (string Antes, string Depois) DiffFacets(string velho, string novo)
    {
        var ve = velho.Split("; ", StringSplitOptions.RemoveEmptyEntries);
        var no = novo.Split("; ", StringSplitOptions.RemoveEmptyEntries);
        var so = string.Join("; ", ve.Except(no, StringComparer.Ordinal));
        var sn = string.Join("; ", no.Except(ve, StringComparer.Ordinal));
        return (so.Length > 0 ? so : "(nenhum)", sn.Length > 0 ? sn : "(nenhum)");
    }

    /// <summary>Documentos do pacote no namespace-alvo, em ordem: principal → includes (DFS).</summary>
    private static List<XmlSchema> DocsEmOrdem(XmlSchema main)
    {
        var docs = new List<XmlSchema>();
        var vistos = new HashSet<XmlSchema>();
        void Coleta(XmlSchema s)
        {
            if (!vistos.Add(s)) return;
            docs.Add(s);
            foreach (var ext in s.Includes.OfType<XmlSchemaExternal>())
            {
                // Import de namespace estranho (ds:) fica fora do domínio do diff.
                if (ext is XmlSchemaImport imp && imp.Namespace != main.TargetNamespace) continue;
                if (ext.Schema is { } inc) Coleta(inc);
            }
        }
        Coleta(main);
        return docs;
    }

    // ── Caminhada (uma instância por snapshot, guarda estado do dedupe) ───────

    private sealed class Walker(string targetNs)
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        public List<XsdDiffNode> Nodes { get; } = new(4096);
        public int Duplicados { get; private set; }

        public void WalkGlobais(XmlSchema doc)
        {
            foreach (var item in doc.Items.OfType<XmlSchemaObject>())
            {
                switch (item)
                {
                    case XmlSchemaElement el:
                        WalkElemento(el, parentPath: null, depth: 0);
                        break;

                    case XmlSchemaComplexType { Name: not null } ct:
                    {
                        var (tipo, facets) = DescreveTipoComplexo(ct);
                        if (Add(ct.Name, XsdNodeKind.TipoComplexo, tipo, "global", facets))
                            WalkCorpoComplexo(ct, ct.Name, depth: 1);
                        break;
                    }

                    case XmlSchemaSimpleType { Name: not null } st:
                        Add(st.Name, XsdNodeKind.TipoSimples, BaseDe(st), "global", FacetSignature(st));
                        break;
                }
            }
        }

        private void WalkElemento(XmlSchemaElement el, string? parentPath, int depth)
        {
            if (depth > 64) return;   // guarda-corpo (leiaute real tem ~12 níveis)

            var qn = el.QualifiedName;   // pós-compilação: nome próprio OU do ref resolvido
            var nome = qn.Namespace == targetNs || qn.Namespace.Length == 0 ? qn.Name : Label(qn);
            var path = parentPath is null ? nome : $"{parentPath}/{nome}";
            var occurs = parentPath is null ? "global" : OccursOf(el);

            string tipo;
            var facets = "";
            XmlSchemaComplexType? inline = null;

            if (!el.SchemaTypeName.IsEmpty)
            {
                tipo = Label(el.SchemaTypeName);
            }
            else
            {
                switch (el.ElementSchemaType)
                {
                    case XmlSchemaComplexType ct when ct.QualifiedName.IsEmpty:
                        tipo = "(complexo inline)";
                        inline = ct;
                        break;
                    case XmlSchemaSimpleType st when st.QualifiedName.IsEmpty:
                        tipo = $"(simples inline: {BaseDe(st)})";
                        facets = FacetSignature(st);
                        break;
                    case { } t when !t.QualifiedName.IsEmpty:
                        tipo = Label(t.QualifiedName);   // ref → tipo do elemento global
                        break;
                    default:
                        tipo = "xs:anyType";
                        break;
                }
            }

            if (!Add(path, XsdNodeKind.Elemento, tipo, occurs, facets)) return;

            // Só desce em complexo ANÔNIMO — tipo nomeado é diffado sob a própria raiz.
            if (inline is not null)
                WalkCorpoComplexo(inline, path, depth + 1);
        }

        private void WalkCorpoComplexo(XmlSchemaComplexType ct, string path, int depth)
        {
            if (depth > 64) return;

            // Atributos EFETIVOS (pós-compilação, inclui herdados); ordenados p/ determinismo.
            foreach (var at in ct.AttributeUses.Values.OfType<XmlSchemaAttribute>()
                         .OrderBy(a => a.QualifiedName.Name, StringComparer.Ordinal))
            {
                var occ = at.Use == XmlSchemaUse.Required ? "1-1" : "0-1";
                string tipo;
                var facets = "";
                if (!at.SchemaTypeName.IsEmpty)
                {
                    tipo = Label(at.SchemaTypeName);
                }
                else if (at.AttributeSchemaType is { } ast && ast.QualifiedName.IsEmpty)
                {
                    tipo = $"(simples inline: {BaseDe(ast)})";
                    facets = FacetSignature(ast);
                }
                else if (at.AttributeSchemaType is { } nomeado)
                {
                    tipo = Label(nomeado.QualifiedName);
                }
                else
                {
                    tipo = "xs:anySimpleType";
                }
                Add($"{path}/@{at.QualifiedName.Name}", XsdNodeKind.Atributo, tipo, occ, facets);
            }

            WalkParticula(ct.ContentTypeParticle, path, depth);
        }

        private void WalkParticula(XmlSchemaParticle? particula, string path, int depth)
        {
            switch (particula)
            {
                case XmlSchemaElement el:
                    WalkElemento(el, path, depth);
                    break;
                case XmlSchemaGroupBase grupo:   // sequence | choice | all
                    foreach (var filho in grupo.Items.OfType<XmlSchemaParticle>())
                        WalkParticula(filho, path, depth);
                    break;
                case XmlSchemaGroupRef gr:
                    WalkParticula(gr.Particle, path, depth);
                    break;
                case XmlSchemaAny any:
                    Add($"{path}/xs:any", XsdNodeKind.Elemento, "(any)", OccursOf(any), "");
                    break;
                    // null / EmptyParticle: sem conteúdo de elemento.
            }
        }

        private bool Add(string path, XsdNodeKind kind, string tipo, string occurs, string facets)
        {
            if (!_seen.Add(path)) { Duplicados++; return false; }
            Nodes.Add(new XsdDiffNode(Nodes.Count, path, kind, tipo, occurs, facets));
            return true;
        }
    }

    // ── Helpers de tipo/facet (compartilhados pela caminhada) ─────────────────

    private static string Label(XmlQualifiedName qn) => qn.Namespace switch
    {
        XmlSchema.Namespace => $"xs:{qn.Name}",
        DsNs => $"ds:{qn.Name}",
        _ => qn.Name,
    };

    private static string OccursOf(XmlSchemaParticle p)
    {
        var min = p.MinOccurs.ToString("G29", CultureInfo.InvariantCulture);
        var max = p.MaxOccurs == decimal.MaxValue
            ? "N"
            : p.MaxOccurs.ToString("G29", CultureInfo.InvariantCulture);
        return $"{min}-{max}";
    }

    private static string BaseDe(XmlSchemaSimpleType st) => st.Content switch
    {
        XmlSchemaSimpleTypeRestriction r when !r.BaseTypeName.IsEmpty => Label(r.BaseTypeName),
        XmlSchemaSimpleTypeList => "xs:list",
        XmlSchemaSimpleTypeUnion => "xs:union",
        _ => "(restrição inline)",
    };

    /// <summary>Assinatura: base + facets PRÓPRIOS, em ordem de documento. Facets da base
    /// nomeada NÃO são expandidos — a base é diffada sob a própria raiz (localidade).</summary>
    private static string FacetSignature(XmlSchemaSimpleType st)
    {
        switch (st.Content)
        {
            case XmlSchemaSimpleTypeRestriction r:
            {
                var parts = new List<string>
                {
                    r.BaseTypeName.IsEmpty
                        ? r.BaseType is { } aninhada ? $"base=({FacetSignature(aninhada)})" : "base=(inline)"
                        : $"base={Label(r.BaseTypeName)}",
                };
                foreach (var f in r.Facets.OfType<XmlSchemaFacet>())
                    parts.Add($"{FacetKind(f)}={f.Value}");
                return string.Join("; ", parts);
            }
            case XmlSchemaSimpleTypeList l:
                return l.ItemTypeName.IsEmpty ? "list(inline)" : $"list(item={Label(l.ItemTypeName)})";
            case XmlSchemaSimpleTypeUnion u:
                return $"union({string.Join(",", (u.MemberTypes ?? []).Select(Label))})";
            default:
                return "";
        }
    }

    private static string FacetKind(XmlSchemaFacet f) => f switch
    {
        XmlSchemaLengthFacet => "length",
        XmlSchemaMinLengthFacet => "minLength",
        XmlSchemaMaxLengthFacet => "maxLength",
        XmlSchemaPatternFacet => "pattern",
        XmlSchemaEnumerationFacet => "enumeration",
        XmlSchemaWhiteSpaceFacet => "whiteSpace",
        XmlSchemaTotalDigitsFacet => "totalDigits",
        XmlSchemaFractionDigitsFacet => "fractionDigits",
        XmlSchemaMinInclusiveFacet => "minInclusive",
        XmlSchemaMaxInclusiveFacet => "maxInclusive",
        XmlSchemaMinExclusiveFacet => "minExclusive",
        XmlSchemaMaxExclusiveFacet => "maxExclusive",
        _ => f.GetType().Name,
    };

    /// <summary>Tipo "declarado" de um complexType global (base de extensão/restrição) +
    /// facets de simpleContent, quando houver.</summary>
    private static (string Tipo, string Facets) DescreveTipoComplexo(XmlSchemaComplexType ct)
    {
        switch (ct.ContentModel)
        {
            case XmlSchemaSimpleContent sc:
                return sc.Content switch
                {
                    XmlSchemaSimpleContentExtension e => ($"simpleContent extends {Label(e.BaseTypeName)}", ""),
                    XmlSchemaSimpleContentRestriction r => (
                        $"simpleContent restricts {Label(r.BaseTypeName)}",
                        string.Join("; ", r.Facets.OfType<XmlSchemaFacet>()
                            .Select(f => $"{FacetKind(f)}={f.Value}"))),
                    _ => ("simpleContent", ""),
                };
            case XmlSchemaComplexContent cc:
                return cc.Content switch
                {
                    XmlSchemaComplexContentExtension e => ($"extends {Label(e.BaseTypeName)}", ""),
                    XmlSchemaComplexContentRestriction r => ($"restricts {Label(r.BaseTypeName)}", ""),
                    _ => ("complexContent", ""),
                };
            default:
                return ("(complexo)", "");
        }
    }
}
