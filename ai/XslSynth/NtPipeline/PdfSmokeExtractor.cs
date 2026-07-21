using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace XslSynth.NtPipeline;

// ─────────────────────────────────────────────────────────────────────────────
// PdfSmokeExtractor — S2 do pipeline "NT nova" (protótipo B5 P-2, exploração):
// mede se dá para extrair texto de um PDF de Nota Técnica da SEFAZ e segmentar
// por seção/Grupo/regra de validação o suficiente para alimentar o LLM depois
// (NtSemantics, ainda não implementado — isso é só o smoke test de viabilidade).
//
// Decisão de biblioteca: PdfPig (pacote NuGet "PdfPig", Apache-2.0, mantido
// ativamente pelo autor original UglyToad) em vez de pypdf (Python) — o ambiente
// de execução real deste projeto é .NET/Linux (WSL) SEM acesso a pip/apt com
// privilégio de root; PdfPig também alinha com a recomendação de arquitetura de
// "projeto .NET 10 standalone Linux-native" (ia-xslt-synthesis.md §9).
//
// 100% determinístico, sem LLM — mede VIABILIDADE de extração/segmentação, não
// produz NtRuleMap.json (isso é o S2 completo, mid-term, fora deste smoke test).
// Desenho: docs/architecture/nt-pipeline-design.md §4-5 (P-2).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Resultado do smoke test de extração de um PDF de NT.</summary>
public sealed record PdfSmokeResult(
    int Paginas,
    int TotalCaracteres,
    IReadOnlyList<string> TitulosDeSecao,
    IReadOnlyList<string> RegrasDeValidacao,
    IReadOnlyList<string> MencoesDeGrupo,
    string AmostraPrimeiraPagina);

public static class PdfSmokeExtractor
{
    // Regras de validação da NF-e são nomeadas "<letra-do-grupo><nn>-<nn>"
    // (ex.: N11-10, B12-10, NT01-01) — âncora textual estável entre NTs.
    private static readonly Regex RegraValidacao =
        new(@"\b[A-Z]{1,3}\d{2}(?:[a-z])?-\d{2}\b", RegexOptions.Compiled);

    // "Grupo X" / "grupo X" (ex.: "Grupo UB", "Grupo W03", "Grupo N01") — a
    // unidade de segmentação que o S2 real precisa reconhecer (nt-pipeline-design §2).
    private static readonly Regex MencaoGrupo =
        new(@"\bGrupo\s+[A-Z][A-Za-z0-9]{0,6}\b", RegexOptions.Compiled);

    // Título de seção: linha curta iniciada por numeração decimal ("1.", "2.3",
    // "6.1.2") — convenção comum de sumário/capítulo em Notas Técnicas da SEFAZ.
    private static readonly Regex TituloSecao =
        new(@"^\s*\d+(?:\.\d+){0,3}\.?\s+[A-ZÀ-Ú][^\n]{3,80}$", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>Abre o PDF, extrai texto por página e mede os anchors estruturais.
    /// Degrade gracioso: PDF ilegível/corrompido → lança (chamador decide; aqui é
    /// exploração via CLI, não caminho de produção com fallback silencioso).</summary>
    public static PdfSmokeResult Extract(string pdfPath)
    {
        using var doc = PdfDocument.Open(pdfPath);
        var paginas = doc.NumberOfPages;
        var textoCompleto = new System.Text.StringBuilder();
        string? primeiraPagina = null;

        foreach (var page in doc.GetPages())
        {
            var texto = page.Text;
            textoCompleto.AppendLine(texto);
            primeiraPagina ??= texto;
        }

        var tudo = textoCompleto.ToString();
        var titulos = TituloSecao.Matches(tudo)
            .Select(m => m.Value.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(40)
            .ToList();
        var regras = RegraValidacao.Matches(tudo)
            .Select(m => m.Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        var grupos = MencaoGrupo.Matches(tudo)
            .Select(m => m.Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        return new PdfSmokeResult(
            paginas, tudo.Length, titulos, regras, grupos,
            (primeiraPagina ?? "").Length > 400 ? primeiraPagina![..400] : primeiraPagina ?? "");
    }
}
