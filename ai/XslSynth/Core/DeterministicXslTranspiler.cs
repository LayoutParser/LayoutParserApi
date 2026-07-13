using System.Xml.Linq;
using XslSynth.Model;

namespace XslSynth.Core;

/// <summary>Resultado da transpilação determinística.</summary>
public sealed record TranspileResult(XDocument Xslt, int MappedFields, string RootName);

/// <summary>
/// Passo 2 do loop (código): LinkMappings → XSLT baseline, SEM IA.
/// Resolve a maior parte dos campos de forma barata e 100% confiável.
/// </summary>
public sealed class DeterministicXslTranspiler
{
    public TranspileResult Transpile(MapperVo mapper)
    {
        var rootName = DetermineRootName(mapper);
        var literalRoot = new XElement(rootName);

        var mapped = 0;
        foreach (var link in mapper.LinkMappings.OrderBy(m => m.Sequence))
        {
            if (string.IsNullOrWhiteSpace(link.TargetPath) || string.IsNullOrWhiteSpace(link.SourcePath))
                continue;

            var segs = Xslt.Segments(link.TargetPath);
            if (segs.Length == 0 || segs[0] != rootName)
                continue;

            // Cria/reaproveita a hierarquia até o pai; a folha é sempre nova.
            var parent = GetOrCreateChildPath(literalRoot, segs[..^1]);
            var leaf = new XElement(segs[^1]);
            AddValueContent(leaf, link);
            parent.Add(leaf);
            mapped++;
        }

        var doc = new XDocument(
            new XElement(Xslt.Ns + "stylesheet",
                new XAttribute("version", "1.0"),
                new XAttribute(XNamespace.Xmlns + "xsl", Xslt.Ns.NamespaceName),
                new XElement(Xslt.Ns + "output",
                    new XAttribute("method", "xml"),
                    new XAttribute("indent", "yes")),
                new XElement(Xslt.Ns + "template",
                    new XAttribute("match", "/"),
                    literalRoot)));

        return new TranspileResult(doc, mapped, rootName);
    }

    /// <summary>Gera o conteúdo XSLT de uma folha a partir de um LinkMapping.</summary>
    private static void AddValueContent(XElement leaf, LinkMappingItem link)
    {
        var select = link.SourcePath!;

        // RemoveWhiteSpaceType != None/vazio → normalize-space (apara e colapsa espaços).
        if (IsWhitespaceStrip(link.RemoveWhiteSpaceType))
            select = $"normalize-space({select})";

        if (!string.IsNullOrEmpty(link.DefaultValue))
        {
            // Fallback: se a origem vier vazia, usa DefaultValue.
            leaf.Add(new XElement(Xslt.Ns + "choose",
                new XElement(Xslt.Ns + "when",
                    new XAttribute("test", $"string({link.SourcePath})=''"),
                    new XText(link.DefaultValue)),
                new XElement(Xslt.Ns + "otherwise",
                    new XElement(Xslt.Ns + "value-of", new XAttribute("select", select)))));
        }
        else
        {
            leaf.Add(new XElement(Xslt.Ns + "value-of", new XAttribute("select", select)));
        }
    }

    private static bool IsWhitespaceStrip(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return false;
        return type.Trim().ToLowerInvariant() switch
        {
            "trim" or "all" or "both" or "left" or "right" => true,
            _ => false
        };
    }

    /// <summary>
    /// Navega (criando o que faltar) a partir da raiz literal seguindo os segmentos.
    /// segments[0] deve ser o nome da raiz. Retorna o elemento do último segmento.
    /// Reutilizado pelo orquestrador para inserir fragmentos de regra.
    /// </summary>
    public static XElement GetOrCreateChildPath(XElement root, IReadOnlyList<string> segments)
    {
        var current = root;
        for (var i = 1; i < segments.Count; i++)
        {
            var name = segments[i];
            var child = current.Elements().FirstOrDefault(e => e.Name.LocalName == name);
            if (child is null)
            {
                child = new XElement(name);
                current.Add(child);
            }
            current = child;
        }
        return current;
    }

    private static string DetermineRootName(MapperVo mapper)
    {
        var firstTarget =
            mapper.LinkMappings.OrderBy(m => m.Sequence)
                .Select(m => m.TargetPath)
                .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p))
            ?? mapper.Rules.Select(r => r.TargetPath)
                .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));

        if (string.IsNullOrWhiteSpace(firstTarget))
            throw new InvalidOperationException("Mapeador sem alvos: impossível inferir a raiz de saída.");

        return Xslt.Segments(firstTarget)[0];
    }
}
