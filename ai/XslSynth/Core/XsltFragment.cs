using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Xsl;

namespace XslSynth.Core;

/// <summary>
/// Utilitários para manipular FRAGMENTOS de XSLT (corpos de regra/folha) de forma
/// segura: parsear, coletar variáveis referenciadas e checar se COMPILAM isolados.
/// A checagem por fragmento garante que o candidato final sempre carregue no
/// <see cref="XslCompiledTransform"/> (nenhum corpo malformado do LLM entra na árvore).
/// </summary>
public static class XsltFragment
{
    private static readonly Regex VarRef = new(@"\$([A-Za-z_][A-Za-z0-9_.\-]*)", RegexOptions.Compiled);

    /// <summary>Nomes de variáveis referenciadas (<c>$nome</c>) num texto XSLT.</summary>
    public static IReadOnlyCollection<string> ReferencedVars(string text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        return VarRef.Matches(text).Select(m => SanitizeVar(m.Groups[1].Value)).Distinct().ToArray();
    }

    /// <summary>Normaliza um nome de variável para NCName válido (troca '.'/'-' por '_').</summary>
    public static string SanitizeVar(string name) => Regex.Replace(name, "[.\\-]", "_");

    /// <summary>
    /// Faz o parse do corpo do fragmento (pode ter múltiplos nós) devolvendo os nós
    /// filhos. Envolve num root temporário com o namespace xsl declarado.
    /// </summary>
    public static IReadOnlyList<XNode> ParseNodes(string bodyXsl)
    {
        var wrapped = $"<frag xmlns:xsl=\"{Xslt.Ns.NamespaceName}\">{bodyXsl}</frag>";
        var el = XElement.Parse(wrapped, LoadOptions.PreserveWhitespace);
        return el.Nodes().ToList();
    }

    /// <summary>
    /// Testa se o corpo COMPILA como XSLT 1.0, isolado num stylesheet mínimo com as
    /// variáveis referenciadas declaradas. Retorna false (e a mensagem) se não.
    /// </summary>
    public static bool Compiles(string bodyXsl, out string? error)
    {
        error = null;
        try
        {
            var vars = ReferencedVars(bodyXsl)
                .Select(v => new XElement(Xslt.Ns + "variable",
                    new XAttribute("name", v), new XAttribute("select", "''")))
                .Cast<object>()
                .ToArray();

            var stylesheet = new XElement(Xslt.Ns + "stylesheet",
                new XAttribute("version", "1.0"),
                new XAttribute(XNamespace.Xmlns + "xsl", Xslt.Ns.NamespaceName),
                new XElement(Xslt.Ns + "output", new XAttribute("method", "xml")),
                vars,
                new XElement(Xslt.Ns + "template", new XAttribute("match", "/"),
                    new XElement("probe", ParseNodes(bodyXsl))));

            var doc = new XDocument(stylesheet);
            var transform = new XslCompiledTransform();
            using var reader = doc.CreateReader();
            transform.Load(reader, XsltSettings.Default, null);
            return true;
        }
        catch (Exception ex) // XmlException, XsltException, etc.
        {
            error = ex.Message;
            return false;
        }
    }
}
