using System.Xml.Linq;

namespace LayoutParserApi.Services.XmlAnalysis.Models
{
    /// <summary>
    /// Mapeamento de segmento MQSeries para elemento XML
    /// </summary>
    public class SegmentMapping
    {
        public int MqSeriesLineNumber { get; set; }
        public string MqSeriesSegment { get; set; }
        public string XmlElementPath { get; set; }
        public XElement XmlElement { get; set; }
    }
}
