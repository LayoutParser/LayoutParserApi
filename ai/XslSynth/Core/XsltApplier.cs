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
    // B3 (cosmético): declaração mínima do mapeador Sysmiddle — sem encoding nem
    // standalone. O XmlWriter não sabe emiti-la assim (força encoding="utf-16" em
    // StringBuilder), então ela é prependada como literal no modo fiel ao gabarito.
    private const string DeclaracaoGabarito = "<?xml version=\"1.0\"?>";

    // B3 (cosmético, descoberto na verificação byte a byte): o serializador do
    // mapeador NUNCA emite tag self-closing — vazios saem como par aberto/fechado
    // (gabarito: <B2BDirectory></B2BDirectory>). O XmlWriter não tem opção para
    // isso via transform, então o modo fiel expande <x a="b"/> → <x a="b"></x>.
    // Seguro aqui: a saída vem do próprio XmlWriter (sem CDATA/comentário/PI).
    private static readonly System.Text.RegularExpressions.Regex SelfClosing = new(
        """<(\w[\w.-]*)((?:[^>"']|"[^"]*"|'[^']*')*?)\s*/>""",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <param name="fielAoGabarito">
    /// Quando true, reproduz a serialização do mapeador de produção (fase B3):
    /// declaração <c>&lt;?xml version="1.0"?&gt;</c> sem encoding/standalone e SEM
    /// indentação (o gabarito é linha única — respeita o indent="no" do xsl:output).
    /// Default false: saída indentada e sem declaração (diff/log dos demais fluxos).
    /// </param>
    public string Apply(XDocument xslt, XDocument input, bool fielAoGabarito = false)
    {
        var transform = new XslCompiledTransform(enableDebug: false);
        using (var xsltReader = xslt.CreateReader())
        {
            // Scripts e document() desabilitados por padrão (seguro/determinístico).
            transform.Load(xsltReader, XsltSettings.Default, null);
        }

        var settings = transform.OutputSettings?.Clone() ?? new XmlWriterSettings();
        settings.Indent = !fielAoGabarito;          // saída limpa para diff/log
        settings.OmitXmlDeclaration = true;         // declaração tratada abaixo

        var sb = new StringBuilder();
        using (var writer = XmlWriter.Create(sb, settings))
        using (var inputReader = input.CreateReader())
        {
            transform.Transform(inputReader, writer);
        }

        if (!fielAoGabarito) return sb.ToString();

        // No modo fiel: declaração colada ao conteúdo (sem newline) e vazios como
        // par aberto/fechado — exatamente como o mapeador de produção serializa.
        return DeclaracaoGabarito + SelfClosing.Replace(sb.ToString(), "<$1$2></$1>");
    }
}
