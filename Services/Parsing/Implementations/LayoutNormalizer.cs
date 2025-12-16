using LayoutParserApi.Models.Entities;
using LayoutParserApi.Services.Parsing.Interfaces;

using Newtonsoft.Json;

namespace LayoutParserApi.Services.Parsing.Implementations
{
    public class LayoutNormalizer : ILayoutNormalizer
    {
        public Layout ReestruturarLayout(Layout layoutOriginal)
        {
            if (layoutOriginal?.Elements == null)
                return layoutOriginal;

            var novoLayout = new Layout
            {
                LayoutGuid = layoutOriginal.LayoutGuid,
                LayoutType = layoutOriginal.LayoutType,
                Name = layoutOriginal.Name,
                Description = layoutOriginal.Description,
                LimitOfCaracters = layoutOriginal.LimitOfCaracters,
                Elements = new List<LineElement>()
            };

            foreach (var elemento in layoutOriginal.Elements)
            {
                if (elemento.Name == "LINHA020")
                {
                    var linha020Limpa = CriarCopiaLineElement(elemento);
                    linha020Limpa.Elements = new List<string>();
                    linha020Limpa.DeserializedElements = new List<object>();

                    var lineElementsParaPromover = new List<LineElement>();

                    if (elemento.Elements != null)
                    {
                        foreach (var elementoJson in elemento.Elements)
                        {
                            if (!EhLineElementFilho(elementoJson))
                                linha020Limpa.Elements.Add(elementoJson);
                            else
                            {
                                var linhaFilha = JsonConvert.DeserializeObject<LineElement>(elementoJson);
                                if (linhaFilha != null)
                                    lineElementsParaPromover.Add(linhaFilha);
                            }
                        }
                    }

                    novoLayout.Elements.Add(linha020Limpa);
                    novoLayout.Elements.AddRange(lineElementsParaPromover);
                }
                else
                    novoLayout.Elements.Add(elemento);
            }

            return novoLayout;
        }

        public Layout ReordenarSequences(Layout layout)
        {
            if (layout?.Elements == null)
                return layout;

            var elementosOrdenados = layout.Elements.OrderBy(e => ObterNumeroDaLinha(e.Name)).ToList();

            for (int i = 0; i < elementosOrdenados.Count; i++)
                elementosOrdenados[i].Sequence = i + 1;

            layout.Elements = elementosOrdenados;
            return layout;
        }

        private int ObterNumeroDaLinha(string nomeLinha)
        {
            if (string.IsNullOrEmpty(nomeLinha) || !nomeLinha.StartsWith("LINHA"))
                return 9999;

            var numeroStr = nomeLinha.Substring(5);
            if (int.TryParse(numeroStr, out int numero))
                return numero;

            return 9999;
        }

        private LineElement CriarCopiaLineElement(LineElement original)
        {
            return new LineElement
            {
                ElementGuid = original.ElementGuid,
                Name = original.Name,
                Description = original.Description,
                Sequence = original.Sequence,
                IsRequired = original.IsRequired,
                MinimalOccurrence = original.MinimalOccurrence,
                MaximumOccurrence = original.MaximumOccurrence,
                InitialValue = original.InitialValue
            };
        }

        private bool EhLineElementFilho(string elementoJson)
        {
            try
            {
                var linha = JsonConvert.DeserializeObject<LineElement>(elementoJson);
                return EhLineElementFilho(linha);
            }
            catch
            {
                return false;
            }
        }

        private bool EhLineElementFilho(LineElement linha)
        {
            return linha != null && !string.IsNullOrEmpty(linha.Name) && linha.Name != "LINHA020" && linha.Name.StartsWith("LINHA");
        }
    }
}