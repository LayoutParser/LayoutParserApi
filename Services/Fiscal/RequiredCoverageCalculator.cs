using System.Xml;
using System.Xml.Schema;

namespace LayoutParserApi.Services.Fiscal
{
    /// <summary>Resultado de <see cref="IRequiredCoverageCalculator.Calculate"/> — issue #380 (#198.5).</summary>
    public sealed record RequiredCoverageResult(double Percent, IReadOnlyList<string> Uncovered);

    /// <summary>
    /// Cobertura ESTÁTICA de destinos obrigatórios do XSD alvo (issue #380, cross-check #198.5) —
    /// diferente de <c>MappingTestRunSummary.CoveragePercent</c>, que é cobertura de TESTE dinâmica
    /// (regras accepted/edited sobre o total de regras do draft). Aqui: enumera os elementos/atributos
    /// <c>minOccurs&gt;=1</c>/<c>use=required</c> do XSD alvo via <see cref="XmlSchemaSet"/> e cruza
    /// com os <c>TargetRefs</c> das regras aceitas — o mesmo formato de XPath por nome local usado em
    /// <c>MappingDraftRule.TargetRefs</c> (ex.: <c>/NFe/infNFe/ide/cUF</c>). Só entram no conjunto
    /// "obrigatório" elementos FOLHA (conteúdo simples/vazio) e atributos — um container estrutural
    /// (ex.: <c>&lt;infNFe&gt;</c>, <c>&lt;ide&gt;</c>) é sempre emitido pela estrutura do XSLT, então
    /// nunca precisa de um <c>TargetRef</c> apontando pra ele mesmo.
    /// </summary>
    public interface IRequiredCoverageCalculator
    {
        /// <summary>
        /// <c>null</c> se o elemento raiz não existir no <paramref name="schemaSet"/> compilado
        /// (schema incompatível com <paramref name="rootElementName"/>/<paramref name="targetNamespace"/>).
        /// </summary>
        RequiredCoverageResult? Calculate(
            XmlSchemaSet schemaSet,
            string rootElementName,
            string targetNamespace,
            IReadOnlyCollection<string> targetRefs);
    }

    public sealed class RequiredCoverageCalculator : IRequiredCoverageCalculator
    {
        public RequiredCoverageResult? Calculate(
            XmlSchemaSet schemaSet,
            string rootElementName,
            string targetNamespace,
            IReadOnlyCollection<string> targetRefs)
        {
            var qualifiedName = new XmlQualifiedName(rootElementName, targetNamespace);
            if (schemaSet.GlobalElements[qualifiedName] is not XmlSchemaElement rootElement)
                return null;

            var required = new HashSet<string>(StringComparer.Ordinal);
            // (TypeName, Path): evita loop infinito em schemas com recursão de tipo (ex.: grupos
            // de repetição que reapontam para o mesmo complexType em profundidade) e evita reexpandir
            // o mesmo caminho mais de uma vez.
            var visited = new HashSet<(string TypeName, string Path)>();

            if (rootElement.ElementSchemaType is XmlSchemaComplexType rootType)
                CollectRequired(schemaSet, rootType, "/" + rootElementName, required, visited);

            if (required.Count == 0)
                return new RequiredCoverageResult(100, Array.Empty<string>());

            var covered = new HashSet<string>(targetRefs.Select(NormalizePath), StringComparer.Ordinal);
            var uncovered = required.Where(r => !covered.Contains(r)).OrderBy(r => r, StringComparer.Ordinal).ToList();
            var percent = (double)(required.Count - uncovered.Count) / required.Count * 100;

            return new RequiredCoverageResult(percent, uncovered);
        }

        /// <summary>Remove índices posicionais (<c>[n]</c>) — o XSD é um contrato de tipo, não de instância.</summary>
        private static string NormalizePath(string path)
        {
            var segments = path.Split('/');
            var normalized = segments.Select(s =>
            {
                var bracketIndex = s.IndexOf('[');
                return bracketIndex >= 0 ? s[..bracketIndex] : s;
            });
            return string.Join('/', normalized);
        }

        private static void CollectRequired(
            XmlSchemaSet schemaSet, XmlSchemaComplexType complexType, string parentPath,
            HashSet<string> required, HashSet<(string, string)> visited)
        {
            foreach (XmlSchemaAttribute attribute in complexType.AttributeUses.Values)
                if (attribute.Use == XmlSchemaUse.Required)
                    required.Add($"{parentPath}/@{attribute.QualifiedName.Name}");

            if (complexType.ContentTypeParticle is XmlSchemaParticle particle)
                CollectParticle(schemaSet, particle, parentPath, required, visited, forceRequired: true);
        }

        private static void CollectParticle(
            XmlSchemaSet schemaSet, XmlSchemaParticle particle, string parentPath,
            HashSet<string> required, HashSet<(string, string)> visited, bool forceRequired)
        {
            switch (particle)
            {
                case XmlSchemaSequence sequence:
                    foreach (XmlSchemaParticle item in sequence.Items)
                        CollectParticle(schemaSet, item, parentPath, required, visited, forceRequired && item.MinOccurs >= 1);
                    break;

                case XmlSchemaAll all:
                    foreach (XmlSchemaParticle item in all.Items)
                        CollectParticle(schemaSet, item, parentPath, required, visited, forceRequired && item.MinOccurs >= 1);
                    break;

                case XmlSchemaChoice:
                    // <xs:choice>: nenhum ramo é individualmente obrigatório (um OU outro satisfaz o
                    // schema) — conservador: não marca filhos do choice como obrigatórios. Diferente
                    // de sequence/all, onde cada item required precisa estar presente.
                    break;

                case XmlSchemaElement element:
                    var resolved = !element.RefName.IsEmpty && schemaSet.GlobalElements[element.RefName] is XmlSchemaElement refElement
                        ? refElement
                        : element;

                    var name = !resolved.QualifiedName.IsEmpty ? resolved.QualifiedName.Name : resolved.Name;
                    if (string.IsNullOrEmpty(name))
                        break;

                    var path = $"{parentPath}/{name}";
                    var childType = resolved.ElementSchemaType as XmlSchemaComplexType;

                    // Só marca o elemento em si como "destino obrigatório" se for FOLHA (conteúdo
                    // simples/vazio — o valor de fato precisa vir de uma regra). Um container
                    // estrutural (ex.: <infNFe>, <ide>) é sempre emitido pela estrutura do XSLT — não
                    // faz sentido exigir um TargetRef apontando pro container em si, só para o que tem
                    // dentro dele (folhas + atributos, coletados recursivamente abaixo).
                    var isLeaf = childType == null || childType.ContentTypeParticle is not XmlSchemaGroupBase;
                    if (forceRequired && element.MinOccurs >= 1 && isLeaf)
                        required.Add(path);

                    if (childType != null)
                    {
                        var key = (childType.QualifiedName.ToString(), path);
                        if (visited.Add(key))
                            CollectRequired(schemaSet, childType, path, required, visited);
                    }
                    break;
            }
        }
    }
}
