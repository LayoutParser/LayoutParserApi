using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace XslSynth.Excel;

// ─────────────────────────────────────────────────────────────────────────────
// ExcelSpecParser — lê a spec .xlsx (DocumentFormat.OpenXml) → SpecModel.
//
// Estrutura REAL da aba "Layout-Emissão-XML-4.00" (confirmada lendo o arquivo):
//   • Títulos de SEÇÃO ("Registro Header", "Registro de Dados",
//     "Registro para Trailer Final", "Legenda") ficam na COLUNA A (col B vazia).
//   • Cabeçalhos de BLOCO ("Bloco-000 - …") ficam na COLUNA B (col A = nº do item).
//     Atenção: o separador é inconsistente na origem — há "Bloco-020" (hífen) e
//     "Bloco 021" (espaço). Notas de reserva ("Bloco 010 a 019 Reservado…") NÃO
//     têm posições (E/F/G vazias) e por isso não são tratadas como bloco.
//   • Linhas de CAMPO têm col A numérica + E/F/G numéricos; o nome do campo vem da
//     col C (blocos de dados) ou col B (campos do HEADER).
//   • Cada "Bloco-NNN" é a 1ª FIELD do seu bloco (o código do registro, posições 7-9);
//     as posições RESETAM a cada bloco (Bloco-001 recomeça em Inicio=10).
//   • O "Tipo Registro" (posições 1-6) é listado UMA vez em "Registro de Dados"
//     (antes do 1º Bloco). Como toda linha física tem esses 6 chars, ele é
//     REPLICADO no início de cada LINHANNN para dar cobertura total de 1-600.
//
// Desenho: docs/architecture/poc-excel-generator.md §2/§3.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Faz o parse da spec Excel (aba de Emissão) para um <see cref="SpecModel"/>.
/// Degrada graciosamente: célula malformada é logada e ignorada, sem derrubar o
/// parse inteiro. Arquivo/aba ausente → exceção clara.
/// </summary>
public sealed class ExcelSpecParser
{
    private readonly Action<string>? _log;

    // Casa "Bloco-020" e "Bloco 021" (hífen ou espaço), captura o número.
    private static readonly Regex BlocoHeader = new(@"^Bloco[-\s]0*(\d+)", RegexOptions.Compiled);

    // Títulos de seção (col A) → como tratar.
    private const string SecHeader = "Registro Header";
    private const string SecDados = "Registro de Dados";
    private const string SecTrailer = "Registro para Trailer Final";

    public ExcelSpecParser(Action<string>? log = null) => _log = log;

    /// <summary>
    /// Lê a aba <paramref name="sheetName"/> do arquivo <paramref name="xlsxPath"/>.
    /// </summary>
    public SpecModel Parse(string xlsxPath, string sheetName = "Layout-Emissão-XML-4.00")
    {
        if (!File.Exists(xlsxPath))
            throw new FileNotFoundException($"Planilha da spec não encontrada: {xlsxPath}", xlsxPath);

        using var doc = SpreadsheetDocument.Open(xlsxPath, false);
        var wbPart = doc.WorkbookPart
            ?? throw new InvalidOperationException("Arquivo .xlsx sem WorkbookPart (corrompido?).");

        var sheet = wbPart.Workbook.Descendants<Sheet>()
            .FirstOrDefault(s => string.Equals(s.Name?.Value, sheetName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Aba '{sheetName}' não encontrada. Abas disponíveis: "
                + string.Join(", ", wbPart.Workbook.Descendants<Sheet>().Select(s => $"'{s.Name?.Value}'")));

        var wsPart = (WorksheetPart)wbPart.GetPartById(sheet.Id!.Value!);
        var sheetData = wsPart.Worksheet.GetFirstChild<SheetData>()
            ?? throw new InvalidOperationException($"Aba '{sheetName}' sem SheetData.");

        // Materializa a SharedStringTable uma vez (indexação O(1) por célula).
        var shared = wbPart.SharedStringTablePart?.SharedStringTable
            .Elements<SharedStringItem>().Select(i => i.InnerText).ToArray()
            ?? Array.Empty<string>();

        var blocks = new List<BlockBuilder>();
        BlockBuilder? cur = null;
        var prefix = new List<SpecField>();   // "Tipo Registro" compartilhado (Registro de Dados)
        var inDados = false;
        var skipped = 0;

        foreach (var row in sheetData.Elements<Row>())
        {
            Dictionary<string, string> c;
            try
            {
                c = ReadRow(row, shared);
            }
            catch (Exception ex)
            {
                skipped++;
                _log?.Invoke($"   [aviso] linha {row.RowIndex?.Value} ilegível ({ex.Message}) — ignorada.");
                continue;
            }

            var colA = Get(c, "A");
            var colB = Get(c, "B");
            var colC = Get(c, "C");

            // Cabeçalho da tabela repetido por seção ("Item | Descrição | …").
            if (colA == "Item") continue;

            // ── Títulos de SEÇÃO (col A) ──────────────────────────────────────
            switch (colA)
            {
                case SecHeader:
                    cur = new BlockBuilder("HEADER", -1);
                    blocks.Add(cur);
                    inDados = false;
                    continue;
                case SecDados:
                    inDados = true;
                    cur = null;         // os campos até o 1º Bloco são o prefixo compartilhado
                    continue;
                case SecTrailer:
                    cur = new BlockBuilder("TRAILER", -2);
                    blocks.Add(cur);
                    inDados = false;
                    continue;
            }

            // ── Cabeçalho de BLOCO (col B: "Bloco-NNN …") + posições presentes ──
            var m = BlocoHeader.Match(colB);
            if (m.Success && TryInt(Get(c, "E"), out _) && TryInt(Get(c, "G"), out _))
            {
                var code = int.Parse(m.Groups[1].Value);
                cur = new BlockBuilder($"LINHA{code:D3}", code);
                blocks.Add(cur);
                inDados = false;
                // O próprio cabeçalho é a 1ª FIELD do bloco: o código do registro (7-9).
                if (TryField(c, cur.Name, out var codeField))
                    cur.Fields.Add(codeField);
                continue;
            }

            // ── Linha de CAMPO ────────────────────────────────────────────────
            if (TryInt(colA, out _) && TryField(c, cur?.Name ?? "?", out var field))
            {
                if (inDados && cur is null)
                    prefix.Add(field with { Bloco = "PREFIX" });   // Tipo Registro compartilhado
                else if (cur is not null)
                    cur.Fields.Add(field);
                else
                    skipped++;   // campo órfão (fora de bloco) — raro
                continue;
            }

            // Demais linhas (reserva, legenda, notas, vazias) — ignoradas em silêncio.
        }

        // Replica o "Tipo Registro" (1-6) no início de cada LINHANNN que não começa
        // em 1 — dá a cobertura total de 600 chars por linha que o parser TCL precisa.
        var finalBlocks = new List<SpecBlock>(blocks.Count);
        foreach (var b in blocks)
        {
            var fields = b.Fields;
            var needsPrefix = b.LineCode >= 0
                && prefix.Count > 0
                && !fields.Any(f => f.Inicio == 1);

            if (needsPrefix)
            {
                var withPrefix = new List<SpecField>(prefix.Count + fields.Count);
                withPrefix.AddRange(prefix.Select(p => p with { Bloco = b.Name }));
                withPrefix.AddRange(fields);
                fields = withPrefix;
            }

            finalBlocks.Add(new SpecBlock(b.Name, b.LineCode, fields));
        }

        if (skipped > 0)
            _log?.Invoke($"   [info] {skipped} linha(s) ignoradas/ilegíveis durante o parse.");

        return new SpecModel(sheetName, finalBlocks);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Monta uma FIELD a partir da linha (nome = col C, senão col B).</summary>
    private static bool TryField(Dictionary<string, string> c, string bloco, out SpecField field)
    {
        field = default!;
        if (!TryInt(Get(c, "E"), out var ini)) return false;
        if (!TryInt(Get(c, "G"), out var tam)) return false;
        TryInt(Get(c, "F"), out var fim);
        TryInt(Get(c, "A"), out var item);

        var nameRaw = Get(c, "C");
        if (string.IsNullOrEmpty(nameRaw)) nameRaw = Get(c, "B");
        // Algumas células trazem um exemplo em linhas extras (ex.: "Chave-Acesso\n12 1234…");
        // o nome do campo é só a 1ª linha.
        var name = FirstLine(nameRaw);

        var xmlRaw = Get(c, "D");
        var xmlRef = string.IsNullOrWhiteSpace(xmlRaw)
            || xmlRaw.Equals("NA", StringComparison.OrdinalIgnoreCase) ? null : xmlRaw;

        var tipoRaw = Get(c, "H");
        var tipo = tipoRaw.Length > 0 ? char.ToUpperInvariant(tipoRaw[0]) : '?';

        int? dec = TryInt(Get(c, "I"), out var d) ? d : null;
        var fmtRaw = Get(c, "J");
        var fmt = string.IsNullOrWhiteSpace(fmtRaw) ? null : fmtRaw;

        field = new SpecField(bloco, item, name, xmlRef, ini, fim, tam, tipo, dec, fmt);
        return true;
    }

    /// <summary>Lê uma Row para um mapa coluna→texto (A..J), resolvendo shared strings.</summary>
    private static Dictionary<string, string> ReadRow(Row row, string[] shared)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var cell in row.Elements<Cell>())
        {
            var col = ColumnLetters(cell.CellReference?.Value);
            if (col is null) continue;
            map[col] = CellText(cell, shared).Trim();
        }
        return map;
    }

    /// <summary>Texto de uma célula (resolve SharedString / InlineString / valor direto).</summary>
    private static string CellText(Cell cell, string[] shared)
    {
        var raw = cell.CellValue?.InnerText ?? "";
        if (cell.DataType?.Value == CellValues.SharedString)
        {
            return int.TryParse(raw, out var idx) && idx >= 0 && idx < shared.Length
                ? shared[idx]
                : "";
        }
        if (cell.DataType?.Value == CellValues.InlineString)
            return cell.InlineString?.Text?.Text ?? cell.InnerText;
        return raw;
    }

    /// <summary>Extrai as letras da referência da célula ("B17" → "B").</summary>
    private static string? ColumnLetters(string? cellRef)
    {
        if (string.IsNullOrEmpty(cellRef)) return null;
        var i = 0;
        while (i < cellRef.Length && char.IsLetter(cellRef[i])) i++;
        return i > 0 ? cellRef[..i] : null;
    }

    private static string Get(Dictionary<string, string> c, string col) =>
        c.TryGetValue(col, out var v) ? v : "";

    /// <summary>1ª linha não-vazia de um texto multi-linha (ou null se em branco).</summary>
    private static string? FirstLine(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var idx = s.IndexOfAny(['\n', '\r']);
        var first = (idx >= 0 ? s[..idx] : s).Trim();
        return first.Length == 0 ? null : first;
    }

    private static bool TryInt(string s, out int value) =>
        int.TryParse(s, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out value);

    /// <summary>Builder mutável interno; congelado em <see cref="SpecBlock"/> no fim.</summary>
    private sealed class BlockBuilder(string name, int lineCode)
    {
        public string Name { get; } = name;
        public int LineCode { get; } = lineCode;
        public List<SpecField> Fields { get; } = new();
    }
}
