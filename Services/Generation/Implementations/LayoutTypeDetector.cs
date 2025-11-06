using LayoutParserApi.Services.Generation.Interfaces;
using System.Xml.Linq;

namespace LayoutParserApi.Services.Generation.Implementations
{
    /// <summary>
    /// Detecta o tipo de layout automaticamente
    /// </summary>
    public class LayoutTypeDetector : ILayoutTypeDetector
    {
        private readonly ILogger<LayoutTypeDetector> _logger;

        public LayoutTypeDetector(ILogger<LayoutTypeDetector> logger)
        {
            _logger = logger;
        }

        public string DetectLayoutType(string layoutXml)
        {
            if (string.IsNullOrWhiteSpace(layoutXml))
                return "Unknown";

            try
            {
                var doc = XDocument.Parse(layoutXml);
                var layoutType = doc.Root?.Element("LayoutType")?.Value ?? "";

                if (!string.IsNullOrEmpty(layoutType))
                {
                    _logger.LogDebug("Tipo de layout detectado do XML: {LayoutType}", layoutType);
                    return layoutType;
                }

                // Tentar detectar pelo nome do layout
                var layoutName = doc.Root?.Element("Name")?.Value ?? "";
                return DetectLayoutTypeByName(layoutName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao detectar tipo de layout do XML");
                return "Unknown";
            }
        }

        public string DetectLayoutTypeByName(string layoutName)
        {
            if (string.IsNullOrWhiteSpace(layoutName))
                return "Unknown";

            var upperName = layoutName.ToUpperInvariant();

            // Detectar por padrões no nome
            if (upperName.Contains("XML") || upperName.Contains("NFe") || upperName.Contains("NFE"))
            {
                return "Xml";
            }

            if (upperName.Contains("IDOC") || upperName.Contains("SAP") || upperName.Contains("EDI_DC40"))
            {
                return "TextPositional"; // IDOC é TextPositional mas com estrutura específica
            }

            if (upperName.Contains("MQSERIES") || upperName.Contains("MQ_SERIES"))
            {
                return "TextPositional";
            }

            if (upperName.Contains("JSON"))
            {
                return "Json";
            }

            // Padrão: TextPositional
            return "TextPositional";
        }
    }
}

