using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Xsl;

namespace XslSynth.Core;

/// <summary>
/// Passo 4 do loop (código): aplica o XSLT no input.
/// MVP usa System.Xml.Xsl.XslCompiledTransform (XSLT 1.0, cross-platform).
/// PRODUÇÃO: trocar por Saxon (Saxon-HE/SaxonCS) para XSLT 2.0/3.0 exigido pela NF-e.
/// Ver docs/architecture/ia-xslt-synthesis.md §8.
/// </summary>
public sealed class XsltApplier
{
    public string Apply(XDocument xslt, XDocument input)
    {
        var transform = new XslCompiledTransform(enableDebug: false);
        using (var xsltReader = xslt.CreateReader())
        {
            // Scripts e document() desabilitados por padrão (seguro/determinístico).
            transform.Load(xsltReader, XsltSettings.Default, null);
        }

        var settings = transform.OutputSettings?.Clone() ?? new XmlWriterSettings();
        settings.Indent = true;
        settings.OmitXmlDeclaration = true; // saída limpa para diff/log

        var sb = new StringBuilder();
        using (var writer = XmlWriter.Create(sb, settings))
        using (var inputReader = input.CreateReader())
        {
            transform.Transform(inputReader, writer);
        }

        return sb.ToString();
    }
}
