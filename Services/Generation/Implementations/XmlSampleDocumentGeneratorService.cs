using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

using LayoutParserApi.Models.Generation;
using LayoutParserApi.Services.Generation.Interfaces;

namespace LayoutParserApi.Services.Generation.Implementations
{
    /// <summary>
    /// Implementação de <see cref="IXmlSampleDocumentGeneratorService"/> (issue #356).
    ///
    /// Spike (critério de aceite 1): não há, no runtime C#, nenhum parser que reconstrua a árvore
    /// do <c>LayoutVO</c> tipo <c>Xml</c> para reserializá-la. <c>XmlLayoutLoader</c> e
    /// <c>TxtGenerator.Parsers.XmlLayoutParser</c> só entendem <c>LineElementVO</c>/<c>FieldElementVO</c>
    /// (TextPositional). O mais próximo é <c>XslSynth.Core.GuidXPathCatalog</c> (outro assembly,
    /// <c>ai/XslSynth.Contracts</c>), que anda a mesma árvore mas achata para um índice GUID→XPath,
    /// sem reter <c>Sequence</c>/ocorrência nem serializar documento. Daí este serviço NOVO —
    /// reaproveitando dele apenas as convenções de caminhada (atributo vira <c>@Name</c> no pai;
    /// <c>Choice</c>/<c>Sequence</c> são wrappers estruturais que não viram elemento).
    /// </summary>
    public class XmlSampleDocumentGeneratorService : IXmlSampleDocumentGeneratorService
    {
        private readonly ILogger<XmlSampleDocumentGeneratorService> _logger;
        private readonly ITypedValueGenerator _valueGenerator;

        private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

        // Wrappers de ocorrência do LayoutVO: não viram segmento de árvore, só repassam o pai.
        private static readonly HashSet<string> WrapperTypes =
            new(StringComparer.Ordinal) { "ChoiceElementVO", "SequenceElementVO" };

        public XmlSampleDocumentGeneratorService(
            ILogger<XmlSampleDocumentGeneratorService> logger,
            ITypedValueGenerator valueGenerator)
        {
            _logger = logger;
            _valueGenerator = valueGenerator;
        }

        public XmlSampleResult GenerateSample(string layoutXmlContent)
        {
            var result = new XmlSampleResult();

            if (string.IsNullOrWhiteSpace(layoutXmlContent))
            {
                result.Success = false;
                result.ErrorMessage = "Conteúdo do layout vazio.";
                return result;
            }

            XElement root;
            try
            {
                // ⚠️ Mesma pegadinha do MapperVO/GuidXPathCatalog: o LayoutVO exportado declara
                // encoding="utf-16" no prólogo mas os bytes estão em utf-8. Corrige a declaração
                // (e tira BOM) antes de parsear como string.
                var text = layoutXmlContent.TrimStart('﻿').TrimStart();
                text = Regex.Replace(text, "encoding=(\"|')utf-16(\"|')", "encoding=\"utf-8\"",
                    RegexOptions.IgnoreCase);

                root = XDocument.Parse(text).Root
                    ?? throw new InvalidOperationException("XML do layout sem elemento raiz.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao parsear o XML do layout para geração de exemplo");
                result.Success = false;
                result.ErrorMessage = $"XML do layout ilegível: {ex.Message}";
                return result;
            }

            try
            {
                var elementsRoot = root.Element("Elements");
                var topElements = elementsRoot?
                    .Elements("Element")
                    .Where(e => XsiType(e) != "AttributeElementVO")
                    .OrderBy(SequenceOf)
                    .ToList() ?? new List<XElement>();

                if (topElements.Count == 0)
                {
                    result.Success = false;
                    result.ErrorMessage = "Layout Xml sem elementos de árvore (<Elements>) para gerar exemplo.";
                    return result;
                }

                var counter = new Counter();
                var container = new XElement("_root_");
                foreach (var top in topElements)
                    BuildInto(container, top, counter, result);

                var builtRoots = container.Elements().ToList();
                XElement docRoot;
                if (builtRoots.Count == 1)
                {
                    docRoot = builtRoots[0];
                }
                else
                {
                    // Múltiplos elementos raiz (incomum) — encapsula para manter XML bem-formado.
                    docRoot = new XElement("Documento", builtRoots);
                    result.Warnings.Add(
                        $"Layout tem {builtRoots.Count} elementos de raiz; encapsulados em <Documento> para produzir XML bem-formado.");
                }

                var declaration = "<?xml version=\"1.0\" encoding=\"utf-8\"?>";
                result.Xml = declaration + Environment.NewLine + docRoot.ToString(SaveOptions.None);
                result.ElementCount = counter.N;
                result.Success = true;
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao gerar documento XML de exemplo a partir do layout");
                result.Success = false;
                result.ErrorMessage = $"Falha ao montar o documento de exemplo: {ex.Message}";
                return result;
            }
        }

        /// <summary>
        /// Constrói o(s) elemento(s) correspondente(s) a <paramref name="layoutEl"/> sob
        /// <paramref name="parent"/>, respeitando ocorrência mínima (mínimo viável: 1, ou
        /// <c>MinimalOccurrence</c> quando &gt; 1, sempre limitado por <c>MaximumOccurrence</c>).
        /// </summary>
        private void BuildInto(XElement parent, XElement layoutEl, Counter counter, XmlSampleResult result)
        {
            var type = XsiType(layoutEl);

            // Wrapper estrutural (Choice/Sequence): não vira elemento — repassa os filhos ao pai.
            if (WrapperTypes.Contains(type))
            {
                foreach (var child in OrderedChildren(layoutEl).Where(c => XsiType(c) != "AttributeElementVO"))
                    BuildInto(parent, child, counter, result);
                return;
            }

            var name = SanitizeName(TextOf(layoutEl, "Name", "Campo"));
            var min = IntOf(layoutEl, "MinimalOccurrence", 0);
            var max = IntOf(layoutEl, "MaximumOccurrence", 0);

            var occurrences = min >= 1 ? min : 1;
            if (max > 0)
                occurrences = Math.Min(occurrences, max);

            if (min > 1 && max != min)
                result.Warnings.Add(
                    $"Elemento '{name}': gerado {occurrences}x (MinimalOccurrence={min}, MaximumOccurrence={(max > 0 ? max.ToString() : "ilimitado")}).");

            for (var i = 0; i < occurrences; i++)
            {
                var node = new XElement(name);
                parent.Add(node);
                counter.N++;

                var children = OrderedChildren(layoutEl).ToList();
                if (children.Count == 0)
                {
                    // Folha (TagElementVO com <Elements/> vazio): valor sintético por heurística de nome.
                    node.Value = _valueGenerator.Generate(_valueGenerator.InferType(name));
                    continue;
                }

                // Atributos primeiro — viram XML attribute do nó atual, não elemento filho.
                foreach (var attr in children.Where(c => XsiType(c) == "AttributeElementVO"))
                {
                    var attrName = SanitizeName(TextOf(attr, "Name", "attr"));
                    node.SetAttributeValue(attrName, _valueGenerator.Generate(_valueGenerator.InferType(attrName)));
                }

                var childElements = children.Where(c => XsiType(c) != "AttributeElementVO").ToList();
                foreach (var child in childElements)
                    BuildInto(node, child, counter, result);

                // TagElementVO que só tinha atributos (sem sub-tags): ainda precisa de um valor textual.
                if (!node.HasElements && string.IsNullOrEmpty(node.Value) && childElements.Count == 0)
                    node.Value = _valueGenerator.Generate(_valueGenerator.InferType(name));
            }
        }

        private static IEnumerable<XElement> OrderedChildren(XElement layoutEl)
        {
            var container = layoutEl.Element("Elements");
            if (container == null)
                return Enumerable.Empty<XElement>();

            return container.Elements("Element").OrderBy(SequenceOf);
        }

        private static string? XsiType(XElement el) =>
            ((string?)el.Attribute(Xsi + "type"))?.Trim();

        private static int SequenceOf(XElement el) =>
            int.TryParse(((string?)el.Element("Sequence"))?.Trim(), out var v) ? v : int.MaxValue;

        private static int IntOf(XElement el, string tag, int fallback) =>
            int.TryParse(((string?)el.Element(tag))?.Trim(), out var v) ? v : fallback;

        private static string TextOf(XElement el, string tag, string fallback)
        {
            var raw = ((string?)el.Element(tag))?.Trim();
            return string.IsNullOrEmpty(raw) ? fallback : raw;
        }

        /// <summary>
        /// Garante um nome de elemento/atributo XML válido. Nomes do LayoutVO real ("Rps", "InfRps")
        /// já são limpos; isto é rede de segurança para nomes com espaço/caracteres inválidos.
        /// </summary>
        private static string SanitizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Campo";

            try
            {
                return XmlConvert.EncodeLocalName(name.Trim());
            }
            catch
            {
                return "Campo";
            }
        }

        private sealed class Counter
        {
            public int N;
        }
    }
}
