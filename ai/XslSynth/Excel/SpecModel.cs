namespace XslSynth.Excel;

// ─────────────────────────────────────────────────────────────────────────────
// Contratos de dados da spec Excel (aba "Layout-Emissão-XML-4.00").
//
// A planilha é a FONTE-DA-VERDADE do layout posicional NF-e: descreve, campo a
// campo, a posição no TXT (Inicio/Fim/Tamanho) e o destino no leiaute NF-e (#XML).
// Estes records são o modelo intermediário consumido pelo TclGenerator (e, depois,
// pelo XslGenerator/Catálogo — PoC-2/3, da Lia).
//
// Desenho: docs/architecture/poc-excel-generator.md §3.2
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Um campo posicional de um bloco (uma linha de dados da planilha).</summary>
/// <param name="Bloco">Nome do bloco dono (ex.: "LINHA001", "HEADER").</param>
/// <param name="Item">Nº sequencial do campo no bloco (col A).</param>
/// <param name="FieldName">Nome do campo (col C; ou col B no HEADER). Pode ser nulo.</param>
/// <param name="XmlRef">Nº do leiaute NF-e (col D: "6", "79a", "BB01"…) ou nulo quando "NA".</param>
/// <param name="Inicio">Posição inicial 1-based dentro da linha do bloco (col E).</param>
/// <param name="Fim">Posição final (col F).</param>
/// <param name="Tamanho">Comprimento em chars (col G) — alimenta o length do FIELD.</param>
/// <param name="Tipo">'C' char · 'N' numérico · 'D' data (col H).</param>
/// <param name="Decimais">Casas decimais quando N monetário (col I) ou nulo.</param>
/// <param name="Formato">Máscara: "AAAAMMDD", "AAAA-MM-DD", literais… (col J) ou nulo.</param>
public sealed record SpecField(
    string Bloco,
    int Item,
    string? FieldName,
    string? XmlRef,
    int Inicio,
    int Fim,
    int Tamanho,
    char Tipo,
    int? Decimais,
    string? Formato);

/// <summary>
/// Um bloco = uma LINHA posicional do documento (600 chars). Cada "Bloco-NNN" da
/// planilha vira <c>LINHANNN</c>; "Registro Header" vira <c>HEADER</c> e
/// "Registro para Trailer Final" vira <c>TRAILER</c>.
/// </summary>
/// <param name="Name">Nome canônico da linha ("HEADER", "LINHA001", "TRAILER").</param>
/// <param name="LineCode">Código numérico do bloco (0, 1, 20…). HEADER=-1, TRAILER=-2.</param>
/// <param name="Fields">Campos do bloco, em ordem posicional (cobertura total da linha).</param>
public sealed record SpecBlock(string Name, int LineCode, IReadOnlyList<SpecField> Fields);

/// <summary>Modelo completo da spec: os blocos de uma aba da planilha.</summary>
/// <param name="SheetName">Nome da aba lida (ex.: "Layout-Emissão-XML-4.00").</param>
/// <param name="Blocks">Blocos na ordem em que aparecem na planilha.</param>
public sealed record SpecModel(string SheetName, IReadOnlyList<SpecBlock> Blocks);
