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

    // ── Campos do MapeadorVO REAL (Sysmiddle) ──────────────────────────────
    // Preenchidos pelo RealMapperParser. Os GUIDs abaixo referenciam o catálogo
    // de layout (input/target) que ainda NÃO temos → o path é resolvido só até a
    // FOLHA (via convenção de nome), não o caminho completo. Ver report do loop.
    public string? ElementGuid { get; set; }
    public string? InputGuid { get; set; }        // FLD_/LIN_ (origem no layout de input)
    public string? TargetGuid { get; set; }       // TAG_/GRT_/ATT_ (destino no layout NF-e)
    public string? TargetType { get; set; }        // prefixo do TargetGuid: TAG|GRT|ATT|SEQ
    public string? TargetLeafName { get; set; }    // nome da folha derivado do sufixo de Name
    public bool IsRequired { get; set; }
}

/// <summary>
/// Regra em DSL do Sysmiddle (condicional, cálculo, formatação). Apesar do nome
/// histórico "C#", o <see cref="ContentValue"/> real é a DSL Sysmiddle:
/// <c>T.&lt;path&gt;</c> = saída, <c>I.LINHA/&lt;campo&gt;</c> = input,
/// <c>#.tmp</c>/<c>$.var</c> = variáveis, <c>if(...) begin ... end</c>.
/// </summary>
public sealed class MapperRule
{
    public string? Name { get; set; }
    public int Sequence { get; set; }
    public string? Description { get; set; }

    /// <summary>
    /// Destino da regra como XPath. No mapeador real é DERIVADO da primeira
    /// atribuição <c>T.&lt;path&gt;</c> da DSL (que já é o caminho completo na
    /// árvore NF-e) — não do <see cref="TargetElementGuid"/> (que exigiria catálogo).
    /// </summary>
    public string? TargetPath { get; set; }

    /// <summary>Corpo da regra em DSL Sysmiddle — o pedaço que o LLM traduz para XSLT.</summary>
    public string? ContentValue { get; set; }

    // ── Campos do MapeadorVO REAL (Sysmiddle) ──────────────────────────────
    public string? ElementGuid { get; set; }
    public string? TargetElementGuid { get; set; }  // ATT_/TAG_/GRT_/SEQ_
    public string? TargetType { get; set; }          // prefixo do TargetElementGuid
    public string? ParentElement { get; set; }       // ex.: "cEAN    (-, Str_MAX)"
    public bool IsRequired { get; set; }
}
