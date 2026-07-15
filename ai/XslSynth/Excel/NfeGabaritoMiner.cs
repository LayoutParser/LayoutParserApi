using System.Text;
using System.Xml.Linq;

namespace XslSynth.Excel;

// ─────────────────────────────────────────────────────────────────────────────
// NfeGabaritoMiner — camada 3 do catálogo: ANCORAGEM POR VALOR num par gabarito
// real (TXT posicional de entrada + XML NF-e de saída, produzido pelo runtime
// Sysmiddle de produção).
//
// Princípio: se o valor fatiado do input (Inicio/Fim da spec) aparece EXATAMENTE
// em UM único XPath do output, esse campo está empiricamente ancorado — sem
// heurística, sem LLM. Valores genéricos ("0", "0.00", vazio) só ambiguam e são
// descartados; valores distintivos (CNPJ, nNF, natOp, datas) ancoram com certeza.
//
// O TXT MQSeries NÃO tem quebras de linha: registros são fatias FIXAS de
// tamanho único — 600 chars no leiaute NF-e do CLI, parametrizável via
// lineLength no Load (pos 1-6 = sequência/HEADER/999999; pos 7-9 = código do bloco).
//
// Desenho: docs/architecture/poc-excel-generator.md §3.4 (camada empírica).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Par gabarito carregado: registros por bloco + índice valor→XPaths do output.</summary>
public sealed class NfeGabarito
{
    /// <summary>Registros de tamanho fixo por bloco ("HEADER", "LINHA001", "TRAILER").</summary>
    public IReadOnlyDictionary<string, List<string>> RecordsByBlock { get; }

    /// <summary>Valor exato (texto) → XPaths de folha do output onde ele aparece.</summary>
    public IReadOnlyDictionary<string, HashSet<string>> ValueToPaths { get; }

    /// <summary>Todos os XPaths de folha (elementos sem filhos + atributos) do output.</summary>
    public IReadOnlyCollection<string> LeafPaths { get; }

    private NfeGabarito(
        Dictionary<string, List<string>> records,
        Dictionary<string, HashSet<string>> valueToPaths,
        HashSet<string> leafPaths)
    {
        RecordsByBlock = records;
        ValueToPaths = valueToPaths;
        LeafPaths = leafPaths;
    }

    /// <summary>
    /// Carrega o par. O TXT é lido em Latin-1 (padrão dos arquivos MQSeries);
    /// o XML de saída não tem namespace (enviNFe plano).
    /// </summary>
    /// <param name="lineLength">
    /// Tamanho fixo de cada registro do TXT. Default 600 (padrão MQSeries/NF-e do
    /// CLI atual). Quando o motor for integrado à API (fase C1), o valor real deve
    /// vir de <c>Layout.LimitOfCaracters</c>, resolvido via <c>LineLengthResolver</c>
    /// do núcleo — não hardcode por cliente.
    /// </param>
    public static NfeGabarito Load(string txtPath, string xmlPath, int lineLength = 600)
    {
        if (lineLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(lineLength), lineLength,
                "Tamanho de linha deve ser positivo.");

        // ── Input: fatias fixas de lineLength chars, agrupadas pelo código do bloco ──
        var raw = File.ReadAllText(txtPath, Encoding.Latin1);
        var records = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var i = 0; i + lineLength <= raw.Length; i += lineLength)
        {
            var rec = raw.Substring(i, lineLength);
            var seq = rec[..6];
            var key = seq == "HEADER" ? "HEADER"
                    : seq == "999999" ? "TRAILER"
                    : $"LINHA{rec.Substring(6, 3)}";
            if (!records.TryGetValue(key, out var list))
                records[key] = list = new List<string>();
            list.Add(rec);
        }

        // ── Output: índice valor→XPaths de folha (sem índices posicionais) ──
        var doc = XDocument.Load(xmlPath);
        var valueToPaths = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var leafPaths = new HashSet<string>(StringComparer.Ordinal);
        IndexElement(doc.Root!, doc.Root!.Name.LocalName, valueToPaths, leafPaths);

        return new NfeGabarito(records, valueToPaths, leafPaths);
    }

    /// <summary>
    /// Ancora um campo da spec: fatia o(s) registro(s) do bloco em [Inicio..Fim],
    /// gera variantes de normalização e devolve o CONJUNTO de XPaths do output com
    /// esse valor (vazio = sem match; 1 = pin forte; 2+ = sinal fraco/ambíguo).
    /// </summary>
    public IReadOnlyList<string> FindAnchorPaths(SpecField f)
    {
        if (!RecordsByBlock.TryGetValue(f.Bloco, out var recs)) return [];

        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rec in recs)
        {
            if (f.Inicio < 1 || f.Fim > rec.Length || f.Fim < f.Inicio) continue;
            var slice = rec.Substring(f.Inicio - 1, f.Fim - f.Inicio + 1);
            foreach (var variant in Variants(f, slice))
            {
                if (!IsDistinctive(variant)) continue;
                if (ValueToPaths.TryGetValue(variant, out var found))
                    paths.UnionWith(found);
            }
        }
        return paths.ToList();
    }

    // ── Normalização de valor (input posicional → forma do output) ───────────

    private static IEnumerable<string> Variants(SpecField f, string slice)
    {
        var t = slice.Trim();
        if (t.Length == 0) yield break;
        yield return t;

        if (!t.All(char.IsDigit)) yield break;

        // Numérico: sem zeros à esquerda ("000150839" → "150839").
        var stripped = t.TrimStart('0');
        if (stripped.Length == 0) stripped = "0";
        if (stripped != t) yield return stripped;

        // N com decimais: "00000000018978" (dec=2) → "189.78" (formato do output).
        if (f.Decimais is int d and > 0 && t.Length > d)
        {
            var intPart = t[..^d].TrimStart('0');
            if (intPart.Length == 0) intPart = "0";
            yield return $"{intPart}.{t[^d..]}";
        }

        // Data AAAAMMDD → AAAA-MM-DD (o formato de dhEmi com fuso não é coberto:
        // esses campos são ancorados pela calibração do arquiteto, não por valor).
        if (f.Tipo == 'D' && t.Length == 8)
            yield return $"{t[..4]}-{t[4..6]}-{t[6..]}";
    }

    /// <summary>
    /// Valor "distintivo": 2+ chars e não-monótono (descarta "", "0", "000", "111"…).
    /// A exigência de XPath ÚNICO no output faz o resto do filtro.
    /// </summary>
    private static bool IsDistinctive(string v)
    {
        if (v.Length < 2) return false;
        var first = v[0];
        return v.Any(c => c != first && c != '.' && c != ',');
    }

    private static void IndexElement(
        XElement el, string path,
        Dictionary<string, HashSet<string>> valueToPaths, HashSet<string> leafPaths)
    {
        foreach (var at in el.Attributes().Where(a => !a.IsNamespaceDeclaration))
            AddValue(valueToPaths, leafPaths, $"{path}/@{at.Name.LocalName}", at.Value);

        var children = el.Elements().ToList();
        if (children.Count == 0)
        {
            AddValue(valueToPaths, leafPaths, path, el.Value);
            return;
        }
        foreach (var child in children)
            IndexElement(child, $"{path}/{child.Name.LocalName}", valueToPaths, leafPaths);
    }

    private static void AddValue(
        Dictionary<string, HashSet<string>> valueToPaths, HashSet<string> leafPaths,
        string path, string value)
    {
        leafPaths.Add(path);
        var v = value.Trim();
        if (v.Length == 0) return;
        if (!valueToPaths.TryGetValue(v, out var set))
            valueToPaths[v] = set = new HashSet<string>(StringComparer.Ordinal);
        set.Add(path);
    }
}
