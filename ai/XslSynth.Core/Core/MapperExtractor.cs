using System.Xml.Linq;
using XslSynth.Model;

namespace XslSynth.Core;

/// <summary>
/// Passo 1 do loop (código): MapperVO (XML) → LinkMappings + Rules + XslContent.
/// </summary>
public sealed class MapperExtractor
{
    public MapperVo ExtractFromFile(string path) => Extract(XDocument.Load(path));

    public MapperVo Extract(XDocument doc)
    {
        if (doc.Root is null)
            throw new InvalidOperationException("MapperVO vazio ou inválido.");

        // Aceita tanto <MapperVO> na raiz quanto aninhado.
        var root = doc.Root.Name.LocalName == "MapperVO"
            ? doc.Root
            : doc.Root.Element("MapperVO") ?? doc.Root;

        var mapper = new MapperVo
        {
            MapperGuid = root.Element("MapperGuid")?.Value,
            Name = root.Element("Name")?.Value,
            Description = root.Element("Description")?.Value,
            InputLayoutGuid = root.Element("InputLayoutGuid")?.Value,
            TargetLayoutGuid = root.Element("TargetLayoutGuid")?.Value,
            XslContent = root.Element("XslContent")?.Value ?? root.Element("Xsl")?.Value
        };

        var linkMappings = root.Element("LinkMappings");
        if (linkMappings is not null)
        {
            foreach (var el in linkMappings.Elements("LinkMappingItem"))
            {
                mapper.LinkMappings.Add(new LinkMappingItem
                {
                    Name = el.Element("Name")?.Value,
                    Sequence = ParseInt(el.Element("Sequence")?.Value),
                    // Ponto de extensão: em produção estes GUIDs são resolvidos para XPath
                    // via catálogo de layout (ResolvePath). No MVP já vêm como XPath.
                    SourcePath = ResolvePath(el.Element("InputLayoutGuid")?.Value),
                    TargetPath = ResolvePath(el.Element("TargetLayoutGuid")?.Value),
                    IsToTruncateValue = ParseBool(el.Element("IsToTruncateValue")?.Value),
                    RemoveWhiteSpaceType = el.Element("RemoveWhiteSpaceType")?.Value,
                    DefaultValue = el.Element("DefaultValue")?.Value,
                    AllowEmpty = ParseBool(el.Element("AllowEmpty")?.Value)
                });
            }
        }

        var rules = root.Element("Rules");
        if (rules is not null)
        {
            foreach (var el in rules.Elements("Rule"))
            {
                mapper.Rules.Add(new MapperRule
                {
                    Name = el.Element("Name")?.Value,
                    Sequence = ParseInt(el.Element("Sequence")?.Value),
                    Description = el.Element("Description")?.Value,
                    TargetPath = ResolvePath(el.Element("TargetElementGuid")?.Value),
                    ContentValue = el.Element("ContentValue")?.Value
                });
            }
        }

        return mapper;
    }

    /// <summary>
    /// Ponto de extensão: no MVP o "GUID" já é o XPath resolvido, então é passthrough.
    /// Em produção, aqui entraria a consulta ao catálogo de layout (GUID → XPath).
    /// </summary>
    private static string? ResolvePath(string? guidOrPath) => guidOrPath?.Trim();

    private static int ParseInt(string? value) => int.TryParse(value, out var n) ? n : 0;
    private static bool ParseBool(string? value) => bool.TryParse(value, out var b) && b;
}
