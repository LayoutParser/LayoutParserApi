using System.Xml;
using System.Xml.Schema;
using XslSynth.Model;

namespace XslSynth.Core.StructuralResolution;

/// <summary>
/// Item 1 da divisão de trabalho da issue #140 (design §2.1, §8). Lê o XSD SEFAZ de um tipo de
/// documento fiscal (NF-e por ora — decisão do dono, 2026-08-27: "a fonte de verdade da estrutura
/// XML de destino é o XSD da SEFAZ, por tipo de documento") e produz uma árvore navegável de
/// <see cref="XmlLayoutNode"/>, com XPath absoluto derivável para qualquer elemento/atributo.
///
/// Reaproveita <see cref="XmlSchemaSet"/> do BCL para resolver <c>xs:import</c>/<c>xs:include</c>
/// e compilar os particles (sequence/choice/all) — não reimplementa parsing de XSD à mão.
/// Zero I/O de rede: os arquivos XSD já precisam estar em disco (mirror
/// <c>nfephp-org/sped-nfe</c>, ver memória de <c>@lp-parser-llm</c> "public-fiscal-data-mirrors").
///
/// Extensível por tipo de documento: o chamador decide qual arquivo XSD raiz e qual elemento
/// global carregar (ex.: NF-e hoje; CT-e/outros no futuro só precisam de outro par arquivo+raiz —
/// nenhuma mudança nesta classe).
/// </summary>
public sealed class XmlLayoutStructureParser
{
    /// <summary>
    /// Carrega o XSD a partir de <paramref name="rootSchemaPath"/> (que pode <c>xs:include</c>/
    /// <c>xs:import</c> outros arquivos no mesmo diretório — resolução relativa padrão do BCL) e
    /// constrói a árvore a partir do elemento global <paramref name="rootElementName"/>.
    /// </summary>
    /// <param name="rootSchemaPath">Caminho do arquivo XSD raiz (ex.: <c>nfe_v4.00.xsd</c>).</param>
    /// <param name="rootElementName">Nome do elemento global raiz (ex.: <c>"NFe"</c>).</param>
    public XmlLayoutNode Parse(string rootSchemaPath, string rootElementName)
    {
        var schemaSet = new XmlSchemaSet
        {
            // XmlResolver padrão resolve xs:import/xs:include relativos ao diretório do arquivo
            // raiz (BCL) — necessário porque o XSD NF-e é fragmentado em vários arquivos
            // (leiauteNFe/tiposBasico/xmldsig-core-schema, mirror nfephp-org/sped-nfe).
            XmlResolver = new XmlUrlResolver()
        };
        using (var reader = XmlReader.Create(rootSchemaPath))
        {
            var schema = XmlSchema.Read(reader, ValidationCallback)
                ?? throw new InvalidOperationException($"Falha ao ler XSD '{rootSchemaPath}' (documento inválido).");
            schemaSet.Add(schema);
        }
        schemaSet.Compile();

        var rootElement = schemaSet.GlobalElements.Values
            .OfType<XmlSchemaElement>()
            .FirstOrDefault(e => e.Name == rootElementName)
            ?? throw new InvalidOperationException(
                $"Elemento global '{rootElementName}' não encontrado no XSD '{rootSchemaPath}'.");

        var visited = new HashSet<XmlSchemaComplexType>();
        return BuildNode(rootElement, parentPath: null, visited);
    }

    private static void ValidationCallback(object? sender, ValidationEventArgs e)
    {
        // XSDs reais da SEFAZ às vezes emitem warnings de redefinição/import redundante —
        // não é motivo para falhar o parse (mesmo princípio de resiliência do dotnet-standards.md:
        // dependência externa que "quase funciona" degrada, não derruba).
        if (e.Severity == XmlSeverityType.Error)
        {
            throw new XmlSchemaException(e.Message, e.Exception);
        }
    }

    private static XmlLayoutNode BuildNode(XmlSchemaElement element, string? parentPath, HashSet<XmlSchemaComplexType> visited)
    {
        var name = element.Name ?? element.RefName.Name;
        var nodePath = parentPath is null ? name : $"{parentPath}/{name}";
        var ns = (element.QualifiedName.Namespace is { Length: > 0 } n) ? n : null;

        var node = new XmlLayoutNode
        {
            NodePath = nodePath,
            Kind = XmlNodeKind.Element,
            Name = name,
            Namespace = ns,
            ParentPath = parentPath,
            MinOccurs = (int)element.MinOccurs,
            MaxOccurs = element.MaxOccursString == "unbounded" ? null : (int)element.MaxOccurs
        };

        if (element.ElementSchemaType is XmlSchemaComplexType complexType)
        {
            // Guarda contra recursão infinita em tipos auto-referenciados (não há caso conhecido
            // na NF-e, mas o XSD não impede estruturalmente) — se já visitado neste ramo, para
            // sem filhos em vez de estourar stack.
            if (!visited.Add(complexType))
            {
                return node;
            }

            foreach (var attribute in complexType.AttributeUses.Values.OfType<XmlSchemaAttribute>())
            {
                node.Children.Add(BuildAttributeNode(attribute, nodePath));
            }

            if (complexType.ContentTypeParticle is XmlSchemaParticle particle)
            {
                CollectChildElements(particle, nodePath, visited, node.Children);
            }

            visited.Remove(complexType);
        }

        return node;
    }

    private static XmlLayoutNode BuildAttributeNode(XmlSchemaAttribute attribute, string parentPath)
    {
        var name = attribute.Name ?? attribute.RefName.Name;
        var ns = (attribute.QualifiedName.Namespace is { Length: > 0 } n) ? n : null;
        return new XmlLayoutNode
        {
            NodePath = $"{parentPath}/@{name}",
            Kind = XmlNodeKind.Attribute,
            Name = name,
            Namespace = ns,
            ParentPath = parentPath,
            MinOccurs = attribute.Use == XmlSchemaUse.Required ? 1 : 0,
            MaxOccurs = 1
        };
    }

    /// <summary>Percorre <c>xs:sequence</c>/<c>xs:choice</c>/<c>xs:all</c> recursivamente — são
    /// wrappers puros no XPath (mesma convenção já usada em <c>GuidXPathCatalog</c> para o
    /// LayoutVO Sysmiddle), só repassam MinOccurs/MaxOccurs do grupo aos filhos quando o grupo em
    /// si repete (ex.: <c>xs:choice maxOccurs="unbounded"</c>).</summary>
    private static void CollectChildElements(
        XmlSchemaParticle particle, string parentPath, HashSet<XmlSchemaComplexType> visited, List<XmlLayoutNode> into)
    {
        switch (particle)
        {
            case XmlSchemaElement element:
                into.Add(BuildNode(element, parentPath, visited));
                break;

            case XmlSchemaGroupBase group:
                foreach (var item in group.Items.OfType<XmlSchemaParticle>())
                {
                    CollectChildElements(item, parentPath, visited, into);
                }
                break;

            case XmlSchemaAny:
                // xs:any (wildcard) — sem nome estrutural, não gera nó navegável.
                break;
        }
    }
}
