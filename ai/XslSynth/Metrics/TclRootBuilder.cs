using System.Text;
using System.Xml.Linq;

namespace XslSynth.Metrics;

/// <summary>Uma LINE do <c>&lt;MAP&gt;</c> TCL: identificador de registro + campos + filhos declarados.</summary>
internal sealed record TclLine(string Identifier, string Name, IReadOnlyList<TclField> Fields, IReadOnlyList<string> Children);

/// <summary>Um FIELD do TCL. <c>Length</c> é null nos mapas delimitados (com separator).</summary>
internal sealed record TclField(string Name, int? Length);

/// <summary>
/// Schema posicional TCL (<c>&lt;MAP&gt;</c>) parseado — a ENTRADA do par do dataset.
/// Note que este é o schema, não a instância: o dataset é <c>TCL(schema) → XSLT</c> e
/// nenhum documento real acompanha o par (ver §A2/§A3 do handoff do Job 2).
/// </summary>
internal sealed class TclLayoutMap
{
    public string? Separator { get; init; }
    public IReadOnlyList<TclLine> Lines { get; init; } = [];

    /// <summary>Declara hierarquia via <c>&lt;CHILD&gt;</c>? Quando não, o ROOT sai PLANO —
    /// e aí os XPaths aninhados do XSLT (ex.: <c>ROOT/Cabecalho/Det</c>) podem não resolver.
    /// Isso não é adivinhado: o gate de saída (<see cref="TclRootBuilder"/>) mede o resultado.</summary>
    public bool HasDeclaredHierarchy => Lines.Any(l => l.Children.Count > 0);

    public static TclLayoutMap? TryParse(string tclText)
    {
        XDocument doc;
        try { doc = XDocument.Parse(tclText); }
        catch { return null; }

        var map = doc.Root;
        if (map is null || map.Name.LocalName != "MAP") return null;

        var sep = (string?)map.Attribute("separator");
        var lines = new List<TclLine>();
        foreach (var line in map.Elements("LINE"))
        {
            var ident = (string?)line.Attribute("identifier") ?? "";
            var nome = (string?)line.Attribute("name") ?? "";
            if (ident.Length == 0 || nome.Length == 0) continue;

            // O identificador declarado às vezes já embute o separador (ex.: "A01|").
            if (!string.IsNullOrEmpty(sep) && ident.EndsWith(sep, StringComparison.Ordinal))
                ident = ident[..^sep.Length];

            var campos = line.Elements("FIELD")
                .Select(f => new TclField(
                    (string?)f.Attribute("name") ?? "",
                    int.TryParse((string?)f.Attribute("length"), out var len) ? len : null))
                .Where(f => f.Name.Length > 0)
                .ToList();

            var filhos = line.Elements("CHILD").Select(c => c.Value.Trim())
                .Where(v => v.Length > 0).ToList();

            lines.Add(new TclLine(ident, nome, campos, filhos));
        }

        return lines.Count == 0 ? null : new TclLayoutMap { Separator = sep, Lines = lines };
    }
}

/// <summary>Resultado da tentativa de montar o ROOT a partir de (TXT de instância + schema TCL).</summary>
internal sealed record RootBuildOutcome(XDocument? Root, string? Motivo, double TaxaCasamento, int Registros);

/// <summary>
/// Elo faltante do §A2 do handoff: <c>TXT de instância + schema TCL → ROOT.xml</c>, que é
/// o documento de entrada esperado pelo XSLT gerado (<c>ROOT/chave/chNFe</c>,
/// <c>ROOT/Cabecalho/cUF</c>…).
///
/// Diferente de <see cref="XslSynth.Excel.RootTreeBuilder"/> (que monta o ROOT a partir da
/// planilha de spec e de registros MQSeries de 600 chars), aqui a fonte da estrutura é o
/// PRÓPRIO TCL do par — é o único jeito de o ROOT casar com os XPaths do XSLT do dataset.
///
/// PRINCÍPIO: este builder RECUSA o que não casa em vez de produzir um ROOT vazio que
/// depois viraria uma NF-e vazia "aprovada" pelo pipeline. A taxa de casamento entre as
/// linhas do TXT e os identificadores do TCL é medida e reportada; abaixo do limite, devolve
/// motivo e nenhum ROOT.
/// </summary>
internal static class TclRootBuilder
{
    /// <summary>Fração mínima de linhas do TXT que precisa casar com algum identificador do
    /// TCL para considerarmos que instância e schema são do mesmo layout.</summary>
    private const double TaxaMinimaCasamento = 0.90;

    /// <summary>Quantidade mínima de tipos de registro DISTINTOS casados. Identificador de
    /// uma letra casa por acidente com quase qualquer texto (um "H" casa com "HEADER…"), e
    /// esse acidente já produziu, num teste, uma NF-e lixo classificada como elegível. Um
    /// layout real de emissão sempre usa vários registros — exigir diversidade mata o falso
    /// casamento sem rejeitar instância legítima.</summary>
    private const int MinimoDeTiposDistintos = 2;

    public static RootBuildOutcome TryBuild(string txtPath, TclLayoutMap map)
    {
        string bruto;
        try { bruto = File.ReadAllText(txtPath, Encoding.Latin1); }
        catch (Exception ex) { return new RootBuildOutcome(null, $"TXT ilegível: {ex.Message}", 0, 0); }

        var linhas = bruto.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.Trim().Length > 0).ToList();
        if (linhas.Count == 0)
            return new RootBuildOutcome(null, "TXT vazio", 0, 0);

        // Identificadores do mais longo para o mais curto: "G01" tem de vencer "G".
        var porTamanho = map.Lines.OrderByDescending(l => l.Identifier.Length).ToList();

        var casadas = linhas.Count(l => Casa(l, porTamanho, map.Separator) is not null);
        var taxa = (double)casadas / linhas.Count;
        if (taxa < TaxaMinimaCasamento)
        {
            var amostra = linhas[0].Length > 24 ? linhas[0][..24] : linhas[0];
            var motivo = linhas.Count == 1
                ? $"instância NÃO casa com o schema TCL: arquivo de linha única ({bruto.Length} chars, formato "
                  + $"MQSeries de registros concatenados) e o TCL espera registros por linha com identificadores "
                  + $"[{string.Join(',', map.Lines.Take(5).Select(l => l.Identifier))}…]"
                : $"instância NÃO casa com o schema TCL: só {taxa:P0} das {linhas.Count} linhas começam com um "
                  + $"identificador declarado, no tamanho de registro previsto (1ª linha: '{amostra}…')";
            return new RootBuildOutcome(null, motivo, taxa, linhas.Count);
        }

        var distintos = linhas.Select(l => Casa(l, porTamanho, map.Separator)?.Identifier)
            .Where(i => i is not null).Distinct(StringComparer.Ordinal).Count();
        if (map.Lines.Count >= MinimoDeTiposDistintos && distintos < MinimoDeTiposDistintos)
            return new RootBuildOutcome(null,
                $"instância NÃO casa com o schema TCL: só {distintos} tipo(s) de registro distinto(s) casaram "
                + $"num TCL de {map.Lines.Count} — casamento acidental de prefixo, não layout compatível",
                taxa, linhas.Count);

        var root = new XElement("ROOT");
        // Pilha de elementos abertos, usada só quando o TCL declara <CHILD> — sem declaração
        // o ROOT sai plano (e o gate de saída decide se isso serviu).
        var pilha = new List<(TclLine Line, XElement El)>();

        foreach (var linha in linhas)
        {
            var tcl = Casa(linha, porTamanho, map.Separator);
            if (tcl is null) continue;

            var el = new XElement(SafeName(tcl.Name));
            PreencheCampos(el, linha, tcl, map.Separator);

            // Anexa sob o ancestral mais próximo que DECLARA este nome como filho.
            var idx = pilha.FindLastIndex(p => p.Line.Children.Contains(tcl.Name, StringComparer.Ordinal));
            if (idx >= 0)
            {
                pilha[idx].El.Add(el);
                pilha.RemoveRange(idx + 1, pilha.Count - idx - 1);
            }
            else
            {
                root.Add(el);
                pilha.Clear();
            }
            pilha.Add((tcl, el));
        }

        return new RootBuildOutcome(new XDocument(root), null, taxa, linhas.Count);
    }

    /// <summary>
    /// Casa uma linha com um registro do TCL: prefixo do identificador E tamanho plausível.
    /// A checagem de tamanho é o que impede um identificador curto de sequestrar uma linha
    /// que não é dele — num mapa posicional o registro tem tamanho conhecido
    /// (identificador + soma dos lengths), então uma linha MAIOR que isso não é esse registro.
    /// Menor é aceito: TXT de produção costuma vir com o padding à direita cortado.
    /// </summary>
    private static TclLine? Casa(string linha, IReadOnlyList<TclLine> porTamanho, string? separator)
    {
        foreach (var tcl in porTamanho)
        {
            if (!linha.StartsWith(tcl.Identifier, StringComparison.Ordinal)) continue;
            if (!string.IsNullOrEmpty(separator)) return tcl;   // delimitado: sem tamanho fixo

            var esperado = tcl.Identifier.Length + tcl.Fields.Sum(f => f.Length ?? 0);
            if (esperado <= tcl.Identifier.Length) return tcl;  // sem length declarado: não dá para checar
            if (linha.TrimEnd().Length <= esperado) return tcl;
        }
        return null;
    }

    private static void PreencheCampos(XElement el, string linha, TclLine tcl, string? separator)
    {
        if (!string.IsNullOrEmpty(separator))
        {
            // Mapa delimitado: o 1º token é o identificador, os demais seguem a ordem dos FIELD.
            var partes = linha.Split(separator);
            for (var i = 0; i < tcl.Fields.Count; i++)
            {
                var valor = i + 1 < partes.Length ? partes[i + 1] : "";
                el.Add(new XElement(SafeName(tcl.Fields[i].Name), valor.Trim()));
            }
            return;
        }

        // Mapa posicional: os campos começam logo após o identificador.
        var pos = tcl.Identifier.Length;
        foreach (var campo in tcl.Fields)
        {
            var len = campo.Length ?? 0;
            var valor = "";
            if (len > 0 && pos < linha.Length)
                valor = linha.Substring(pos, Math.Min(len, linha.Length - pos));
            pos += len;
            el.Add(new XElement(SafeName(campo.Name), valor.Trim()));
        }
    }

    /// <summary>Nomes vindos do TCL de produção podem conter caracteres inválidos para XML
    /// (ex.: espaço). Sanea sem alterar o caso comum — nome inválido derrubaria o ROOT inteiro.</summary>
    private static string SafeName(string nome)
    {
        var sb = new StringBuilder(nome.Length);
        foreach (var c in nome)
            sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
        if (sb.Length == 0 || !(char.IsLetter(sb[0]) || sb[0] == '_')) sb.Insert(0, '_');
        return sb.ToString();
    }
}
