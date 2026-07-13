using System.Xml.Linq;

namespace XslSynth.Core;

/// <summary>Constantes/utilitários compartilhados da síntese de XSLT.</summary>
internal static class Xslt
{
    /// <summary>Namespace do XSLT 1.0.</summary>
    public static readonly XNamespace Ns = "http://www.w3.org/1999/XSL/Transform";

    /// <summary>Quebra um XPath simples ("/A/B/C") em segmentos, ignorando vazios.</summary>
    public static string[] Segments(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries);
}
