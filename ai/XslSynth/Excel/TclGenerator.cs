using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace XslSynth.Excel;

// ─────────────────────────────────────────────────────────────────────────────
// TclGenerator — SpecModel → TCL <MAP> (parser posicional determinístico).
//
// O TCL descreve como quebrar cada LINHA física (600 chars) em campos:
//   <MAP>
//     <LINE identifier="000" name="LINHA000">
//       <FIELD name="TipoRegistro" length="6"/>
//       <FIELD name="codigoRegistro" length="3"/>
//       <FIELD name="ControleDaVersaoDoArquivo" length="3"/>
//       …
//     </LINE>
//   </MAP>
//
// 100% determinístico, sem catálogo nem LLM. A saída é a árvore ROOT que o XSL
// (PoC-3, da Lia) vai consumir. Cobertura TOTAL da linha: TODO campo vira FIELD
// (inclusive Tipo Registro, código do bloco, Filler e campos #XML=NA), pois o
// parser precisa consumir os 600 chars.
//
// Desenho: docs/architecture/poc-excel-generator.md §3.3.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Transpila um <see cref="SpecModel"/> em um TCL <c>&lt;MAP&gt;</c> bem-formado.</summary>
public sealed class TclGenerator
{
    private static readonly Regex CodigoRegistro = new(@"^Bloco[-\s]\d+", RegexOptions.Compiled);

    public XDocument Generate(SpecModel spec)
    {
        var map = new XElement("MAP");

        foreach (var block in spec.Blocks)
        {
            var line = new XElement("LINE",
                new XAttribute("identifier", Identifier(block)),
                new XAttribute("name", block.Name));

            // Nomes únicos por LINE (o ROOT precisa de campos endereçáveis pelo XSL).
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in block.Fields)
            {
                var name = Unique(FieldName(f), used);
                line.Add(new XElement("FIELD",
                    new XAttribute("name", name),
                    new XAttribute("length", f.Tamanho.ToString(CultureInfo.InvariantCulture))));
            }

            map.Add(line);
        }

        return new XDocument(new XDeclaration("1.0", "utf-8", "yes"), map);
    }

    // O identifier é o discriminador da linha física: código do bloco (7-9) para
    // as LINHANNN; o próprio nome para HEADER/TRAILER.
    private static string Identifier(SpecBlock block) =>
        block.LineCode >= 0 ? block.LineCode.ToString("D3", CultureInfo.InvariantCulture) : block.Name;

    // Nome do FIELD: o cabeçalho de bloco (col B "Bloco-NNN …") vira "codigoRegistro";
    // os demais viram um slug NCName do nome do campo.
    private static string FieldName(SpecField f)
    {
        if (f.FieldName is { } n && CodigoRegistro.IsMatch(n)) return "codigoRegistro";
        return Slug(f.FieldName);
    }

    /// <summary>
    /// Sanitiza para NCName válido (padrão de LinkMappingTranspiler/CoverageValidator),
    /// com dobra de acentos antes para nomes legíveis ("Descrição" → "Descricao").
    /// </summary>
    private static string Slug(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "campo";
        var folded = FoldDiacritics(raw.Trim());
        var s = Regex.Replace(folded, "[^A-Za-z0-9]+", "_").Trim('_');
        if (s.Length == 0) return "campo";
        if (!char.IsLetter(s[0]) && s[0] != '_') s = "_" + s;
        return s;
    }

    /// <summary>
    /// Remove diacríticos (ç, ã, ó…) preservando as letras-base. Usa um mapa
    /// explícito (não <c>String.Normalize</c>) porque o projeto roda com
    /// <c>InvariantGlobalization=true</c>, onde a normalização Unicode vira no-op.
    /// </summary>
    private static string FoldDiacritics(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s) sb.Append(Fold(ch));
        return sb.ToString();
    }

    private static char Fold(char c) => c switch
    {
        'á' or 'à' or 'â' or 'ã' or 'ä' or 'å' => 'a',
        'Á' or 'À' or 'Â' or 'Ã' or 'Ä' or 'Å' => 'A',
        'é' or 'è' or 'ê' or 'ë' => 'e',
        'É' or 'È' or 'Ê' or 'Ë' => 'E',
        'í' or 'ì' or 'î' or 'ï' => 'i',
        'Í' or 'Ì' or 'Î' or 'Ï' => 'I',
        'ó' or 'ò' or 'ô' or 'õ' or 'ö' => 'o',
        'Ó' or 'Ò' or 'Ô' or 'Õ' or 'Ö' => 'O',
        'ú' or 'ù' or 'û' or 'ü' => 'u',
        'Ú' or 'Ù' or 'Û' or 'Ü' => 'U',
        'ç' => 'c', 'Ç' => 'C',
        'ñ' => 'n', 'Ñ' => 'N',
        _ => c
    };

    /// <summary>Garante nome único no LINE (Filler, Filler_2, …).</summary>
    private static string Unique(string baseName, HashSet<string> used)
    {
        if (used.Add(baseName)) return baseName;
        for (var i = 2; ; i++)
        {
            var candidate = $"{baseName}_{i}";
            if (used.Add(candidate)) return candidate;
        }
    }
}
