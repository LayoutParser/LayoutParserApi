using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using XslSynth.Model;
using XslSynth.Synthesis;

namespace XslSynth.Core;

// ─────────────────────────────────────────────────────────────────────────────
// ProvenancePublisher — A6 (Trilha A, escopo corrigido 2026-07-21): sidecar de
// proveniência (generated-provenance.json) + passo de "publicação" que remove
// o XComment de debug do candidato ANTES de qualquer promoção pra Mapper.XslContent.
//
// COBRE OS DOIS MECANISMOS (correção de escopo da Aria, ver
// docs/architecture/ia-fiscal-diagnosis-vision.md §3.1):
//   • LinkMappingItem (~237 campos, a maioria — inclui o exemplo real da
//     CHAVEACESSO, confirmado LinkMapping pelo dono do projeto) — via
//     mapper.LinkMappings + GuidXPathCatalog (resolução do destino).
//   • MapperRule/RuleTranslation (~98 campos via DSL) — via DslRuleTranslator/
//     DslBlockInterpreter (RuleTranslation já traz Rule+Source de qualquer um
//     dos dois; ContentValue da Rule original é reaproveitado p/ extrair os
//     campos de INPUT referenciados, "I.LINHAnnn/Campo").
//
// POR QUE NÃO reaproveitar os XComment de debug já embutidos (parsing de volta
// o texto do comentário): a informação já está disponível ESTRUTURADA em
// LinkMappingItem/RuleTranslation — ler dessas fontes é mais robusto que
// reparsear texto de comentário, e desacopla o sidecar de qualquer mudança
// futura no FORMATO do XComment de debug (que continua existindo — é uma
// ferramenta de debug legítima do loop OFFLINE, ver §3.1 do doc de visão:
// "hoje isso só afeta o loop offline/prototype"). O que este arquivo GARANTE
// é que a cópia "publicável" (a que um dia vira Mapper.XslContent) nunca
// carrega esse rastro — não que o candidato de debug pare de tê-lo.
//
// Bug corrigido (5.2): LinkMappingTranspiler embute
// "target=... type=... input=... [guid-catalog=hit xpath=...]" como filho
// LITERAL do elemento de saída dentro do <xsl:template> — isso NÃO é uma
// instrução xsl:, é conteúdo do literal result element, e por isso
// SOBREVIVE a XslCompiledTransform.Transform() (comentários filhos de um
// elemento literal do resultado são copiados para a árvore de saída, ao
// contrário de <xsl:comment> que seria a forma correta de GERAR um
// comentário no output). CandidateBuilder tem o MESMO padrão nos nós de
// regra ("rule='...' src=..."). StripDebugComments() remove os dois,
// genericamente, sem precisar de 2 fixes especializados.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Uma entrada do sidecar: XPath de saída → {mecanismo, regra, fonte, campos de input}.</summary>
public sealed record ProvenanceEntry(
    string OutputXPath,
    string Mecanismo,      // "LinkMapping" | "DslRule"
    string Regra,          // Name do LinkMappingItem/MapperRule (ou GUID, se Name ausente)
    string Fonte,          // qualidade/origem da resolução (ver BuildLinkEntries/BuildRuleEntries)
    IReadOnlyList<string> CamposInput);

/// <summary>Sidecar completo — artefato versionável, gerado offline ao lado do XSLT.</summary>
public sealed class ProvenanceSidecar
{
    public required string Mapeador { get; init; }
    public DateTime GeradoEmUtc { get; init; } = DateTime.UtcNow;
    public int TotalLinkMappings { get; init; }
    public int TotalRules { get; init; }

    /// <summary>LinkMappings sem folha derivável (mesmo critério de exclusão do LinkMappingTranspiler) — transparência, não erro.</summary>
    public required IReadOnlyList<string> LinkMappingsSemFolha { get; init; }

    public required List<ProvenanceEntry> Entradas { get; init; }

    [JsonIgnore]
    public int Total => Entradas.Count;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        // Padrão do projeto (mesmo de XsdDeltaModels/NtPipeline): preservar XPath/regex legíveis no JSON.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);
}

/// <summary>Resultado do passo de publicação: sidecar + candidato limpo + auto-verificação.</summary>
public sealed record PublishResult(
    ProvenanceSidecar Sidecar,
    XDocument CandidatoPublicavel,
    int ComentariosRemovidos,
    int ComentariosRemanescentes); // deve ser SEMPRE 0 — gate de auto-verificação

public static class ProvenancePublisher
{
    // Campos de input referenciados na DSL: "I.LINHA050/Campo" → captura LINHA050/Campo.
    private static readonly Regex InputRef =
        new(@"I\.([A-Za-z0-9_]+/[A-Za-z0-9_]+)", RegexOptions.Compiled);

    /// <summary>
    /// Monta o sidecar de proveniência a partir dos DOIS mecanismos (LinkMapping +
    /// DslRule/interpreter) e produz a versão "publicável" do candidato (sem os
    /// XComment de debug). Não escreve em disco — o chamador decide os caminhos.
    /// </summary>
    public static PublishResult Publish(
        MapperVo mapper,
        GuidXPathCatalog? targetCatalog,
        IReadOnlyList<RuleTranslation> ruleTranslations,
        XDocument candidate)
    {
        var semFolha = new List<string>();
        var entradas = new List<ProvenanceEntry>();
        entradas.AddRange(BuildLinkEntries(mapper, targetCatalog, semFolha));
        entradas.AddRange(BuildRuleEntries(ruleTranslations));

        var sidecar = new ProvenanceSidecar
        {
            Mapeador = mapper.Name ?? mapper.MapperGuid ?? "?",
            TotalLinkMappings = mapper.LinkMappings.Count,
            TotalRules = mapper.Rules.Count,
            LinkMappingsSemFolha = semFolha,
            Entradas = entradas,
        };

        var (limpo, removidos) = StripDebugComments(candidate);
        var remanescentes = limpo.Descendants().Nodes().OfType<XComment>().Count();

        return new PublishResult(sidecar, limpo, removidos, remanescentes);
    }

    // ── Mecanismo 1: LinkMapping (~237 campos, a maioria) ─────────────────────
    private static IEnumerable<ProvenanceEntry> BuildLinkEntries(
        MapperVo mapper, GuidXPathCatalog? targetCatalog, List<string> semFolha)
    {
        foreach (var link in mapper.LinkMappings.OrderBy(l => l.Sequence))
        {
            // Mesmo padrão de LinkMappingTranspiler.Transpile: uma única resolução,
            // reaproveitada (evita 2 lookups e mantém entry/resolvidoPeloCatalogo consistentes).
            GuidXPathEntry? entry = null;
            var resolvidoPeloCatalogo = targetCatalog is not null
                && targetCatalog.TryResolve(link.TargetGuid, out entry) && !entry.IsAttribute;

            var outputXPath = resolvidoPeloCatalogo ? entry!.XPath : link.TargetLeafName;

            if (string.IsNullOrWhiteSpace(outputXPath))
            {
                semFolha.Add(link.Name ?? link.TargetGuid ?? link.ElementGuid ?? "?");
                continue;
            }

            var campos = string.IsNullOrWhiteSpace(link.InputGuid)
                ? Array.Empty<string>()
                : new[] { link.InputGuid! };

            yield return new ProvenanceEntry(
                OutputXPath: outputXPath,
                Mecanismo: "LinkMapping",
                Regra: link.Name ?? link.TargetGuid ?? "?",
                Fonte: resolvidoPeloCatalogo ? "CatalogoGuid" : "ConvencaoDeNome(simbolico)",
                CamposInput: campos);
        }
    }

    // ── Mecanismo 2: DslRule (~98 campos, via interpreter/Ollama/fallback) ────
    private static IEnumerable<ProvenanceEntry> BuildRuleEntries(IReadOnlyList<RuleTranslation> translations)
    {
        foreach (var tr in translations)
        {
            if (string.IsNullOrWhiteSpace(tr.TargetPath)) continue;

            var dsl = tr.Rule.ContentValue ?? "";
            var campos = InputRef.Matches(dsl)
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            yield return new ProvenanceEntry(
                OutputXPath: tr.TargetPath!,
                Mecanismo: "DslRule",
                Regra: tr.Rule.Name ?? "?",
                Fonte: tr.Source.ToString(),
                CamposInput: campos);
        }
    }

    /// <summary>
    /// Remove TODO XComment filho de elemento (debug de LinkMappingTranspiler
    /// E de CandidateBuilder — mesmo anti-padrão nos dois) de uma CÓPIA do
    /// candidato. Genérico por desenho: não depende do texto/formato do
    /// comentário, então cobre qualquer XComment de debug presente hoje OU
    /// adicionado por código futuro — não são precisos 2 fixes especializados.
    /// </summary>
    private static (XDocument Limpo, int Removidos) StripDebugComments(XDocument candidate)
    {
        var limpo = new XDocument(candidate);
        var comentarios = limpo.Descendants().Nodes().OfType<XComment>().ToList();
        foreach (var c in comentarios)
            c.Remove();
        return (limpo, comentarios.Count);
    }
}
