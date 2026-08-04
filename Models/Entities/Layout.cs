using System.Xml.Serialization;

namespace LayoutParserApi.Models.Entities
{
    [XmlRoot("LayoutVO")]
    public class Layout
    {
        public string LayoutGuid { get; set; }
        public string LayoutType { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public int LimitOfCaracters { get; set; }

        [XmlArray("Elements")]
        [XmlArrayItem("Element")]
        public List<LineElement> Elements { get; set; } = new();

        public int Delimiter { get; set; }
        public string Escape { get; set; }
        public string InitializerLine { get; set; }
        public string FinisherLine { get; set; }

        /// <summary>
        /// Discriminador CANÔNICO de formato físico posicional (ver ADR-001):
        /// <c>true</c> = IDOC (um registro por linha), <c>false</c> = MQSeries (stream contínuo).
        ///
        /// <para><b>Tri-estado de propósito.</b> <c>null</c> significa "o XML do layout não trouxe
        /// o elemento" — layout legado, que cai no fallback deprecado de
        /// <see cref="Configuration.PositionalFormatResolver"/> com Warning. Não colapse em
        /// <c>bool</c>: "ausente" e "<c>false</c> explícito" precisam ser distinguíveis, senão a
        /// migração dos legados fica sem instrumento de medição.</para>
        /// </summary>
        public bool? WithBreakLines { get; set; }
    }
}