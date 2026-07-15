using System.Text;
using System.Xml.Linq;

namespace XslSynth.Excel;

// ─────────────────────────────────────────────────────────────────────────────
// RootTreeBuilder — PoC-3/A1: TXT posicional MQSeries → árvore XML ROOT.
//
// O TXT NÃO tem quebras de linha: registros são fatias FIXAS de tamanho único
// (600 chars no leiaute NF-e do CLI; parametrizável via lineLength no Build).
// HEADER/TRAILER são identificados pelos MARCADORES das posições 1-6
// ("HEADER" / seq "999999") — NUNCA pelas posições 7-9, que no HEADER/TRAILER
// contêm o ano ("202x") e gerariam blocos fantasma (o "LINHA202" da PoC-1).
//
// Cada campo é fatiado pelos Inicio/Fim ABSOLUTOS do SpecModel (autoritativos —
// cobrem as 10 descontinuidades de posição observadas em 8 blocos), NÃO pelo
// comprimento cumulativo do TCL. O TCL segue existindo como artefato de export
// para a plataforma; o ROOT daqui é o que o XSL gerado consome.
//
// Linhas físicas repetidas (4× LINHA081 = continuação de infCpl) viram
// elementos repetidos em ordem de chegada — o XSL faz o for-each/concat.
//
// Desenho: docs/architecture/poc-excel-generator.md §7.2.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Resultado da montagem do ROOT (com contagens para o gate A1).</summary>
/// <param name="Root">Documento <c>&lt;ROOT&gt;&lt;LINHA…&gt;</c> na ordem do arquivo.</param>
/// <param name="Registros">Total de fatias de tamanho fixo (<c>lineLength</c>) lidas.</param>
/// <param name="RegistrosSemBloco">Registros cujo código não existe na spec (ignorados).</param>
/// <param name="BlocosDesconhecidos">Códigos de bloco não encontrados na spec.</param>
public sealed record RootBuildReport(
    XDocument Root,
    int Registros,
    int RegistrosSemBloco,
    IReadOnlyList<string> BlocosDesconhecidos);

/// <summary>
/// Constrói a árvore ROOT a partir do TXT real + SpecModel. 100% determinístico.
/// Degrada graciosamente: bloco desconhecido é contado e ignorado, sem derrubar.
/// </summary>
public sealed class RootTreeBuilder
{
    /// <summary>
    /// Monta a árvore ROOT fatiando o TXT em registros de tamanho fixo.
    /// </summary>
    /// <param name="lineLength">
    /// Tamanho fixo de cada registro do TXT. Default 600 (padrão MQSeries/NF-e do
    /// CLI atual). Quando o motor for integrado à API (fase C1), o valor real deve
    /// vir de <c>Layout.LimitOfCaracters</c>, resolvido via <c>LineLengthResolver</c>
    /// do núcleo — não hardcode por cliente.
    /// </param>
    public RootBuildReport Build(string txtPath, SpecModel spec, Action<string>? log = null, int lineLength = 600)
    {
        if (!File.Exists(txtPath))
            throw new FileNotFoundException($"TXT de entrada não encontrado: {txtPath}", txtPath);

        if (lineLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(lineLength), lineLength,
                "Tamanho de linha deve ser positivo.");

        // Latin-1: padrão dos arquivos MQSeries (mesma leitura do NfeGabaritoMiner).
        var raw = File.ReadAllText(txtPath, Encoding.Latin1);

        var byName = spec.Blocks.ToDictionary(b => b.Name, StringComparer.Ordinal);
        var namedFields = spec.Blocks.ToDictionary(
            b => b.Name,
            b => TclGenerator.NamedFields(b),
            StringComparer.Ordinal);

        var root = new XElement("ROOT");
        var registros = 0;
        var semBloco = 0;
        var desconhecidos = new List<string>();

        for (var i = 0; i + lineLength <= raw.Length; i += lineLength)
        {
            var rec = raw.Substring(i, lineLength);
            registros++;

            // Marcadores nas posições 1-6; pos 7-9 SÓ para as linhas de dados.
            var seq = rec[..6];
            var key = seq == "HEADER" ? "HEADER"
                    : seq == "999999" ? "TRAILER"
                    : $"LINHA{rec.Substring(6, 3)}";

            if (!byName.TryGetValue(key, out _))
            {
                semBloco++;
                if (!desconhecidos.Contains(key)) desconhecidos.Add(key);
                log?.Invoke($"   [aviso] registro {registros}: bloco '{key}' não existe na spec — ignorado.");
                continue;
            }

            var line = new XElement(key);
            foreach (var (f, name) in namedFields[key])
            {
                // Fatia ABSOLUTA [Inicio..Fim]; fora do range → campo vazio (honesto).
                var value = f.Inicio >= 1 && f.Fim >= f.Inicio && f.Fim <= rec.Length
                    ? rec.Substring(f.Inicio - 1, f.Fim - f.Inicio + 1)
                    : "";
                line.Add(new XElement(name, Sanitize(value)));
            }
            root.Add(line);
        }

        return new RootBuildReport(
            new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root),
            registros, semBloco, desconhecidos);
    }

    /// <summary>
    /// Troca chars de controle inválidos em XML 1.0 por espaço (arquivos MQ podem
    /// trazer lixo binário no filler) — preserva o comprimento da fatia.
    /// </summary>
    private static string Sanitize(string s)
    {
        if (s.All(c => c >= ' ' || c is '\t')) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(c >= ' ' || c is '\t' ? c : ' ');
        return sb.ToString();
    }
}
