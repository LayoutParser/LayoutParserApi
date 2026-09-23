using XslSynth.Model;

namespace XslSynth.Core;

// ─────────────────────────────────────────────────────────────────────────────
// DocumentShell — issue #438 (casca do documento gerado).
//
// PROBLEMA REAL (medido em 71 artefatos de produção de dbo.tbGeneratedMapperArtifact):
// o CandidateBuilder já emitia <xsl:template match="/"> + elemento raiz, mas tratava TODA
// regra T.<path> como ELEMENTO. No DSL Sysmiddle não há marca de atributo no path
// (T.inutNFe/versao, T.inutNFe/xmlns, T.enviNFe/NFe/infNFe/Id) — o que distingue é o tipo do
// nó de destino no layout (prefixo ATT_ do TargetElementGuid, ou AttributeElementVO no
// LayoutVO). Resultado: 16 artefatos com <versao> filho, 11 com <xmlns> filho e 14 com <Id>
// filho — XML inválido/sem namespace, mesmo o DSL declarando os valores.
//
// PRINCÍPIO: nada aqui é inventado. Namespace e versão só saem de uma regra do PRÓPRIO
// mapeador (constante literal). Atributo é reconhecido por (a) TargetType ATT da regra,
// (b) AttributeElementVO do layout de destino, (c) o nome reservado "xmlns" (que jamais é
// nome de elemento válido). Se um valor de namespace não é constante, NÃO é chutado: a casca
// sai sem ele e a limitação é registrada.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Insumos opcionais para o <see cref="CandidateBuilder"/> montar a casca do documento.</summary>
/// <param name="AttributeTargetPaths">Caminhos (normalizados por <see cref="Normalize"/>) que são ATRIBUTOS no layout de destino.</param>
public sealed record DocumentShellOptions(IReadOnlySet<string>? AttributeTargetPaths = null)
{
    public static readonly DocumentShellOptions Empty = new();

    /// <summary>Monta as opções a partir do catálogo do layout de destino (nullable — degrade gracioso).</summary>
    public static DocumentShellOptions From(GuidXPathCatalog? targetCatalog)
    {
        if (targetCatalog is null || targetCatalog.Count == 0) return Empty;

        var attrs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in targetCatalog.Entries.Where(e => e.IsAttribute))
            attrs.Add(Normalize(e.XPath));
        return new DocumentShellOptions(attrs);
    }

    /// <summary>
    /// Raiz única do layout de destino, quando inequívoca (1 primeiro segmento distinto entre os
    /// nós não-atributo). null = layout ausente, vazio ou multi-raiz (ex.: layout posicional com várias LINHAs).
    /// </summary>
    public static string? SingleRoot(GuidXPathCatalog? targetCatalog)
    {
        if (targetCatalog is null || targetCatalog.Count == 0) return null;
        var roots = targetCatalog.Entries
            .Where(e => !e.IsAttribute)
            .Select(e => Xslt.Segments(e.XPath).FirstOrDefault())
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToList();
        return roots.Count == 1 ? roots[0] : null;
    }

    /// <summary>Remove '@' e os wrappers estruturais Choice/Sequence, para comparar path do DSL com path do LayoutVO.</summary>
    public static string Normalize(string path) =>
        string.Join('/', Xslt.Segments(path)
            .Select(s => s.TrimStart('@'))
            .Where(s => s != "Choice" && s != "Sequence"));

    /// <summary>
    /// <paramref name="tr"/> é ATRIBUTO? (a) o nó da própria regra é ATT_ e o path é o da regra;
    /// (b) o layout de destino declara esse path como atributo; (c) nome reservado xmlns.
    /// </summary>
    public bool IsAttribute(Synthesis.RuleTranslation tr)
    {
        if (string.IsNullOrWhiteSpace(tr.TargetPath)) return false;
        var leaf = Xslt.Segments(tr.TargetPath).LastOrDefault();
        if (leaf is null) return false;
        if (IsNamespaceDeclaration(leaf)) return true;

        var norm = Normalize(tr.TargetPath);
        if (string.Equals(tr.Rule.TargetType, "ATT", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(tr.Rule.TargetPath)
            && Normalize(tr.Rule.TargetPath) == norm)
            return true;

        return AttributeTargetPaths?.Contains(norm) == true;
    }

    /// <summary><c>xmlns</c> e <c>xmlns_xxx</c>/<c>xmlns:xxx</c>: declaração de namespace, nunca elemento.</summary>
    public static bool IsNamespaceDeclaration(string leaf) =>
        leaf == "xmlns" || leaf.StartsWith("xmlns_", StringComparison.Ordinal) || leaf.StartsWith("xmlns:", StringComparison.Ordinal);
}

/// <summary>O que a casca do documento acabou tendo (vai no relatório de cobertura do artefato).</summary>
/// <param name="RootElement">Elemento raiz literal emitido.</param>
/// <param name="Namespace">Namespace do documento, só se declarado como constante no mapeador; senão null.</param>
/// <param name="Attributes">Atributos emitidos via xsl:attribute (caminho → nome).</param>
public sealed record ShellInfo(string RootElement, string? Namespace, IReadOnlyList<string> Attributes);
