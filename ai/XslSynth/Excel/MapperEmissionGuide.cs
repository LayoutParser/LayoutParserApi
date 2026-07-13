using System.Text.RegularExpressions;
using XslSynth.Core;

namespace XslSynth.Excel;

/// <summary>
/// Etapa B: guia de emissão extraído do MAPEADOR REAL (MapperVO descriptografado).
///
/// As regras DSL do Sysmiddle carregam guardas que a spec Excel e o XSD NÃO
/// expressam — ex.: retTrib e cobr/fat/vLiq só são emitidos com valor != 0:
///
///   if(IsNullOrEmpty(#.vRetPIS) != True()) begin
///       #.valorRetidoPis = #.vRetPIS;
///       if(#.valorRetidoPis != 0) begin                ← a guarda que o gabarito segue
///           T.enviNFe/NFe/infNFe/total/retTrib/vRetPIS = FormaterDecimal(...,2);
///       end
///   end
///
/// Este guia varre as 98 regras e cataloga, por T.path, quais destinos têm a
/// guarda "!= 0". O XslGenerator então troca o teste desses destinos de
/// "não-vazio" para "valor &gt; 0" — SEGURO POR CONSTRUÇÃO: só remove zeros que o
/// mapeador também removeria; nunca afrouxa um teste.
///
/// Degrade gracioso: sem o MapperVO no disco, o guia é vazio (comportamento atual).
/// </summary>
public sealed class MapperEmissionGuide
{
    // Bloco interno guardado: if(#.x != 0) begin … end  (os corpos reais não aninham
    // outro begin/end dentro — o lazy .*? para no primeiro 'end').
    // \w e não [A-Za-z0-9_]: as variáveis da DSL têm acentos REAIS no mapeador
    // (#.valorRetençãoPrevidencia) e \w cobre Unicode em .NET.
    private static readonly Regex NonZeroBlock = new(
        @"if\s*\(\s*#\.\w+\s*!=\s*0\s*\)\s*begin\s*(?<corpo>.*?)\s*end",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex TargetPath = new(
        @"T\.(?<path>[A-Za-z0-9_/]+)\s*=", RegexOptions.Compiled);

    private readonly HashSet<string> _paths;                          // path completo
    private readonly Dictionary<string, List<string>> _folhasPorPai;  // pai → folhas (typos)

    private MapperEmissionGuide(HashSet<string> paths, Dictionary<string, List<string>> folhasPorPai)
    {
        _paths = paths;
        _folhasPorPai = folhasPorPai;
    }

    public static MapperEmissionGuide Empty { get; } =
        new(new(StringComparer.Ordinal), new(StringComparer.Ordinal));

    public int PathCount => _paths.Count;

    /// <summary>Carrega o MapperVO real e extrai os destinos com guarda != 0.</summary>
    public static MapperEmissionGuide Load(string mapperVoPath)
    {
        var mapper = new RealMapperParser().ParseFile(mapperVoPath);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var porPai = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var rule in mapper.Rules)
        {
            foreach (Match bloco in NonZeroBlock.Matches(rule.ContentValue ?? ""))
            {
                foreach (Match t in TargetPath.Matches(bloco.Groups["corpo"].Value))
                {
                    var p = t.Groups["path"].Value.Trim();
                    if (p.Length == 0) continue;
                    paths.Add(p);
                    var corte = p.LastIndexOf('/');
                    if (corte <= 0) continue;
                    var (pai, folha) = (p[..corte], p[(corte + 1)..]);
                    if (!porPai.TryGetValue(pai, out var folhas))
                        porPai[pai] = folhas = new List<string>();
                    folhas.Add(folha);
                }
            }
        }
        return new MapperEmissionGuide(paths, porPai);
    }

    /// <summary>
    /// O mapeador só emite este destino quando o valor é != 0?
    /// Match exato por path; fallback pela folha SOB O MESMO PAI com tolerância de
    /// 1 char (typo real do mapeador: <c>vBCIRRFL</c> vs XSD <c>vBCIRRF</c>).
    /// </summary>
    public bool ExigeNaoZero(string xpath)
    {
        if (_paths.Contains(xpath)) return true;

        var corte = xpath.LastIndexOf('/');
        if (corte <= 0) return false;
        var (pai, folha) = (xpath[..corte], xpath[(corte + 1)..]);
        if (!_folhasPorPai.TryGetValue(pai, out var folhas)) return false;

        foreach (var f in folhas)
        {
            if (Math.Abs(f.Length - folha.Length) > 1) continue;
            if (f.StartsWith(folha, StringComparison.Ordinal)
                || folha.StartsWith(f, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
