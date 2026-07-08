using System.Xml.Linq;

namespace XslSynth.Core;

/// <summary>Uma divergência node-a-node, com o XPath exato — combustível do loop de reparo.</summary>
public sealed record NodeDiff(string Kind, string XPath, string? Expected, string? Actual)
{
    public override string ToString() =>
        Kind switch
        {
            "missing" => $"[FALTA]   {XPath} (esperado: <{Expected}>)",
            "extra" => $"[SOBRA]   {XPath} (obtido: <{Actual}>)",
            "text" => $"[TEXTO]   {XPath} — esperado='{Expected}' obtido='{Actual}'",
            "name" => $"[NOME]    {XPath} — esperado='{Expected}' obtido='{Actual}'",
            "attr" => $"[ATRIB]   {XPath} — esperado='{Expected}' obtido='{Actual}'",
            _ => $"[{Kind}]  {XPath} — esperado='{Expected}' obtido='{Actual}'"
        };
}

/// <summary>
/// Passo 5 do loop (código): diff CANÔNICO node-a-node.
/// Normaliza espaços, ordem de atributos e namespaces, e reporta os XPaths divergentes.
/// É o que faz o loop convergir (o LLM conserta exatamente o que o diff aponta).
/// </summary>
public sealed class CanonicalDiffer
{
    public IReadOnlyList<NodeDiff> Diff(string expectedXml, string actualXml)
    {
        var expected = Normalize(XDocument.Parse(expectedXml).Root!);
        var actual = Normalize(XDocument.Parse(actualXml).Root!);

        var diffs = new List<NodeDiff>();
        CompareElement(expected, actual, "/" + expected.Name.LocalName, diffs);
        return diffs;
    }

    /// <summary>Remove nós de texto compostos só por espaço (whitespace insignificante).</summary>
    private static XElement Normalize(XElement source)
    {
        var clone = new XElement(source.Name);

        foreach (var attr in source.Attributes().Where(a => !a.IsNamespaceDeclaration))
            clone.Add(new XAttribute(attr.Name, attr.Value));

        var childElements = source.Elements().ToList();
        if (childElements.Count > 0)
        {
            foreach (var child in childElements)
                clone.Add(Normalize(child));
        }
        else
        {
            var text = source.Value.Trim();
            if (text.Length > 0)
                clone.Value = text;
        }

        return clone;
    }

    private static void CompareElement(XElement expected, XElement actual, string xpath, List<NodeDiff> diffs)
    {
        // Nome (localname + namespace).
        if (expected.Name != actual.Name)
        {
            diffs.Add(new NodeDiff("name", xpath, expected.Name.LocalName, actual.Name.LocalName));
            return;
        }

        // Atributos (ordem canonizada = comparação por nome, independente de ordem).
        CompareAttributes(expected, actual, xpath, diffs);

        var expChildren = expected.Elements().ToList();
        var actChildren = actual.Elements().ToList();

        // Folha (sem filhos elementos): compara texto direto.
        if (expChildren.Count == 0 && actChildren.Count == 0)
        {
            var expText = expected.Value.Trim();
            var actText = actual.Value.Trim();
            if (expText != actText)
                diffs.Add(new NodeDiff("text", xpath, expText, actText));
            return;
        }

        // Filhos: comparação posicional (correta para XML ordenado por schema, como NF-e).
        var max = Math.Max(expChildren.Count, actChildren.Count);
        for (var i = 0; i < max; i++)
        {
            var e = i < expChildren.Count ? expChildren[i] : null;
            var a = i < actChildren.Count ? actChildren[i] : null;

            if (e is not null && a is null)
                diffs.Add(new NodeDiff("missing", ChildPath(xpath, e, expChildren), e.Name.LocalName, null));
            else if (e is null && a is not null)
                diffs.Add(new NodeDiff("extra", ChildPath(xpath, a, actChildren), null, a.Name.LocalName));
            else if (e is not null && a is not null)
                CompareElement(e, a, ChildPath(xpath, e, expChildren), diffs);
        }
    }

    private static void CompareAttributes(XElement expected, XElement actual, string xpath, List<NodeDiff> diffs)
    {
        var expAttrs = expected.Attributes().Where(a => !a.IsNamespaceDeclaration)
            .ToDictionary(a => a.Name, a => a.Value);
        var actAttrs = actual.Attributes().Where(a => !a.IsNamespaceDeclaration)
            .ToDictionary(a => a.Name, a => a.Value);

        foreach (var (name, expValue) in expAttrs)
        {
            if (!actAttrs.TryGetValue(name, out var actValue))
                diffs.Add(new NodeDiff("attr", $"{xpath}/@{name.LocalName}", expValue, null));
            else if (expValue != actValue)
                diffs.Add(new NodeDiff("attr", $"{xpath}/@{name.LocalName}", expValue, actValue));
        }

        foreach (var (name, actValue) in actAttrs)
        {
            if (!expAttrs.ContainsKey(name))
                diffs.Add(new NodeDiff("attr", $"{xpath}/@{name.LocalName}", null, actValue));
        }
    }

    /// <summary>Monta o XPath do filho, com índice [n] quando há irmãos de mesmo nome.</summary>
    private static string ChildPath(string parentPath, XElement child, IReadOnlyList<XElement> siblings)
    {
        var sameName = siblings.Where(s => s.Name == child.Name).ToList();
        if (sameName.Count <= 1)
            return $"{parentPath}/{child.Name.LocalName}";

        var index = sameName.IndexOf(child) + 1;
        return $"{parentPath}/{child.Name.LocalName}[{index}]";
    }
}
