using System.Xml.Linq;

using LayoutParserApi.Services.Generation.Implementations;

using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Services.Generation
{
    /// <summary>
    /// Issue #356: parser de árvore do <c>LayoutVO</c> tipo <c>Xml</c> para
    /// <see cref="XmlSampleDocumentGeneratorService"/>. Cobre ordenação por <c>Sequence</c>,
    /// <c>AttributeElementVO</c> como atributo (não filho), ocorrência mínima, o mismatch de
    /// declaração utf-16/utf-8 e o degrade gracioso em XML malformado.
    /// </summary>
    public class XmlSampleDocumentGeneratorServiceTests
    {
        private static XmlSampleDocumentGeneratorService Build() =>
            new(NullLogger<XmlSampleDocumentGeneratorService>.Instance,
                new TypedValueGenerator(NullLogger<TypedValueGenerator>.Instance));

        private const string LayoutBasico = @"<?xml version=""1.0"" encoding=""utf-16""?>
<LayoutVO xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xsi:type=""XmlLayoutVO"">
  <LayoutGuid>LAY_x</LayoutGuid>
  <LayoutType>Xml</LayoutType>
  <Name>LAY_X</Name>
  <Elements>
    <Element xsi:type=""GroupTagElementVO"">
      <ElementGuid>GRT_1</ElementGuid>
      <Name>Rps</Name>
      <Sequence>1</Sequence>
      <Elements>
        <Element xsi:type=""AttributeElementVO"">
          <ElementGuid>ATT_1</ElementGuid>
          <Name>Id</Name>
          <Sequence>1</Sequence>
        </Element>
        <Element xsi:type=""TagElementVO"">
          <ElementGuid>TAG_B</ElementGuid>
          <Name>DataEmissao</Name>
          <Sequence>3</Sequence>
          <Elements/>
        </Element>
        <Element xsi:type=""TagElementVO"">
          <ElementGuid>TAG_A</ElementGuid>
          <Name>Numero</Name>
          <Sequence>2</Sequence>
          <Elements/>
        </Element>
      </Elements>
    </Element>
  </Elements>
</LayoutVO>";

        [Fact]
        public void GenerateSample_produz_xml_bem_formado_com_raiz_do_layout()
        {
            var result = Build().GenerateSample(LayoutBasico);

            Assert.True(result.Success, result.ErrorMessage);
            var doc = XDocument.Parse(result.Xml!);
            Assert.Equal("Rps", doc.Root!.Name.LocalName);
            Assert.True(result.ElementCount >= 3);
        }

        [Fact]
        public void GenerateSample_trata_AttributeElementVO_como_atributo_nao_filho()
        {
            var result = Build().GenerateSample(LayoutBasico);

            var doc = XDocument.Parse(result.Xml!);
            Assert.NotNull(doc.Root!.Attribute("Id"));
            Assert.DoesNotContain(doc.Root.Elements(), e => e.Name.LocalName == "Id");
        }

        [Fact]
        public void GenerateSample_respeita_ordem_por_Sequence()
        {
            var result = Build().GenerateSample(LayoutBasico);

            var doc = XDocument.Parse(result.Xml!);
            var nomes = doc.Root!.Elements().Select(e => e.Name.LocalName).ToList();
            Assert.Equal(new[] { "Numero", "DataEmissao" }, nomes);
        }

        [Fact]
        public void GenerateSample_honra_MinimalOccurrence_maior_que_um()
        {
            const string layout = @"<LayoutVO xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"">
  <LayoutType>Xml</LayoutType>
  <Elements>
    <Element xsi:type=""GroupTagElementVO"">
      <Name>Lista</Name><Sequence>1</Sequence>
      <Elements>
        <Element xsi:type=""TagElementVO"">
          <Name>Item</Name><Sequence>1</Sequence>
          <MinimalOccurrence>3</MinimalOccurrence>
          <MaximumOccurrence>0</MaximumOccurrence>
          <Elements/>
        </Element>
      </Elements>
    </Element>
  </Elements>
</LayoutVO>";

            var result = Build().GenerateSample(layout);

            Assert.True(result.Success, result.ErrorMessage);
            var doc = XDocument.Parse(result.Xml!);
            Assert.Equal(3, doc.Root!.Elements("Item").Count());
        }

        [Fact]
        public void GenerateSample_limita_ocorrencia_por_MaximumOccurrence()
        {
            const string layout = @"<LayoutVO xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"">
  <LayoutType>Xml</LayoutType>
  <Elements>
    <Element xsi:type=""GroupTagElementVO"">
      <Name>Lista</Name><Sequence>1</Sequence>
      <Elements>
        <Element xsi:type=""TagElementVO"">
          <Name>Item</Name><Sequence>1</Sequence>
          <MinimalOccurrence>5</MinimalOccurrence>
          <MaximumOccurrence>2</MaximumOccurrence>
          <Elements/>
        </Element>
      </Elements>
    </Element>
  </Elements>
</LayoutVO>";

            var result = Build().GenerateSample(layout);

            var doc = XDocument.Parse(result.Xml!);
            Assert.Equal(2, doc.Root!.Elements("Item").Count());
        }

        [Fact]
        public void GenerateSample_aceita_declaracao_utf16_com_bytes_utf8()
        {
            // LayoutBasico já declara encoding="utf-16"; o parse tem que funcionar mesmo assim.
            var result = Build().GenerateSample(LayoutBasico);
            Assert.True(result.Success, result.ErrorMessage);
        }

        [Fact]
        public void GenerateSample_degrade_gracioso_em_xml_malformado()
        {
            var result = Build().GenerateSample("<LayoutVO><Elements><Element></LayoutVO");

            Assert.False(result.Success);
            Assert.NotNull(result.ErrorMessage);
            Assert.Null(result.Xml);
        }

        [Fact]
        public void GenerateSample_falha_quando_layout_sem_arvore()
        {
            var result = Build().GenerateSample(
                @"<LayoutVO><LayoutType>Xml</LayoutType><Elements></Elements></LayoutVO>");

            Assert.False(result.Success);
        }

        [Fact]
        public void GenerateSample_preenche_folha_com_valor_nao_vazio()
        {
            var result = Build().GenerateSample(LayoutBasico);

            var doc = XDocument.Parse(result.Xml!);
            var numero = doc.Root!.Element("Numero");
            Assert.NotNull(numero);
            Assert.False(string.IsNullOrWhiteSpace(numero!.Value));
        }
    }
}
