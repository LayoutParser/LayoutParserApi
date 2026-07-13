using System.Text.RegularExpressions;
using System.Xml.Linq;
using XslSynth.Model;

namespace XslSynth.Core;

/// <summary>Resultado da transpilação dos LinkMappings reais.</summary>
/// <param name="Leaves">Elementos-folha XSLT (um por link), prontos p/ o candidato.</param>
/// <param name="Count">Quantos links viraram folha.</param>
/// <param name="Skipped">Links sem nome de folha derivável (não transpilados).</param>
public sealed record LinkTranspileResult(IReadOnlyList<XElement> Leaves, int Count, int Skipped);

/// <summary>
/// Transpila os 237 <see cref="LinkMappingItem"/> reais → folhas XSLT, por CÓDIGO
/// (determinístico, sem IA). Aplica <c>RemoveWhiteSpaceType</c> (→ normalize-space),
/// <c>IsToTruncateValue</c>, <c>DefaultValue</c> (→ xsl:choose com fallback) e
/// <c>AllowEmpty=false</c> (→ xsl:if envolvendo a emissão).
///
/// ⚠️ LIMITE HONESTO: sem o catálogo de layout (GUID→XPath), NÃO temos o caminho
/// completo do input nem o pai do destino. Portanto:
///   • o destino é resolvido só até a FOLHA (sufixo do Name);
///   • o <c>select</c> do input é SIMBÓLICO (token derivado do InputGuid). O XSLT
///     COMPILA (XPath válido), mas não casa com um input real até resolvermos os
///     GUIDs. É exatamente a lacuna que o catálogo preencherá depois.
/// </summary>
public sealed class LinkMappingTranspiler
{
    public LinkTranspileResult Transpile(MapperVo mapper)
    {
        var leaves = new List<XElement>();
        var skipped = 0;

        foreach (var link in mapper.LinkMappings.OrderBy(m => m.Sequence))
        {
            var leafName = SafeName(link.TargetLeafName);
            if (leafName is null)
            {
                skipped++;
                continue;
            }

            var leaf = new XElement(leafName);
            // Rastro do GUID de destino (útil para auditoria e resolução futura).
            leaf.Add(new XComment($" target={link.TargetGuid} type={link.TargetType} input={link.InputGuid} "));

            var src = SymbolicInputSelect(link.InputGuid);
            if (IsWhitespaceStrip(link.RemoveWhiteSpaceType))
                src = $"normalize-space({src})";

            XElement emission;
            if (!string.IsNullOrEmpty(link.DefaultValue))
            {
                // Fallback: origem vazia → DefaultValue.
                emission = new XElement(Xslt.Ns + "choose",
                    new XElement(Xslt.Ns + "when",
                        new XAttribute("test", $"string({SymbolicInputSelect(link.InputGuid)})=''"),
                        new XText(link.DefaultValue)),
                    new XElement(Xslt.Ns + "otherwise",
                        new XElement(Xslt.Ns + "value-of", new XAttribute("select", src))));
            }
            else
            {
                emission = new XElement(Xslt.Ns + "value-of", new XAttribute("select", src));
            }

            // AllowEmpty=false: só emite conteúdo quando a origem tem valor.
            if (!link.AllowEmpty && string.IsNullOrEmpty(link.DefaultValue))
            {
                leaf.Add(new XElement(Xslt.Ns + "if",
                    new XAttribute("test", $"string({SymbolicInputSelect(link.InputGuid)})!=''"),
                    emission));
            }
            else
            {
                leaf.Add(emission);
            }

            leaves.Add(leaf);
        }

        return new LinkTranspileResult(leaves, leaves.Count, skipped);
    }

    /// <summary>
    /// select SIMBÓLICO do input: token NCName derivado do GUID (hífens→'_').
    /// XPath válido (passo de nome de elemento), mas não resolvido ao layout real.
    /// </summary>
    private static string SymbolicInputSelect(string? inputGuid) =>
        string.IsNullOrWhiteSpace(inputGuid) ? "''" : SafeName(inputGuid) ?? "''";

    private static bool IsWhitespaceStrip(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return false;
        return type.Trim().ToLowerInvariant() switch
        {
            "nothing" or "none" or "" => false,
            _ => true // "All", "Trim", "Left", "Right"…
        };
    }

    /// <summary>Sanitiza para um NCName válido (nome de elemento/XPath).</summary>
    private static string? SafeName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = Regex.Replace(raw.Trim(), "[^A-Za-z0-9_]", "_");
        if (s.Length == 0) return null;
        if (!char.IsLetter(s[0]) && s[0] != '_') s = "_" + s;
        return s;
    }
}
