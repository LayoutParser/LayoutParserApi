using LayoutParserApi.Models.Entities;

using Newtonsoft.Json;

using XslSynth.Core.StructuralResolution;
using XslSynth.Prompting;

// Ambíguo com LayoutParserApi.Models.Entities (MapperVo/MapperRule/LinkMappingItem também existem
// lá, do lado do runtime Windows-only — ver ai/XslSynth.Contracts/Model/MapperVo.cs). Este serviço
// trabalha exclusivamente com os tipos de XslSynth.Model (o MapperVo real vem de RealMapperParser,
// que produz XslSynth.Model.MapperVo, não o tipo espelho de Models.Entities).
using MapperVo = XslSynth.Model.MapperVo;
using LinkMappingItem = XslSynth.Model.LinkMappingItem;
using MapperRule = XslSynth.Model.MapperRule;
using FieldToXmlMapping = XslSynth.Model.FieldToXmlMapping;
using TxtFieldReference = XslSynth.Model.TxtFieldReference;
using MappingKind = XslSynth.Model.MappingKind;

namespace LayoutParserApi.Services.Transformation.StructuralResolution
{
    /// <summary>
    /// Itens 2/6 da divisão de trabalho da issue #140 (design §8): conecta o motor de resolução
    /// estrutural já implementado (<c>ai/XslSynth.Contracts/Core/StructuralResolution/</c>, commit
    /// <c>36ae5cb</c>) ao pipeline real da API — <see cref="Layout"/>/<see cref="ParsedField"/> reais
    /// (do parse posicional) e <see cref="MapperVo"/> real (via <see cref="XslSynth.Core.RealMapperParser"/>
    /// sobre o mapper decifrado, Parser B canônico da issue #139).
    ///
    /// Escopo desta versão (ver resposta da tarefa para a justificativa completa): a fonte de
    /// funções conhecidas (<c>FunctionCatalog</c>, Camada 2) não está com um caminho de DLL
    /// configurado em nenhum host hoje — nenhum chamador existente resolve isso. Por isso todo
    /// <see cref="MappingCandidate.KnownFunctions"/> sai <c>null</c> aqui, o que o critério §5
    /// (condição 5) já trata como "não confirmado" ⇒ <c>best-effort</c> — degrada corretamente, não
    /// lança. Conectar um <c>FunctionCatalog</c> real é trabalho futuro, fora do escopo desta tarefa.
    /// </summary>
    public class FieldMappingCompositionService
    {
        private readonly StructuralXmlCatalogCacheService _catalogCache;
        private readonly MappingStructureService _mappingStructure;
        private readonly ILogger<FieldMappingCompositionService> _logger;

        public FieldMappingCompositionService(
            StructuralXmlCatalogCacheService catalogCache,
            MappingStructureService mappingStructure,
            ILogger<FieldMappingCompositionService> logger)
        {
            _catalogCache = catalogCache;
            _mappingStructure = mappingStructure;
            _logger = logger;
        }

        /// <summary>
        /// Compõe <see cref="FieldToXmlMapping"/>[] a partir dos dados JÁ resolvidos pelo chamador —
        /// não faz I/O de banco/decrypt (isso é responsabilidade do endpoint/controller, que já tem
        /// os serviços certos injetados). Nunca lança: qualquer falha de composição individual vira
        /// log + item omitido, o request principal (execute-candidates/parse) nunca é afetado.
        /// </summary>
        /// <param name="sourceLayout">Layout de origem já carregado (posicional), com <see cref="LineElement"/>/
        /// campos aninhados — usado para resolver GUID/nome/repetição das origens.</param>
        /// <param name="parsedFields">Campos já parseados do documento de entrada (fonte real de
        /// <c>lineOccurrence</c> — <see cref="ParsedField.Occurrence"/>, nunca sintético).</param>
        /// <param name="mapperVo">Mapper real, já parseado via <c>RealMapperParser</c> a partir do
        /// conteúdo decifrado do banco.</param>
        public IReadOnlyList<FieldToXmlMapping> Compose(
            Layout sourceLayout,
            IReadOnlyList<ParsedField> parsedFields,
            MapperVo mapperVo)
        {
            var catalog = _catalogCache.GetOrBuildCatalog(mapperVo.TargetLayoutGuid);
            if (catalog == null)
            {
                // Sem catálogo XML de destino não há como resolver Targets — degrade para "nenhum
                // mapeamento estrutural disponível" em vez de lançar (dotnet-standards.md).
                return Array.Empty<FieldToXmlMapping>();
            }

            var composer = new FieldToXmlMappingComposer(catalog);
            var crosswalk = BuildSourceCrosswalk(sourceLayout);
            var results = new List<FieldToXmlMapping>();

            foreach (var link in mapperVo.LinkMappings)
            {
                try
                {
                    var candidate = BuildLinkCandidate(link, crosswalk, parsedFields);
                    if (candidate != null)
                        results.Add(composer.Compose(candidate));
                }
                catch (Exception ex)
                {
                    // Isolamento por item (mesmo princípio de ExecuteSysmiddleCandidatesAsync): uma
                    // regra/link malformado não pode derrubar a composição inteira.
                    _logger.LogWarning(ex, "Falha ao compor field mapping para LinkMappingItem {Name}", link.Name);
                }
            }

            foreach (var rule in mapperVo.Rules)
            {
                try
                {
                    var structuredRule = _mappingStructure.ParseRule(rule);
                    var candidate = BuildRuleCandidate(rule, structuredRule, crosswalk, parsedFields);
                    if (candidate != null)
                        results.Add(composer.Compose(candidate));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Falha ao compor field mapping para Rule {Name}", rule.Name);
                }
            }

            return results;
        }

        private static MappingCandidate? BuildLinkCandidate(
            LinkMappingItem link, Dictionary<string, SourceCrosswalkEntry> crosswalkByGuid, IReadOnlyList<ParsedField> parsedFields)
        {
            if (string.IsNullOrWhiteSpace(link.TargetLeafName))
                return null;

            var sources = new List<TxtFieldReference>();
            var allResolved = false;
            var anyRepeats = false;

            if (!string.IsNullOrWhiteSpace(link.InputGuid) && crosswalkByGuid.TryGetValue(link.InputGuid, out var entry))
            {
                allResolved = true;
                anyRepeats = entry.LineElement.IsPositionalGroupRepetition;
                var reference = BuildTxtFieldReference(entry, parsedFields);
                if (reference != null)
                    sources.Add(reference);
            }

            return new MappingCandidate(
                MappingId: $"link:{link.Name}",
                Sources: sources,
                Kind: MappingKindClassifier.ClassifyLinkMapping(),
                TargetPath: link.TargetLeafName!,
                TargetPathIsFullPath: false, // LinkMappingItem só resolve até a folha (design §3 item 2)
                Functions: Array.Empty<string>(),
                LoopType: null,
                AllSourcesResolvedFromOriginLayout: allResolved,
                SourcesHavePositionalGroupRepetition: anyRepeats,
                KnownFunctions: null);
        }

        private static MappingCandidate? BuildRuleCandidate(
            MapperRule rule, StructuredRule structuredRule,
            Dictionary<string, SourceCrosswalkEntry> crosswalkByGuid, IReadOnlyList<ParsedField> parsedFields)
        {
            var targetPath = rule.TargetPath;
            if (string.IsNullOrWhiteSpace(targetPath))
                return null;

            var kind = MappingKindClassifier.ClassifyRule(structuredRule);

            var sources = new List<TxtFieldReference>();
            var allResolved = kind == MappingKind.Static; // sem sources ⇒ condição 1 satisfeita trivialmente
            var anyRepeats = false;

            // AllSources vem como "Linha/Campo" (I.<Linha>/<Campo> sem o prefixo, design §1) — a DSL
            // não carrega GUID de origem, só nome; resolve-se por nome via o crosswalk NAME→GUID do
            // layout de origem (mesma convenção já usada pelo parser posicional).
            var byName = crosswalkByGuid.Values
                .ToLookup(e => $"{e.LineName}/{e.FieldName}", StringComparer.Ordinal);

            if (kind != MappingKind.Static)
            {
                allResolved = structuredRule.AllSources.Count > 0;
                foreach (var source in structuredRule.AllSources)
                {
                    var matches = byName[source].ToList();
                    if (matches.Count == 0)
                    {
                        allResolved = false;
                        continue;
                    }

                    // Pode haver mais de um LineElement com o mesmo nome em pontos distintos do
                    // layout (mesma ressalva já documentada em XmlLayoutCatalog para o lado XML) —
                    // usa o primeiro, best-effort de desambiguação; não é o foco desta tarefa.
                    var entry = matches[0];
                    anyRepeats = anyRepeats || entry.LineElement.IsPositionalGroupRepetition;
                    var reference = BuildTxtFieldReference(entry, parsedFields);
                    if (reference != null)
                        sources.Add(reference);
                    else
                        allResolved = false;
                }
            }

            return new MappingCandidate(
                MappingId: $"rule:{rule.Name}",
                Sources: sources,
                Kind: kind,
                TargetPath: targetPath!,
                TargetPathIsFullPath: true, // Rule.TargetPath já é o caminho completo (design §1, via T.<path> da DSL)
                Functions: structuredRule.AllFunctions,
                LoopType: structuredRule.LoopType,
                AllSourcesResolvedFromOriginLayout: allResolved,
                SourcesHavePositionalGroupRepetition: anyRepeats,
                KnownFunctions: null);
        }

        private static TxtFieldReference? BuildTxtFieldReference(SourceCrosswalkEntry entry, IReadOnlyList<ParsedField> parsedFields)
        {
            // Fonte real de lineOccurrence (design §4.1): fragmento físico bruto
            // (IsAggregatedOccurrence == false), nunca o agregado (Occurrence == 0).
            var physical = parsedFields
                .Where(f => !f.IsAggregatedOccurrence
                    && string.Equals(f.LineName, entry.LineName, StringComparison.Ordinal)
                    && string.Equals(f.FieldName, entry.FieldName, StringComparison.Ordinal))
                .OrderBy(f => f.Occurrence)
                .FirstOrDefault();

            // Sem ocorrência física real no documento de entrada (campo declarado no layout mas não
            // presente no parse) ainda é uma origem estruturalmente válida — lineOccurrence cai no
            // default de ParsedField (1), preservando LineGuid/FieldGuid reais do crosswalk.
            var lineOccurrence = physical?.Occurrence ?? 1;
            var start = physical?.Start ?? 0;
            var length = physical?.Length ?? 0;

            return new TxtFieldReference(
                entry.LineElement.ElementGuid,
                entry.LineName,
                entry.FieldElement.ElementGuid,
                entry.FieldName,
                lineOccurrence,
                start,
                length);
        }

        /// <summary>Varre recursivamente <see cref="Layout.Elements"/> (mesma convenção JSON já usada
        /// em <c>LayoutParserService.CollectAllElements</c>/<c>LayoutValidationService.SeparateElements</c>
        /// para desserializar <see cref="FieldElement"/>/<see cref="LineElement"/> aninhados) e indexa
        /// por <c>FieldElement.ElementGuid</c> — é o crosswalk que permite ir de um GUID de origem
        /// (LinkMappingItem.InputGuid) ou de um par Linha/Campo (StructuredRule.AllSources) até o
        /// LineElement/FieldElement reais do layout de origem.</summary>
        private static Dictionary<string, SourceCrosswalkEntry> BuildSourceCrosswalk(Layout sourceLayout)
        {
            var byGuid = new Dictionary<string, SourceCrosswalkEntry>(StringComparer.Ordinal);
            foreach (var line in sourceLayout.Elements ?? new List<LineElement>())
            {
                Walk(line);
            }
            return byGuid;

            void Walk(LineElement line)
            {
                if (line?.Elements == null)
                    return;

                foreach (var elementJson in line.Elements)
                {
                    FieldElement? field = null;
                    try { field = JsonConvert.DeserializeObject<FieldElement>(elementJson); }
                    catch { /* não era FieldElement — tenta LineElement aninhado abaixo */ }

                    if (field != null && !string.IsNullOrEmpty(field.Name) && !string.IsNullOrEmpty(field.ElementGuid))
                    {
                        byGuid[field.ElementGuid] = new SourceCrosswalkEntry(line, field, line.Name, field.Name);
                        continue;
                    }

                    try
                    {
                        var nested = JsonConvert.DeserializeObject<LineElement>(elementJson);
                        if (nested != null && !string.IsNullOrEmpty(nested.Name))
                            Walk(nested);
                    }
                    catch { /* nem FieldElement nem LineElement reconhecível — ignora, não é fatal */ }
                }
            }
        }

        private sealed record SourceCrosswalkEntry(LineElement LineElement, FieldElement FieldElement, string LineName, string FieldName);
    }
}
