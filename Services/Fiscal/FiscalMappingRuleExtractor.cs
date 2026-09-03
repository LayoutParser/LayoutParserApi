using System.IO.Compression;
using System.Xml.Linq;
using LayoutParserApi.Models.Fiscal;

namespace LayoutParserApi.Services.Fiscal
{
    /// <summary>
    /// Passo 1 da autoria fiscal assistida (issue #103) — extração determinística, SEM LLM, de
    /// tabelas de decisão fiscal a partir de um arquivo .xlsx real fornecido pelo dono do projeto.
    ///
    /// Formato real confirmado contra
    /// <c>Layout_NF-e_Mensageria_Envio_ReformaTritutária_v1 - NT 1.50.xlsx</c> (2026-09-02):
    /// o Excel de autoria fiscal do dono NÃO é uma lista de/para (campoOrigem → campoDestino,
    /// condição) como o desenho original hipotético (`plano-tecnico-backlog-pendente-2026-09-02.md`
    /// #103) previa. É uma mistura de dois tipos de aba bem diferentes:
    ///   1. Abas de "catálogo de layout posicional" (ex.: "Layout-Emissão-XML-4.00") — praticamente
    ///      idêntas em formato às planilhas de layout já usadas no baixo-código do projeto
    ///      (colunas Item/ID/Campo/Descrição/#XML/ID PAI/Inicio/Fim/Tamanho/Tipo/Ocorrencia/
    ///      Decimais/Formato/Considerações). Não é uma "regra condicional" — é a definição de
    ///      estrutura, já coberta pelo parsing de layout existente do projeto. Este extrator
    ///      DELIBERADAMENTE NÃO tenta reprocessar essas abas — ficam em
    ///      <see cref="FiscalMappingRuleExtractionResult.SkippedSheets"/>.
    ///   2. Abas de "tabela de decisão" (ex.: "Regra-CST 40 41 e 50", "Detalhe-CST-ICMS") — aqui
    ///      sim mora a lógica condicional real: uma linha de cabeçalho com um rótulo "Regra"/"Item"
    ///      na primeira coluna seguido de nomes de campo de condição (ex.: "orig", "CST", "vICMS",
    ///      "motDesICMS"), e linhas de dados onde cada condição pode ser um valor único, uma lista
    ///      textual ("0 ou 1 ou 2") ou uma faixa textual ("Maior 0,00") — e uma coluna final de
    ///      desfecho em texto livre (ex.: "Sistema Signature gera as TAG's VICMS e motDesICMS").
    ///      É este segundo formato que <see cref="Extract"/> reconhece e estrutura em
    ///      <see cref="FiscalMappingRule"/>.
    ///
    /// Implementação usa apenas <see cref="System.IO.Compression.ZipArchive"/> +
    /// <see cref="System.Xml.Linq"/> (já no shared framework, sem dependência nova) para ler o
    /// XLSX como pacote OOXML bruto — decisão deliberada para não introduzir ClosedXML/EPPlus só
    /// para esta primeira fatia determinística; se o Passo 2 (geração de XSLT) precisar de leitura
    /// de estilo/fórmula mais rica, reavaliar.
    /// </summary>
    public interface IFiscalMappingRuleExtractor
    {
        /// <summary>
        /// Extrai as regras de tabela de decisão de todas as abas do workbook. Não lança para
        /// abas fora do formato esperado — apenas registra em <c>SkippedSheets</c>.
        /// </summary>
        FiscalMappingRuleExtractionResult Extract(Stream xlsxStream);
    }

    public class FiscalMappingRuleExtractor : IFiscalMappingRuleExtractor
    {
        private static readonly string[] RuleHeaderMarkers = { "regra" };
        private static readonly string[] StopMarkers = { "legenda" };

        private readonly ILogger<FiscalMappingRuleExtractor> _logger;

        public FiscalMappingRuleExtractor(ILogger<FiscalMappingRuleExtractor> logger)
        {
            _logger = logger;
        }

        public FiscalMappingRuleExtractionResult Extract(Stream xlsxStream)
        {
            var result = new FiscalMappingRuleExtractionResult();

            using var archive = new ZipArchive(xlsxStream, ZipArchiveMode.Read, leaveOpen: true);

            var sharedStrings = ReadSharedStrings(archive);
            var sheets = ReadSheetCatalog(archive);

            foreach (var (sheetName, sheetPath) in sheets)
            {
                var entry = archive.GetEntry(sheetPath);
                if (entry is null)
                {
                    _logger.LogWarning("Aba {SheetName} referenciada no workbook mas ausente no pacote ({SheetPath})", sheetName, sheetPath);
                    result.SkippedSheets.Add(sheetName);
                    continue;
                }

                var rows = ReadRows(entry, sharedStrings);
                var rules = TryExtractDecisionTable(sheetName, rows);
                if (rules is null)
                {
                    result.SkippedSheets.Add(sheetName);
                    continue;
                }

                result.DecisionTableSheets.Add(sheetName);
                result.Rules.AddRange(rules);
            }

            return result;
        }

        /// <summary>
        /// Varre a aba procurando uma linha de cabeçalho de tabela de decisão ("Regra" na 1ª
        /// coluna + nomes de condição nas seguintes). Retorna null se a aba não tiver nenhuma —
        /// nesse caso ela é tratada como fora de escopo deste extrator (provável catálogo de
        /// layout posicional), não como erro.
        /// </summary>
        private List<FiscalMappingRule>? TryExtractDecisionTable(string sheetName, List<List<string>> rows)
        {
            var headerRowIndex = rows.FindIndex(r => r.Count > 0 && RuleHeaderMarkers.Contains(r[0].Trim().ToLowerInvariant()));
            if (headerRowIndex < 0)
            {
                return null;
            }

            var headerRow = rows[headerRowIndex];
            // Colunas 2..N do cabeçalho = nomes das condições (ignora células vazias no fim).
            var headers = headerRow.Skip(1).ToList();
            while (headers.Count > 0 && string.IsNullOrWhiteSpace(headers[^1]))
            {
                headers.RemoveAt(headers.Count - 1);
            }

            if (headers.Count == 0)
            {
                _logger.LogWarning("Aba {SheetName} tem linha 'Regra' mas sem colunas de condição — tratando como fora de escopo", sheetName);
                return null;
            }

            var rules = new List<FiscalMappingRule>();

            for (var i = headerRowIndex + 1; i < rows.Count; i++)
            {
                var row = rows[i];
                var firstCell = row.Count > 0 ? row[0].Trim() : string.Empty;

                if (row.Count == 0 || row.All(string.IsNullOrWhiteSpace))
                {
                    // Linha inteiramente em branco encerra a tabela de decisão desta aba.
                    break;
                }

                if (StopMarkers.Contains(firstCell.ToLowerInvariant()))
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(firstCell))
                {
                    // 1ª coluna vazia mas linha tem conteúdo — é a linha de legenda logo abaixo do
                    // cabeçalho (ex.: "|X|X|xx|xx" marcando obrigatório/opcional), não uma regra.
                    // Pula sem contar como dado — formato real confirmado tem essa linha entre o
                    // cabeçalho e a 1ª regra.
                    continue;
                }

                var rule = new FiscalMappingRule
                {
                    SheetName = sheetName,
                    RuleNumber = firstCell,
                    SourceRowNumber = i + 1, // 1-based, já contando cabeçalho real da planilha
                };

                var expectedColumns = 1 + headers.Count;
                if (row.Count < expectedColumns)
                {
                    // Linha mais curta do que o cabeçalho — não adivinha, sinaliza revisão humana.
                    rule.RequiresManualReview = true;
                    for (var h = 0; h < headers.Count && (h + 1) < row.Count; h++)
                    {
                        rule.Conditions.Add(new FiscalRuleCondition { Field = headers[h], RawValue = row[h + 1] });
                    }
                }
                else
                {
                    for (var h = 0; h < headers.Count; h++)
                    {
                        rule.Conditions.Add(new FiscalRuleCondition { Field = headers[h], RawValue = row[h + 1] });
                    }

                    var outcomeCells = row.Skip(expectedColumns).Where(c => !string.IsNullOrWhiteSpace(c));
                    rule.Outcome = string.Join(" ", outcomeCells).Trim();

                    if (string.IsNullOrWhiteSpace(rule.Outcome))
                    {
                        // Sem desfecho textual reconhecível — não é erro fatal, mas precisa de olho
                        // humano antes de virar XSLT (Passo 2 não tem o que gerar sem desfecho).
                        rule.RequiresManualReview = true;
                    }
                }

                rules.Add(rule);
            }

            return rules;
        }

        // ---- leitura de baixo nível do pacote OOXML ----

        private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        private static List<string> ReadSharedStrings(ZipArchive archive)
        {
            var entry = archive.GetEntry("xl/sharedStrings.xml");
            var list = new List<string>();
            if (entry is null)
            {
                return list;
            }

            using var stream = entry.Open();
            var doc = XDocument.Load(stream);
            foreach (var si in doc.Root!.Elements(Main + "si"))
            {
                var text = string.Concat(si.Descendants(Main + "t").Select(t => t.Value));
                list.Add(text);
            }

            return list;
        }

        private static List<(string Name, string Path)> ReadSheetCatalog(ZipArchive archive)
        {
            var workbookEntry = archive.GetEntry("xl/workbook.xml")
                ?? throw new InvalidDataException("xlsx inválido: xl/workbook.xml ausente.");
            var relsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels");

            XDocument workbookDoc;
            using (var workbookStream = workbookEntry.Open())
            {
                workbookDoc = XDocument.Load(workbookStream);
            }

            var relIdToTarget = new Dictionary<string, string>();
            if (relsEntry is not null)
            {
                XDocument relsDoc;
                using (var relsStream = relsEntry.Open())
                {
                    relsDoc = XDocument.Load(relsStream);
                }
                XNamespace relNs = "http://schemas.openxmlformats.org/package/2006/relationships";
                foreach (var rel in relsDoc.Root!.Elements(relNs + "Relationship"))
                {
                    var id = rel.Attribute("Id")?.Value;
                    var target = rel.Attribute("Target")?.Value;
                    if (id is not null && target is not null)
                    {
                        relIdToTarget[id] = target;
                    }
                }
            }

            var sheets = new List<(string, string)>();
            var sheetsElement = workbookDoc.Root!.Element(Main + "sheets");
            if (sheetsElement is null)
            {
                return sheets;
            }

            foreach (var sheet in sheetsElement.Elements(Main + "sheet"))
            {
                var name = sheet.Attribute("name")?.Value ?? "(sem nome)";
                var rId = sheet.Attribute(Rel + "id")?.Value;
                if (rId is null || !relIdToTarget.TryGetValue(rId, out var target))
                {
                    continue;
                }

                var path = target.StartsWith("/") ? target.TrimStart('/') : $"xl/{target}";
                sheets.Add((name, path));
            }

            return sheets;
        }

        private static List<List<string>> ReadRows(ZipArchiveEntry entry, List<string> sharedStrings)
        {
            using var stream = entry.Open();
            var doc = XDocument.Load(stream);
            var sheetData = doc.Root!.Element(Main + "sheetData");
            var rows = new List<List<string>>();
            if (sheetData is null)
            {
                return rows;
            }

            foreach (var rowElement in sheetData.Elements(Main + "row"))
            {
                var cellsByIndex = new SortedDictionary<int, string>();
                foreach (var cellElement in rowElement.Elements(Main + "c"))
                {
                    var reference = cellElement.Attribute("r")?.Value ?? string.Empty;
                    var columnIndex = ColumnLettersToIndex(reference);
                    var type = cellElement.Attribute("t")?.Value;
                    var valueElement = cellElement.Element(Main + "v");

                    string value;
                    if (type == "s" && valueElement is not null && int.TryParse(valueElement.Value, out var sharedIndex)
                        && sharedIndex >= 0 && sharedIndex < sharedStrings.Count)
                    {
                        value = sharedStrings[sharedIndex];
                    }
                    else if (type == "inlineStr")
                    {
                        value = string.Concat(cellElement.Descendants(Main + "t").Select(t => t.Value));
                    }
                    else
                    {
                        value = valueElement?.Value ?? string.Empty;
                    }

                    cellsByIndex[columnIndex] = value;
                }

                if (cellsByIndex.Count == 0)
                {
                    rows.Add(new List<string>());
                    continue;
                }

                var maxColumn = cellsByIndex.Keys.Max();
                var row = new List<string>(maxColumn);
                for (var c = 1; c <= maxColumn; c++)
                {
                    row.Add(cellsByIndex.TryGetValue(c, out var v) ? v : string.Empty);
                }

                rows.Add(row);
            }

            return rows;
        }

        /// <summary>Converte referência de célula (ex.: "C7") para índice de coluna 1-based.</summary>
        private static int ColumnLettersToIndex(string cellReference)
        {
            var index = 0;
            foreach (var ch in cellReference)
            {
                if (!char.IsLetter(ch))
                {
                    break;
                }

                index = index * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
            }

            return index == 0 ? 1 : index;
        }
    }
}
