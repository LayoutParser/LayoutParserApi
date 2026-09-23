using LayoutParserApi.Models.Generation;

namespace LayoutParserApi.Services.Generation.Interfaces
{
    /// <summary>
    /// Gera um documento XML de exemplo percorrendo a árvore de um <c>LayoutVO</c> tipo <c>Xml</c>
    /// (<c>GroupTagElementVO</c>/<c>TagElementVO</c>/<c>AttributeElementVO</c>), respeitando
    /// <c>Sequence</c>/<c>MinimalOccurrence</c>/<c>MaximumOccurrence</c>. Issue #356 — estende o
    /// endpoint <c>POST /api/layouts/{guid}/generate-sample</c>, sem contrato paralelo.
    /// </summary>
    public interface IXmlSampleDocumentGeneratorService
    {
        /// <summary>
        /// Percorre o XML do layout e devolve um documento de exemplo sintético.
        /// </summary>
        /// <param name="layoutXmlContent">Conteúdo bruto do <c>LayoutVO</c> (o mesmo texto decifrado do catálogo).</param>
        XmlSampleResult GenerateSample(string layoutXmlContent);
    }
}
