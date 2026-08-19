using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using LayoutParserApi.Models.Logging;
using LayoutParserApi.Services.Interfaces;

namespace LayoutParserApi.Services.Logging
{
    /// <summary>
    /// Implementação de leitura unificada dos 4 arquivos de log (Api/Lib/Decrypt + a cópia do
    /// log do job de métricas de IA que roda na VM Linux — ver handoff-ponte-log-aimetrics.md).
    /// Desenho fechado pela arquiteta (logging unificado): os arquivos já compartilham
    /// diretório e um formato de linha compatível; este serviço só parseia e normaliza.
    /// Resiliência: falha em um arquivo/fonte nunca derruba as demais (try/catch por fonte
    /// e por linha), e falta de arquivo (ex.: Lib/Decrypt ainda não gerados nesta máquina)
    /// apenas reduz o resultado, sem quebrar o endpoint.
    /// </summary>
    public class UnifiedLogReaderService : IUnifiedLogReaderService
    {
        // ✅ Não reconstrói histórico infinito: além do arquivo ativo, lê no máximo mais
        // 2 arquivos rotacionados mais recentes por fonte (barato, cobre o caso comum).
        private const int MaxFilesPerSource = 3;
        private const int MinPageSize = 1;
        private const int MaxPageSize = 500;

        // Formato do Serilog usado pela própria API (ver outputTemplate em Program.cs):
        // [yyyy-MM-dd HH:mm:ss.fff] [LVL] [Corr:xxx] [Src:xxx] mensagem
        // O grupo [Src:...] é opcional para não quebrar o parse de linhas gravadas ANTES
        // desta mudança (sem o campo Source no outputTemplate).
        // ✅ FIX (QA/Quinn 2026-07-30): [Corr:...] também é OPCIONAL. O job de métricas
        // (ai/XslSynth --mode=metrics-batch, MetricsBatchRunner.cs) configura um Serilog próprio
        // cujo outputTemplate NÃO tem [Corr:...] — com o grupo obrigatório, 100% das linhas
        // "Geracao concluida." eram descartadas (viravam continuação da entrada anterior) e os 3
        // endpoints do Gap 3 respondiam vazio. Relaxar o regex (em vez de alinhar o template do
        // job) recupera também o histórico JÁ gravado, sem re-rodar lote nem redeployar o CLI.
        private static readonly Regex ApiLinePattern = new(
            @"^\[(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})\]\s\[(?<level>[A-Za-z]+)\]"
            + @"(?:\s\[Corr:(?<corr>[^\]]*)\])?(?:\s\[Src:(?<source>[^\]]*)\])?\s(?<message>.*)$",
            RegexOptions.Compiled);

        // Formato do RollingFileLogger usado por LayoutParserLib/LayoutParserDecrypt
        // (fora deste repo, só leitura aqui): "{DateTime:O} [LVL] [Corr:xxx] mensagem"
        private static readonly Regex SimpleLinePattern = new(
            @"^(?<ts>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})?)\s\[(?<level>[A-Za-z]+)\]\s\[Corr:(?<corr>[^\]]*)\]\s(?<message>.*)$",
            RegexOptions.Compiled);

        // Ordem de severidade para o filtro "level = mínimo" (aceita abreviação Serilog e palavra cheia).
        private static readonly Dictionary<string, int> LevelOrder = new(StringComparer.OrdinalIgnoreCase)
        {
            ["VRB"] = 0,
            ["VERBOSE"] = 0,
            ["DBG"] = 1,
            ["DEBUG"] = 1,
            ["INF"] = 2,
            ["INFO"] = 2,
            ["INFORMATION"] = 2,
            ["WRN"] = 3,
            ["WARN"] = 3,
            ["WARNING"] = 3,
            ["ERR"] = 4,
            ["ERROR"] = 4,
            ["FTL"] = 5,
            ["FATAL"] = 5,
            ["CRITICAL"] = 5,
        };

        private readonly IConfiguration _configuration;
        private readonly ILogger<UnifiedLogReaderService> _logger;

        public UnifiedLogReaderService(IConfiguration configuration, ILogger<UnifiedLogReaderService> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<PagedLogResult> GetLogsAsync(UnifiedLogFilter filter)
        {
            filter ??= new UnifiedLogFilter();

            var page = filter.Page < 1 ? 1 : filter.Page;
            var pageSize = filter.PageSize < MinPageSize
                ? MinPageSize
                : Math.Min(filter.PageSize, MaxPageSize);

            try
            {
                // Mesmo diretório configurado pro Serilog (Logging:File:Directory) em Program.cs.
                var logDirectory = _configuration["Logging:File:Directory"] ?? "Logs";

                // ✅ Cada fonte é lida de forma independente e resiliente (Task.WhenAll só paraleliza
                // I/O; erro em uma fonte não propaga pras demais, ver ReadSourceAsync).
                var apiTask = ReadSourceAsync(logDirectory, "layoutparserapi.log", fixedSource: null, ApiLinePattern);
                var libTask = ReadSourceAsync(logDirectory, "layoutparserlib.log", fixedSource: "Lib", SimpleLinePattern);
                var decryptTask = ReadSourceAsync(logDirectory, "layoutparserdecrypt.log", fixedSource: "Decrypt", SimpleLinePattern);

                // ✅ 4ª fonte (ponte de log AiMetrics, docs/architecture/handoff-ponte-log-aimetrics.md):
                // cópia do log do job de métricas que roda na VM Linux, trazida de hora em hora pro
                // diretório de logs da API com nome PRÓPRIO (nunca sobrescrevendo o log ativo).
                // fixedSource: null de propósito — o Source vem do [Src:...] da própria linha, senão
                // o resumo de lote e linhas de outros Sources seriam mascarados como "AiMetrics".
                // Nome hardcoded como os outros 3: chave nova no appsettings.json não chegaria ao
                // servidor (o ci-dev.yml preserva o appsettings do destino).
                var aiTask = ReadSourceAsync(logDirectory, "layoutparserai.log", fixedSource: null, ApiLinePattern);

                // ✅ 5ª fonte: MCP Server (mcp/LayoutParserMcp), agora com logging estruturado
                // Serilog no mesmo outputTemplate da API (ver Program.cs do MCP). fixedSource:
                // "MCP" porque o MCP roda como processo separado e sempre grava com esse Source
                // fixo — não há sub-fontes a distinguir como no caso do log de AiMetrics.
                var mcpTask = ReadSourceAsync(logDirectory, "layoutparsermcp.log", fixedSource: "MCP", ApiLinePattern);

                await Task.WhenAll(apiTask, libTask, decryptTask, aiTask, mcpTask);

                IEnumerable<UnifiedLogEntry> all = apiTask.Result
                    .Concat(libTask.Result)
                    .Concat(decryptTask.Result)
                    .Concat(aiTask.Result)
                    .Concat(mcpTask.Result);

                all = ApplyFilters(all, filter);

                var materialized = all.OrderByDescending(e => e.Timestamp).ToList();
                var totalCount = materialized.Count;
                var items = materialized.Skip((page - 1) * pageSize).Take(pageSize).ToList();

                return new PagedLogResult
                {
                    Items = items,
                    TotalCount = totalCount,
                    Page = page,
                    PageSize = pageSize
                };
            }
            catch (Exception ex)
            {
                // ✅ Degrada graciosamente: erro inesperado na orquestração retorna vazio em vez
                // de derrubar o request (o endpoint continua respondendo 200).
                _logger.LogError(ex, "Falha inesperada ao consultar logs unificados");
                return new PagedLogResult { Items = new List<UnifiedLogEntry>(), TotalCount = 0, Page = page, PageSize = pageSize };
            }
        }

        private IEnumerable<UnifiedLogEntry> ApplyFilters(IEnumerable<UnifiedLogEntry> entries, UnifiedLogFilter filter)
        {
            if (!string.IsNullOrWhiteSpace(filter.Source))
                entries = entries.Where(e => string.Equals(e.Source, filter.Source, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(filter.Level))
            {
                if (LevelOrder.TryGetValue(filter.Level.Trim(), out var minOrder))
                {
                    entries = entries.Where(e => LevelOrder.TryGetValue(e.Level, out var order) && order >= minOrder);
                }
                else
                {
                    // Nível desconhecido: ignora o filtro em vez de falhar a requisição inteira.
                    _logger.LogDebug("Filtro de level desconhecido, ignorado: {Level}", filter.Level);
                }
            }

            if (!string.IsNullOrWhiteSpace(filter.CorrelationId))
                entries = entries.Where(e => string.Equals(e.CorrelationId, filter.CorrelationId, StringComparison.OrdinalIgnoreCase));

            if (filter.From.HasValue)
                entries = entries.Where(e => e.Timestamp >= filter.From.Value);

            if (filter.To.HasValue)
                entries = entries.Where(e => e.Timestamp <= filter.To.Value);

            if (!string.IsNullOrWhiteSpace(filter.Search))
                entries = entries.Where(e => e.Message.Contains(filter.Search, StringComparison.OrdinalIgnoreCase));

            return entries;
        }

        /// <summary>
        /// Lê o arquivo ativo de uma fonte + os rotacionados mais recentes (até MaxFilesPerSource),
        /// parseando cada um. Falha total ou parcial na fonte não propaga — retorna o que der certo.
        /// </summary>
        private async Task<List<UnifiedLogEntry>> ReadSourceAsync(string directory, string baseFileName, string? fixedSource, Regex pattern)
        {
            var result = new List<UnifiedLogEntry>();

            try
            {
                if (!Directory.Exists(directory))
                {
                    _logger.LogWarning("Diretório de logs não encontrado ({Directory}) ao ler {BaseFileName}", directory, baseFileName);
                    return result;
                }

                var nameWithoutExt = Path.GetFileNameWithoutExtension(baseFileName);
                var extension = Path.GetExtension(baseFileName);
                var searchPattern = $"{nameWithoutExt}*{extension}";

                var candidateFiles = Directory.GetFiles(directory, searchPattern)
                    .Select(path => new FileInfo(path))
                    .OrderByDescending(fi => fi.LastWriteTimeUtc)
                    .Take(MaxFilesPerSource)
                    .ToList();

                if (candidateFiles.Count == 0)
                {
                    _logger.LogWarning("Nenhum arquivo encontrado para a fonte {BaseFileName} em {Directory}", baseFileName, directory);
                    return result;
                }

                foreach (var file in candidateFiles)
                {
                    try
                    {
                        var lines = await ReadAllLinesSharedAsync(file.FullName);
                        result.AddRange(ParseLines(lines, fixedSource, pattern, file.Name));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Falha ao ler/parsear arquivo de log {FilePath}, ignorado", file.FullName);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao listar arquivos da fonte {BaseFileName} em {Directory}", baseFileName, directory);
            }

            return result;
        }

        /// <summary>
        /// Abre o arquivo com FileShare.ReadWrite (o sink File do Serilog usa shared:true, e o
        /// RollingFileLogger do Lib/Decrypt reabre/fecha a cada append) para não competir com o
        /// processo que está escrevendo.
        /// </summary>
        private static async Task<List<string>> ReadAllLinesSharedAsync(string path)
        {
            var lines = new List<string>();
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
                lines.Add(line);

            return lines;
        }

        /// <summary>
        /// Parseia as linhas de um arquivo. Linha que não bate no regex principal é tratada como
        /// continuação da entrada anterior (ex.: stack trace multi-linha) — não é descartada
        /// silenciosamente nem quebra o parse do arquivo (try/catch por linha).
        /// </summary>
        /// <remarks>
        /// ✅ FIX (incidente 2026-07-28): linha malformada NUNCA loga Warning-com-stack-trace
        /// individualmente — isso é auto-amplificador (muitas linhas malformadas = muitos
        /// warnings = rotação/eviction acelerada dos logs pelo rollOnFileSizeLimit/
        /// retainedFileCountLimit do Serilog, causa direta da perda de logs históricos reais).
        /// Falha por linha vira Debug (barato, sem stack trace em arquivo por padrão) e as
        /// ocorrências são agregadas num único Warning resumido ao final do arquivo.
        /// </remarks>
        private List<UnifiedLogEntry> ParseLines(IReadOnlyList<string> lines, string? fixedSource, Regex pattern, string fileName)
        {
            var entries = new List<UnifiedLogEntry>();
            UnifiedLogEntry? current = null;
            var malformedCount = 0;

            foreach (var rawLine in lines)
            {
                if (string.IsNullOrEmpty(rawLine))
                    continue;

                try
                {
                    var parsed = TryParseLine(rawLine, fixedSource, pattern);
                    if (parsed != null)
                    {
                        entries.Add(parsed);
                        current = parsed;
                    }
                    else if (current != null)
                    {
                        // Continuação da entrada anterior (stack trace, etc.)
                        current.Message = current.Message + Environment.NewLine + rawLine;
                    }
                    // Se não há entrada anterior (linha corrompida logo no início do arquivo),
                    // a linha é descartada — não há como anexá-la a nada.
                }
                catch (Exception ex)
                {
                    malformedCount++;
                    _logger.LogDebug(ex, "Falha ao parsear linha de log em {FileName}, linha ignorada", fileName);
                }
            }

            if (malformedCount > 0)
            {
                _logger.LogWarning("{Count} linha(s) malformada(s) em {FileName}, ignoradas", malformedCount, fileName);
            }

            return entries;
        }

        private UnifiedLogEntry? TryParseLine(string line, string? fixedSource, Regex pattern)
        {
            var match = pattern.Match(line);
            if (!match.Success)
                return null;

            var tsRaw = match.Groups["ts"].Value;
            DateTime timestamp;

            if (pattern == ApiLinePattern)
            {
                if (!DateTime.TryParseExact(tsRaw, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp))
                    return null;
            }
            else
            {
                // ✅ FIX (incidente 2026-07-28): RoundtripKind + AssumeUniversal é combinação
                // inválida no .NET — TryParse LANÇA ArgumentException em vez de retornar false,
                // fazendo 100% das linhas Lib/Decrypt falharem o parse. RoundtripKind sozinho já
                // preserva Kind=Utc pro sufixo "Z" do formato "O" usado pelo RollingFileLogger.
                if (!DateTime.TryParse(tsRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp))
                    return null;
            }

            var level = match.Groups["level"].Value;
            var corr = match.Groups["corr"].Value;
            var message = match.Groups["message"].Value;

            // Source: fixo pra Lib/Decrypt; pro arquivo da API, vem do campo [Src:...] da própria
            // linha (default "Backend" se ausente — linhas antigas gravadas antes desta mudança).
            var source = fixedSource ?? (match.Groups["source"].Success && !string.IsNullOrWhiteSpace(match.Groups["source"].Value)
                ? match.Groups["source"].Value
                : "Backend");

            return new UnifiedLogEntry
            {
                Source = source,
                Timestamp = timestamp,
                Level = level,
                CorrelationId = string.IsNullOrWhiteSpace(corr) ? null : corr,
                Message = message
            };
        }
    }
}
