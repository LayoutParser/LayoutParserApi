using LayoutParserApi.Models.Entities;
using LayoutParserApi.Services.Transformation;
using LayoutParserApi.Services.Transformation.StructuralResolution;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Newtonsoft.Json;

// Ambíguo com LayoutParserApi.Models.Entities (mesmo racional do FieldMappingCompositionService).
using MapperVo = XslSynth.Model.MapperVo;
using LinkMappingItem = XslSynth.Model.LinkMappingItem;
using MappingKind = XslSynth.Model.MappingKind;
using Confidence = XslSynth.Model.Confidence;

namespace LayoutParserApi.Tests.StructuralResolution;

/// <summary>
/// Item 5 da tarefa (issue #140, itens 2/6-9): valida que o wiring PONTA A PONTA funciona — Layout
/// posicional real (com <see cref="LineElement"/>/<see cref="FieldElement"/> reais) + ParsedField
/// reais + MapperVo real (mesma forma que <c>RealMapperParser</c> produz, sem passar pelo decrypt/
/// banco — isso é o que os 25 testes unitários do motor já cobrem) + catálogo XML real (mesmo XSD
/// NF-e público usado em <c>ai/XslSynth.Core.Tests/StructuralResolution</c>) → <see cref="FieldToXmlMapping"/>[]
/// compostos corretamente. NÃO reimplementa os testes unitários do motor (classificador/composer/
/// resolução de ocorrência) — só confirma que <see cref="FieldMappingCompositionService"/> liga as
/// peças reais sem quebrar nada no meio do caminho.
/// </summary>
public sealed class FieldMappingCompositionServiceIntegrationTests
{
    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "StructuralResolution", "fixtures", fileName);

    private static FieldMappingCompositionService BuildService(string? schemaPath)
    {
        var options = Options.Create(new StructuralResolutionOptions
        {
            NfeSchemaPath = schemaPath ?? string.Empty,
            NfeRootElementName = "NFe"
        });
        var catalogCache = new StructuralXmlCatalogCacheService(
            new MemoryCache(new MemoryCacheOptions()),
            options,
            NullLogger<StructuralXmlCatalogCacheService>.Instance);
        var mappingStructure = new MappingStructureService();
        return new FieldMappingCompositionService(catalogCache, mappingStructure, NullLogger<FieldMappingCompositionService>.Instance);
    }

    /// <summary>Layout de origem sintético: 1 linha "DET", 1 campo "NATOP" — mesma convenção JSON
    /// (Type discriminador + serialização Newtonsoft) usada pelo Sysmiddle real. Usa "natOp" como
    /// folha de destino (não "CFOP") porque CFOP é AMBÍGUO no XSD real da NF-e (aparece 2x — mesmo
    /// gotcha documentado na memória de @lp-parser-llm e em XmlLayoutStructureParserTests); "natOp"
    /// é confirmado único, permitindo testar a resolução por nome de folha sem cair no caso
    /// "ambíguo, não resolvido" do composer (que é um comportamento correto, mas não o que este
    /// teste de wiring quer exercitar).</summary>
    private static Layout BuildSourceLayout(out string lineGuid, out string fieldGuid)
    {
        lineGuid = "LIN_det";
        fieldGuid = "FLD_natop";

        var field = new FieldElement
        {
            ElementGuid = fieldGuid,
            Name = "NATOP",
            Sequence = 1,
            LengthField = 60
        };

        var line = new LineElement
        {
            ElementGuid = lineGuid,
            Name = "DET",
            Sequence = 1,
            Elements = new List<string> { JsonConvert.SerializeObject(field) }
        };

        return new Layout
        {
            LayoutGuid = "LAY_source",
            Name = "LayoutSinteticoIssue140",
            Elements = new List<LineElement> { line }
        };
    }

    private static List<ParsedField> BuildParsedFields() => new()
    {
        new ParsedField
        {
            LineName = "DET",
            FieldName = "NATOP",
            Occurrence = 1,
            OccurrenceCount = 1,
            IsAggregatedOccurrence = false,
            Value = "VENDA DE MERCADORIA",
            Start = 0,
            Length = 60
        }
    };

    /// <summary>MapperVo sintético — mesma forma que <c>RealMapperParser.Parse</c> produziria a
    /// partir de um MapperVO real decifrado: 1 <see cref="LinkMappingItem"/> direto NATOP→natOp.</summary>
    private static MapperVo BuildMapperVo(string fieldGuid)
    {
        var mapper = new MapperVo
        {
            MapperGuid = "MAP_sintetico_issue140",
            TargetLayoutGuid = "NFe-target"
        };
        mapper.LinkMappings.Add(new LinkMappingItem
        {
            Name = "NaturezaDaOperacao_natOp",
            InputGuid = fieldGuid,
            TargetGuid = "TAG_natOp",
            TargetLeafName = "natOp"
        });
        return mapper;
    }

    [Fact]
    public void Compose_LinkMappingDireto_ResolveTargetNaArvoreRealDoXsdEUsaOcorrenciaFisicaReal()
    {
        var schemaPath = FixturePath("nfe_v4.00.xsd");
        Assert.True(File.Exists(schemaPath), $"Fixture XSD não encontrada em {schemaPath} — verifique CopyToOutputDirectory no csproj.");

        var service = BuildService(schemaPath);
        var sourceLayout = BuildSourceLayout(out _, out var fieldGuid);
        var parsedFields = BuildParsedFields();
        var mapperVo = BuildMapperVo(fieldGuid);

        var mappings = service.Compose(sourceLayout, parsedFields, mapperVo);

        var mapping = Assert.Single(mappings);
        Assert.Equal(MappingKind.Direct, mapping.Kind);
        var target = Assert.Single(mapping.Targets);
        Assert.Contains("natOp", target.Xpath);
        Assert.Equal(Confidence.BestEffort, mapping.Confidence); // fallback por nome de folha, nunca authoritative (design §5 condição 2)

        var source = Assert.Single(mapping.Sources);
        Assert.Equal(1, source.LineOccurrence); // de ParsedField.Occurrence real, não sintético/hardcoded no composer
        Assert.Equal(fieldGuid, source.FieldGuid);
    }

    [Fact]
    public void Compose_SemXsdConfigurado_DegradaParaListaVaziaSemLancar()
    {
        var service = BuildService(schemaPath: null); // StructuralResolution:NfeSchemaPath ausente — cenário de host sem XSD configurado
        var sourceLayout = BuildSourceLayout(out _, out var fieldGuid);
        var mappings = service.Compose(sourceLayout, BuildParsedFields(), BuildMapperVo(fieldGuid));

        Assert.Empty(mappings); // degrada, não lança (dotnet-standards.md)
    }
}
