namespace XslSynth.Model;

/// <summary>
/// Espelha (de forma enxuta e standalone) a estrutura do MapperVO do Sysmiddle.
/// No runtime real isto vive em LayoutParserApi.Models.Entities.MapperVo; aqui é
/// replicado para manter este projeto de síntese 100% desacoplado do runtime
/// Windows-only (ver docs/architecture/ia-xslt-synthesis.md §9).
/// </summary>
public sealed class MapperVo
{
    public string? MapperGuid { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? InputLayoutGuid { get; set; }
    public string? TargetLayoutGuid { get; set; }

    /// <summary>XSL já existente (semente few-shot / referência para o LLM), se houver.</summary>
    public string? XslContent { get; set; }

    /// <summary>Mapeamentos diretos campo→campo (transpilados por código, sem IA).</summary>
    public List<LinkMappingItem> LinkMappings { get; } = new();

    /// <summary>Regras com código C# (traduzidas para XSLT pelo sintetizador).</summary>
    public List<MapperRule> Rules { get; } = new();
}

/// <summary>Mapeamento DIRETO campo→campo.</summary>
public sealed class LinkMappingItem
{
    public string? Name { get; set; }
    public int Sequence { get; set; }

    // No MVP standalone guardam o XPath JÁ RESOLVIDO. Produção: GUID → XPath via catálogo.
    public string? SourcePath { get; set; }   // origem  (<InputLayoutGuid>)
    public string? TargetPath { get; set; }   // destino (<TargetLayoutGuid>)

    public bool IsToTruncateValue { get; set; }
    public string? RemoveWhiteSpaceType { get; set; }
    public string? DefaultValue { get; set; }
    public bool AllowEmpty { get; set; }
}

/// <summary>Regra com código C# arbitrário (condicional, cálculo, formatação).</summary>
public sealed class MapperRule
{
    public string? Name { get; set; }
    public int Sequence { get; set; }
    public string? Description { get; set; }

    /// <summary>Destino da regra (<TargetElementGuid>), como XPath no MVP.</summary>
    public string? TargetPath { get; set; }

    /// <summary>Código C# da regra — o pedaço que o LLM traduz para XSLT.</summary>
    public string? ContentValue { get; set; }
}
