using System.Text;
using System.Xml.Linq;

using LayoutParserApi.Models.Entities;

namespace LayoutParserApi.Services.Transformation.Ai
{
    /// <summary>
    /// Elo faltante documentado em
    /// <c>docs/architecture/decisao-pendente-input-xml-repairorchestrator-2026-08-29.md</c>:
    /// <c>ParsedField (parser posicional REAL da API) → XDocument ROOT</c>, no mesmo "dialeto" que
    /// <c>ai/XslSynth/Excel/RootTreeBuilder.cs</c> (spec Excel) e
    /// <c>ai/XslSynth/Metrics/TclRootBuilder.cs</c> (schema TCL avulso) já produzem — um
    /// <c>&lt;ROOT&gt;&lt;LineName&gt;&lt;FieldName&gt;valor&lt;/FieldName&gt;…&lt;/LineName&gt;&lt;/ROOT&gt;</c>
    /// plano, na ordem física do documento.
    ///
    /// <para><b>Por que aqui, e não em <c>ai/XslSynth.Core</c>:</b> a fonte é
    /// <see cref="ParsedField"/>, um modelo de domínio da API (<c>LayoutParserApi.Models.Entities</c>).
    /// Colocar este conversor na classlib compartilhada acoplaria <c>XslSynth.Core</c> ao domínio da
    /// API, quebrando o isolamento que o CLI standalone (<c>ai/XslSynth/Program.cs</c>) precisa manter
    /// (Opção 2 do desenho, descartada). Aqui, no lado da API, o conversor faz a ponte SEM tocar em
    /// nada de <c>ai/XslSynth.Core</c> — só produz o <see cref="XDocument"/> que o
    /// <c>RepairOrchestrator</c> já espera como parâmetro <c>input</c>.</para>
    ///
    /// <para><b>Gate de qualidade (mesmo espírito do <c>TclRootBuilder</c>):</b> recusa produzir um
    /// ROOT de fachada quando o parse posicional claramente não bateu com o layout — evita que um
    /// XML vazio/lixo convirja trivialmente contra o gabarito e mascare a métrica de sucesso do
    /// motor (mesmo princípio citado no header do <c>TclRootBuilder</c>).</para>
    /// </summary>
    public static class ParsedFieldRootTreeBuilder
    {
        /// <summary>Fração mínima de campos físicos com valor não vazio. Abaixo disso, o parse
        /// posicional provavelmente não é compatível com o layout informado — devolver esse ROOT ao
        /// RepairOrchestrator só produziria uma síntese sobre ruído.</summary>
        private const double TaxaMinimaComValor = 0.10;

        public static ParsedFieldRootBuildResult Build(IReadOnlyList<ParsedField>? parsedFields)
        {
            if (parsedFields is null || parsedFields.Count == 0)
                return new ParsedFieldRootBuildResult(null,
                    "Nenhum ParsedField disponível (parse posicional não produziu campos).", 0, 0, 0);

            // Occurrence==0 + IsAggregatedOccurrence é o valor lógico AGREGADO (concatenação de
            // fragmentos físicos repetidos, ex.: LINHA081/infCpl) gerado por
            // AggregatePositionalGroupRepetitions — usar os dois juntos duplicaria o mesmo conteúdo
            // no ROOT. Mantemos só os fragmentos físicos (Occurrence >= 1), que é o que reflete a
            // estrutura real do documento posicional — mesma filosofia do RootTreeBuilder/TclRootBuilder.
            var fisicos = parsedFields.Where(f => !f.IsAggregatedOccurrence).ToList();
            if (fisicos.Count == 0)
                return new ParsedFieldRootBuildResult(null,
                    "Todos os ParsedFields são agregados (Occurrence=0) — sem fragmentos físicos para montar o ROOT.",
                    0, parsedFields.Count, 0);

            var comValor = fisicos.Count(f => !f.IsMissing && !string.IsNullOrWhiteSpace(f.Value));
            var taxa = fisicos.Count == 0 ? 0 : (double)comValor / fisicos.Count;
            var distintos = fisicos.Select(f => f.LineName).Distinct(StringComparer.Ordinal).Count();

            if (taxa < TaxaMinimaComValor)
                return new ParsedFieldRootBuildResult(null,
                    $"instância NÃO produz ROOT confiável: só {taxa:P0} dos {fisicos.Count} campos físicos têm " +
                    $"valor não vazio — parse posicional provavelmente incompatível com o layout informado",
                    distintos, fisicos.Count, comValor);

            var root = new XElement("ROOT");
            XElement? currentLine = null;
            string? currentKey = null;

            // Sequence preserva a ordem física de leitura do documento (não a ordem alfabética de
            // LineName) — é isso que faz linhas repetidas (ex.: 4x LINHA081) virarem elementos
            // repetidos em ordem de chegada, como o RootTreeBuilder já faz para o TXT MQSeries.
            foreach (var field in fisicos.OrderBy(f => f.Sequence))
            {
                var key = $"{field.LineName}{field.Occurrence}";
                if (key != currentKey || currentLine is null)
                {
                    currentLine = new XElement(SafeName(field.LineName));
                    root.Add(currentLine);
                    currentKey = key;
                }

                currentLine.Add(new XElement(SafeName(field.FieldName), Sanitize(field.Value ?? string.Empty)));
            }

            var doc = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root);
            return new ParsedFieldRootBuildResult(doc, null, distintos, fisicos.Count, comValor);
        }

        /// <summary>Nomes de linha/campo às vezes trazem caracteres inválidos para XML (raro, mas o
        /// layout é dado externo) — sanea sem alterar o caso comum.</summary>
        private static string SafeName(string? nome)
        {
            nome ??= string.Empty;
            var sb = new StringBuilder(nome.Length);
            foreach (var c in nome)
                sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
            if (sb.Length == 0 || !(char.IsLetter(sb[0]) || sb[0] == '_'))
                sb.Insert(0, '_');
            return sb.ToString();
        }

        /// <summary>Troca chars de controle inválidos em XML 1.0 por espaço (mesmo tratamento do
        /// <c>RootTreeBuilder</c> para arquivos MQ com lixo binário no filler).</summary>
        private static string Sanitize(string s)
        {
            if (s.All(c => c >= ' ' || c is '\t'))
                return s;
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
                sb.Append(c >= ' ' || c is '\t' ? c : ' ');
            return sb.ToString();
        }
    }

    /// <summary>Resultado da montagem do ROOT a partir de <see cref="ParsedField"/>s reais.</summary>
    /// <param name="Root">Documento <c>&lt;ROOT&gt;&lt;LineName&gt;…&lt;/LineName&gt;&lt;/ROOT&gt;</c>, ou
    /// <c>null</c> quando o gate de qualidade recusou a montagem.</param>
    /// <param name="Motivo">Motivo da recusa (<c>null</c> em caso de sucesso).</param>
    /// <param name="LinhasDistintas">Quantidade de <c>LineName</c> distintos entre os campos físicos.</param>
    /// <param name="CamposFisicos">Total de <see cref="ParsedField"/> físicos (Occurrence >= 1) considerados.</param>
    /// <param name="CamposComValor">Quantos desses campos têm valor não vazio e não marcado como faltante.</param>
    public sealed record ParsedFieldRootBuildResult(
        XDocument? Root,
        string? Motivo,
        int LinhasDistintas,
        int CamposFisicos,
        int CamposComValor);
}
