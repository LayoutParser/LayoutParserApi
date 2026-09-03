using System.Xml.Linq;
using System.Xml.XPath;

using LayoutParserApi.Models.Entities;
using LayoutParserApi.Models.Enums;

using XslSynth.Model;

namespace LayoutParserApi.Services.XmlAnalysis
{
    /// <summary>
    /// Reconstrução reversa best-effort XML→TXT (issue #151, Fase 4). Caminho INVERSO do parse
    /// direto: em vez de reinventar "onde fica cada campo", reaproveita o mesmo
    /// <see cref="FieldToXmlMapping"/>[] que <see cref="StructuralResolution.FieldMappingCompositionService"/>
    /// já compõe para o pathway de proveniência (Sources = <see cref="TxtFieldReference"/> com
    /// posição/tamanho no TXT; Targets = <see cref="XmlNodeReference"/> com o XPath no XML de
    /// destino) — não é um mapeamento novo, é o mesmo crosswalk usado ao contrário.
    /// </summary>
    /// <remarks>
    /// <b>Escopo MVP (decisão do desenho de arquitetura):</b> só TXT posicional FIXO
    /// (<c>Layout.WithBreakLines != false</c> descartado deliberadamente aqui — MQSeries/IDOC com
    /// linha variável ficam fora; largura de campo fixa é o único caso onde "onde escrever" nunca é
    /// ambíguo sem dado real para validar as nuances de linha variável).
    /// <para>
    /// <b>"Best-effort" é contratual, não cosmético</b> (issue #151, riscos §2): todo
    /// <see cref="FieldToXmlMapping"/> com mais de uma origem, ou cujo <see cref="MappingKind"/> não
    /// é <see cref="MappingKind.Direct"/>/<see cref="MappingKind.Static"/>, é tratado como
    /// computado/derivado — sem caminho reverso determinístico (ex.: concatenação de dois campos
    /// via XSLT) — e vira <see cref="ReconstructionWarningKind.NotDeterministicallyReversible"/>, não
    /// erro fatal.
    /// </para>
    /// </remarks>
    public class ReverseReconstructionService
    {
        private readonly ILogger<ReverseReconstructionService> _logger;

        public ReverseReconstructionService(ILogger<ReverseReconstructionService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Reconstrói o TXT posicional fixo a partir do XML de destino, usando o crosswalk
        /// <paramref name="mappings"/> já resolvido (mesmo crosswalk usado no parse direto/composer de
        /// proveniência) e as posições declaradas em <paramref name="sourceLayout"/>. Nunca lança:
        /// qualquer falha isolada de campo vira <see cref="ReconstructionWarningKind.ProcessingError"/>,
        /// não aborta a reconstrução dos demais campos (dotnet-standards.md — resiliência).
        /// </summary>
        public ReconstructionResult Reconstruct(
            Layout sourceLayout,
            IReadOnlyList<FieldToXmlMapping> mappings,
            XDocument targetXml)
        {
            var result = new ReconstructionResult();
            if (sourceLayout?.Elements == null || sourceLayout.Elements.Count == 0)
            {
                result.Warnings.Add(new ReconstructionWarning
                {
                    Kind = ReconstructionWarningKind.ProcessingError,
                    Message = "Layout de origem sem elementos — nada a reconstruir."
                });
                return result;
            }

            var navigator = targetXml?.CreateNavigator();
            // Chave: (LineName, Occurrence) -> buffer de caracteres já pré-preenchido com espaço até
            // LimitOfCaracters (linha física fixa, escopo MVP declarado na classe).
            var lineBuffers = new Dictionary<(string LineName, int Occurrence), char[]>();
            var lineOrder = new List<(string LineName, int Occurrence)>();
            var lineWidth = sourceLayout.LimitOfCaracters > 0 ? sourceLayout.LimitOfCaracters : 0;

            foreach (var mapping in mappings ?? Array.Empty<FieldToXmlMapping>())
            {
                try
                {
                    ReconstructOneMapping(mapping, navigator, lineWidth, lineBuffers, lineOrder, result);
                }
                catch (Exception ex)
                {
                    // Isolamento por item — mesmo princípio de FieldMappingCompositionService.Compose:
                    // um mapeamento malformado não pode derrubar a reconstrução inteira.
                    _logger.LogWarning(ex, "Falha ao reconstruir campo a partir do mapeamento {MappingId}", mapping?.MappingId);
                    result.Warnings.Add(new ReconstructionWarning
                    {
                        FieldName = mapping?.Sources?.FirstOrDefault()?.FieldName ?? string.Empty,
                        LineName = mapping?.Sources?.FirstOrDefault()?.LineName ?? string.Empty,
                        Kind = ReconstructionWarningKind.ProcessingError,
                        Message = $"Falha inesperada ao reconstruir o campo (mapeamento {mapping?.MappingId}) — ver log do servidor."
                    });
                }
            }

            result.ReconstructedText = string.Join(
                Environment.NewLine,
                lineOrder.Select(key => new string(lineBuffers[key])));

            return result;
        }

        private void ReconstructOneMapping(
            FieldToXmlMapping mapping,
            XPathNavigator? navigator,
            int lineWidth,
            Dictionary<(string, int), char[]> lineBuffers,
            List<(string, int)> lineOrder,
            ReconstructionResult result)
        {
            // Só 1:1 direto tem caminho reverso determinístico (issue #151, riscos §2) — campo
            // derivado/computado (concatenação, função XSLT) fica sem reconstrução, mas não é erro.
            if (mapping.Sources.Count != 1 || mapping.Targets.Count != 1
                || (mapping.Kind != MappingKind.Direct && mapping.Kind != MappingKind.Static))
            {
                result.FieldsAttempted++;
                var firstSource = mapping.Sources.FirstOrDefault();
                result.Warnings.Add(new ReconstructionWarning
                {
                    LineName = firstSource?.LineName ?? string.Empty,
                    FieldName = firstSource?.FieldName ?? string.Empty,
                    Occurrence = firstSource?.LineOccurrence ?? 0,
                    Kind = ReconstructionWarningKind.NotDeterministicallyReversible,
                    Message = $"Mapeamento '{mapping.MappingId}' ({mapping.Kind}) tem {mapping.Sources.Count} origem(ns)/{mapping.Targets.Count} destino(s) — sem caminho reverso determinístico."
                });
                return;
            }

            var source = mapping.Sources[0];
            var target = mapping.Targets[0];
            result.FieldsAttempted++;

            var lineKey = (source.LineName, source.LineOccurrence);
            if (!lineBuffers.TryGetValue(lineKey, out var buffer))
            {
                // Largura da linha: LimitOfCaracters do layout quando disponível (escopo MVP fixo);
                // sem ele, usa best-effort o próprio Start+Length do maior campo já visto — nunca
                // lança por falta de metadado, só produz uma linha mais curta que o esperado.
                var width = lineWidth > 0 ? lineWidth : source.StartPosition + source.Length;
                buffer = new string(' ', Math.Max(width, source.StartPosition + source.Length)).ToCharArray();
                lineBuffers[lineKey] = buffer;
                lineOrder.Add(lineKey);
            }

            if (navigator == null)
            {
                result.Warnings.Add(new ReconstructionWarning
                {
                    LineName = source.LineName,
                    FieldName = source.FieldName,
                    Occurrence = source.LineOccurrence,
                    Kind = ReconstructionWarningKind.FieldNotFoundInXml,
                    Message = "XML de destino ausente/inválido."
                });
                return;
            }

            string? value = null;
            try
            {
                var node = navigator.SelectSingleNode(target.Xpath);
                value = node?.Value;
            }
            catch (XPathException ex)
            {
                _logger.LogDebug(ex, "XPath inválido/não suportado em reconstrução reversa: {Xpath}", target.Xpath);
            }

            if (string.IsNullOrEmpty(value))
            {
                result.Warnings.Add(new ReconstructionWarning
                {
                    LineName = source.LineName,
                    FieldName = source.FieldName,
                    Occurrence = source.LineOccurrence,
                    Kind = ReconstructionWarningKind.FieldNotFoundInXml,
                    Message = $"Nenhum valor encontrado no XML para o XPath '{target.Xpath}'."
                });
                return;
            }

            var truncated = false;
            if (value.Length > source.Length)
            {
                value = value.Substring(0, source.Length);
                truncated = true;
            }

            // Padding: alinhamento não é conhecido aqui (TxtFieldReference não carrega
            // AlignmentType — só o crosswalk do layout original teria isso, e não é repassado ao
            // FieldToXmlMapping) — best-effort declarado: preenche à esquerda com espaço (mais seguro
            // para texto; numérico com zero à esquerda é uma melhoria futura quando o tipo do campo
            // estiver disponível no mapeamento).
            var padded = value.PadRight(source.Length);
            for (var i = 0; i < source.Length && source.StartPosition + i < buffer.Length; i++)
                buffer[source.StartPosition + i] = padded[i];

            result.FieldsReconstructed++;

            if (truncated)
            {
                result.Warnings.Add(new ReconstructionWarning
                {
                    LineName = source.LineName,
                    FieldName = source.FieldName,
                    Occurrence = source.LineOccurrence,
                    Kind = ReconstructionWarningKind.ValueTruncated,
                    Message = $"Valor do XML ({value.Length + (truncated ? 1 : 0)} chars antes do corte) excede o tamanho declarado ({source.Length}) — truncado."
                });
            }
        }
    }
}
