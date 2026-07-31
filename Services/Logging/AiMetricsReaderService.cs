using System.Globalization;
using System.Text.RegularExpressions;

using LayoutParserApi.Models.Logging;
using LayoutParserApi.Services.Interfaces;

namespace LayoutParserApi.Services.Logging
{
    /// <summary>
    /// Implementação do Gap 3 (docs/architecture/handoff-frontend-gap-3-painel-ia-metrics.md):
    /// parseia as linhas "Geracao concluida." do job ai/XslSynth --mode=metrics-batch
    /// (Source=AiMetrics) por cima do que <see cref="IUnifiedLogReaderService"/> já lê do arquivo
    /// de log unificado. Não duplica leitura/rotação de arquivo — só adiciona o parse dos
    /// pares Chave=Valor da mensagem estruturada.
    /// Linhas "Resumo do lote." são ignoradas: todos os campos pedidos pelos 2 endpoints deste
    /// Gap são deriváveis a partir dos casos individuais.
    /// </summary>
    public class AiMetricsReaderService : IAiMetricsReaderService
    {
        private const string AiMetricsSource = "AiMetrics";
        private const string GeracaoPrefix = "Geracao concluida.";
        private const string CypressPrefix = "Cypress validado.";

        // Página grande o suficiente pra volume real (~54 casos/rodada), evitando ida-e-volta
        // extra na maioria dos casos; o loop de paginação abaixo cobre o crescimento no tempo.
        private const int FetchPageSize = 500;

        // ✅ FIX (QA/Quinn): mesmo teto do UnifiedLogFilter irmão (UnifiedLogReaderService.cs) —
        // sem isso, um client mal-intencionado ou um bug de UI pode pedir uma página gigante.
        private const int MinPageSize = 1;
        private const int MaxPageSize = 500;

        // Formato gravado pelo Endpoint 3 (AiMetricsController.PostCypressResult):
        // "Cypress validado. Layout=x CypressValidado=true CStatPollux=100 Observacao=texto livre"
        // Ancorado nas chaves conhecidas e nesta ordem: Observacao= é o ÚLTIMO grupo e captura tudo
        // até o fim da linha, então texto livre com "=" não consegue mais forjar campo (ver <remarks>
        // de TryParseCypress). Os grupos finais são opcionais pra tolerar linhas sem observação.
        private static readonly Regex CypressLinePattern = new(
            @"^Cypress validado\.\s+Layout=(?<layout>\S*)"
            + @"(?:\s+CypressValidado=(?<cypressValidado>\S*))?"
            + @"(?:\s+CStatPollux=(?<cstat>\S*))?"
            + @"(?:\s+Observacao=(?<observacao>.*))?$",
            RegexOptions.Compiled);

        private readonly IUnifiedLogReaderService _unifiedLogReaderService;
        private readonly ILogger<AiMetricsReaderService> _logger;

        public AiMetricsReaderService(IUnifiedLogReaderService unifiedLogReaderService, ILogger<AiMetricsReaderService> logger)
        {
            _unifiedLogReaderService = unifiedLogReaderService;
            _logger = logger;
        }

        public async Task<PagedAiMetricsGenerationsResult> GetGenerationsAsync(AiMetricsGenerationFilter filter)
        {
            filter ??= new AiMetricsGenerationFilter();
            var page = filter.Page < 1 ? 1 : filter.Page;
            var pageSize = filter.PageSize < MinPageSize ? MinPageSize : Math.Min(filter.PageSize, MaxPageSize);

            var all = await GetAllGenerationsAsync(filter.De, filter.Ate);

            IEnumerable<AiMetricsGeneration> filtered = all;

            if (!string.IsNullOrWhiteSpace(filter.Layout))
                filtered = filtered.Where(g => g.Layout.Contains(filter.Layout, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(filter.Modelo))
                filtered = filtered.Where(g => string.Equals(g.Modelo, filter.Modelo, StringComparison.OrdinalIgnoreCase));

            if (filter.Sucesso.HasValue)
                filtered = filtered.Where(g => g.Sucesso == filter.Sucesso.Value);

            var materialized = filtered.OrderByDescending(g => g.Timestamp).ToList();
            var totalCount = materialized.Count;
            var items = materialized.Skip((page - 1) * pageSize).Take(pageSize).ToList();

            return new PagedAiMetricsGenerationsResult
            {
                Success = true,
                TotalCount = totalCount,
                Page = page,
                PageSize = pageSize,
                Items = items
            };
        }

        public async Task<AiMetricsSummary> GetSummaryAsync(DateTime? de, DateTime? ate)
        {
            var all = await GetAllGenerationsAsync(de, ate);

            if (all.Count == 0)
            {
                // ✅ Job nunca rodou (ou ainda não tem log neste ambiente) — resumo vazio,
                // nunca 500 (degradação graciosa, ver dotnet-standards.md).
                return new AiMetricsSummary { Success = true };
            }

            var sucesso = all.Where(g => g.Sucesso).ToList();

            var porDocType = all
                .GroupBy(g => g.DocType)
                .Select(grp => new AiMetricsDocTypeSummary
                {
                    DocType = grp.Key,
                    Total = grp.Count(),
                    Sucesso = grp.Count(g => g.Sucesso),
                    TokensPorSegundoMedio = grp.Any(g => g.Sucesso)
                        ? grp.Where(g => g.Sucesso).Average(g => g.TokensPorSegundo)
                        : 0
                })
                .OrderBy(d => d.DocType)
                .ToList();

            return new AiMetricsSummary
            {
                Success = true,
                TotalGeracoes = all.Count,
                TotalSucesso = sucesso.Count,
                TotalFalhas = all.Count - sucesso.Count,
                TokensPorSegundoMedio = sucesso.Count > 0 ? sucesso.Average(g => g.TokensPorSegundo) : 0,
                TagOverlapMedio = sucesso.Count > 0 ? sucesso.Average(g => g.TagOverlapRatio) : 0,
                TextSimilarityMedia = sucesso.Count > 0 ? sucesso.Average(g => g.TextSimilarityRatio) : 0,
                TotalXsdValidado = all.Count(g => g.XsdValido == true),
                TotalCypressValidado = all.Count(g => g.CypressValidado == true),

                // ✅ FIX (QA/Quinn 2026-07-30): contar "cStat não-vazio" transformava REJEIÇÃO em
                // autorização — cStat 110/225 são rejeição e entravam no card de autorizados.
                // Decisão de arquitetura: a API NÃO re-deriva autorização a partir do código; quem
                // sabe se o cStat significou aceite é o cliente Cypress, e ele já codifica isso em
                // CypressValidado. Allow-list ("100") erraria o dataset atual, majoritariamente
                // eventos (CancCTe/consSitCTe), cujos códigos de aceite são outros.
                TotalCStatAutorizado = all.Count(g => g.CypressValidado == true),
                PorDocType = porDocType,
                UltimaRodada = all.Max(g => g.Timestamp)
            };
        }

        /// <summary>
        /// Busca TODAS as linhas AiMetrics (paginando o leitor unificado, que limita o retorno
        /// por página) já parseadas, com o MERGE do resultado do Cypress/Pollux (linhas
        /// "Cypress validado.", gravadas pelo Endpoint 3) aplicado por cima das gerações originais,
        /// e só então recorta o período pedido. Falha na leitura/parse degrada pra lista vazia —
        /// nunca derruba os endpoints acima.
        /// </summary>
        /// <remarks>
        /// ✅ FIX (QA/Quinn 2026-07-30): o filtro de data NÃO pode ir pro leitor unificado. As
        /// linhas "Cypress validado." nascem depois da geração (o Job 2 roda no dia seguinte), então
        /// filtrar o sábado descartava justamente o resultado postado no domingo e o merge sumia do
        /// painel. Lê tudo → faz o merge → filtra as GERAÇÕES por De/Ate. Volume é baixo
        /// (~54 casos/rodada) e o leitor já filtra em memória, então não há custo relevante de I/O.
        /// </remarks>
        private async Task<List<AiMetricsGeneration>> GetAllGenerationsAsync(DateTime? de, DateTime? ate)
        {
            var result = new List<AiMetricsGeneration>();

            // Última atualização de Cypress por Layout — usada pro merge lógico (não físico, ver
            // adendo do Gap 3): a leitura, não o arquivo, reflete o resultado mais recente.
            var latestCypressByLayout = new Dictionary<string, CypressResultEntry>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var page = 1;
                int totalCount;

                do
                {
                    // Sem From/To de propósito (ver <remarks> acima) — o recorte de período é
                    // aplicado no fim, só sobre as gerações, depois do merge do Cypress.
                    var raw = await _unifiedLogReaderService.GetLogsAsync(new Models.Logging.UnifiedLogFilter
                    {
                        Source = AiMetricsSource,
                        Page = page,
                        PageSize = FetchPageSize
                    });

                    totalCount = raw.TotalCount;

                    foreach (var entry in raw.Items)
                    {
                        var parsedGeracao = TryParseGeracao(entry.Message, entry.Timestamp);
                        if (parsedGeracao != null)
                        {
                            result.Add(parsedGeracao);
                            continue;
                        }

                        var parsedCypress = TryParseCypress(entry.Message, entry.Timestamp);
                        if (parsedCypress != null
                            && (!latestCypressByLayout.TryGetValue(parsedCypress.Layout, out var existing)
                                || parsedCypress.Timestamp >= existing.Timestamp))
                        {
                            latestCypressByLayout[parsedCypress.Layout] = parsedCypress;
                        }
                    }

                    page++;
                }
                while ((page - 1) * FetchPageSize < totalCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha inesperada ao ler/parsear métricas de IA (AiMetrics)");
                return new List<AiMetricsGeneration>();
            }

            // ✅ Idempotência da ingestão (POST /api/ai-metrics/generations/ingest): o log é
            // append-only, então reenviar o mesmo lote (retry de rede, replay de uma rodada) grava
            // linhas duplicadas. Colapsa por (Layout, Timestamp) mantendo a ÚLTIMA lida — reenvio
            // não infla os totais/médias do painel. Gerações legítimas de rodadas diferentes têm
            // Timestamp diferente e são todas preservadas; mesmo layout no mesmo instante exato só
            // acontece em duplicata real (uma geração leva segundos).
            result = result
                .GroupBy(g => (g.Layout, g.Timestamp))
                .Select(grupo => grupo.Last())
                .ToList();

            ApplyCypressMerge(result, latestCypressByLayout);

            // Recorte de período: só agora, e só sobre as gerações (o merge já foi aplicado com a
            // linha de Cypress que estaria fora da janela).
            IEnumerable<AiMetricsGeneration> noPeriodo = result;

            if (de.HasValue)
                noPeriodo = noPeriodo.Where(g => g.Timestamp >= de.Value);

            if (ate.HasValue)
                noPeriodo = noPeriodo.Where(g => g.Timestamp <= ate.Value);

            return noPeriodo.ToList();
        }

        /// <summary>
        /// Aplica o resultado do Cypress por cima das gerações: para cada Layout com resultado
        /// postado, marca CypressValidado/CStatPollux (que nasceram null no metrics-batch) APENAS na
        /// geração mais recente anterior àquele POST. Layout sem entrada de Cypress fica intacto.
        /// </summary>
        /// <remarks>
        /// ✅ FIX (QA/Quinn 2026-07-30): a condição anterior era só "cypress &gt;= geracao", sem
        /// limite inferior. Como o dataset re-roda semanalmente, o MESMO Layout aparece em várias
        /// rodadas e um único POST marcava TODAS as rodadas históricas — 1 validação virava 2 (ou N)
        /// no card do painel. O resultado pertence à rodada que o Job 2 acabou de validar, isto é, a
        /// geração mais recente que já existia quando o POST chegou.
        /// </remarks>
        private static void ApplyCypressMerge(
            List<AiMetricsGeneration> geracoes,
            Dictionary<string, CypressResultEntry> latestCypressByLayout)
        {
            if (latestCypressByLayout.Count == 0)
                return;

            foreach (var grupo in geracoes.GroupBy(g => g.Layout, StringComparer.OrdinalIgnoreCase))
            {
                if (!latestCypressByLayout.TryGetValue(grupo.Key, out var cypress))
                    continue;

                var alvo = grupo
                    .Where(g => g.Timestamp <= cypress.Timestamp)
                    .OrderByDescending(g => g.Timestamp)
                    .FirstOrDefault();

                if (alvo is null)
                    continue; // POST anterior a qualquer geração desse layout — nada a marcar.

                alvo.CypressValidado = cypress.CypressValidado;
                alvo.CStatPollux = cypress.CStatPollux;
            }
        }

        /// <summary>Resultado já parseado de uma linha "Cypress validado.", usado só internamente pro merge.</summary>
        private sealed record CypressResultEntry(string Layout, bool? CypressValidado, string? CStatPollux, DateTime Timestamp);

        /// <summary>
        /// Parseia uma linha "Geracao concluida. Chave=Valor ...". Linhas "Resumo do lote." e
        /// qualquer coisa que não bata no formato esperado retornam null (ignoradas pelo
        /// chamador) — nunca lança, pra não derrubar o parse do restante do arquivo.
        /// </summary>
        private AiMetricsGeneration? TryParseGeracao(string message, DateTime timestamp)
        {
            if (string.IsNullOrWhiteSpace(message) || !message.StartsWith(GeracaoPrefix, StringComparison.Ordinal))
                return null;

            try
            {
                // ⚠️ Limitação conhecida (QA/Quinn): a tokenização assume que nenhum valor
                // (em especial "Layout=...") contém espaço — hoje válido pros paths reais do
                // dataset (backslash, sem espaço), mas se algum dia um layout com espaço no nome
                // passar a ser logado, ele seria truncado silenciosamente no primeiro espaço.
                // Baixa prioridade: não corrigido agora, documentado para revisão futura.
                var tokens = message.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var token in tokens)
                {
                    var separatorIndex = token.IndexOf('=');
                    if (separatorIndex <= 0)
                        continue; // parte do prefixo ("Geracao", "concluida."), sem '='

                    var key = token[..separatorIndex];
                    var value = token[(separatorIndex + 1)..];
                    fields[key] = value;
                }

                var layout = fields.GetValueOrDefault("Layout", string.Empty);

                return new AiMetricsGeneration
                {
                    Layout = layout,
                    DocType = DeriveDocType(layout),
                    Modelo = fields.GetValueOrDefault("Modelo", string.Empty),
                    // ✅ Prefere o instante REAL da geração (campo Timestamp=, gravado pela ingestão
                    // via POST generations/ingest) ao instante da linha de log — num lote postado no
                    // fim de uma rodada de ~4h, todos os casos teriam o mesmo horário do POST.
                    // Ausente (linhas do job local, anteriores à ingestão) = timestamp da linha.
                    Timestamp = ParseTimestamp(fields.GetValueOrDefault("Timestamp")) ?? timestamp,
                    TokensPorSegundo = ParseDouble(fields.GetValueOrDefault("TokensPorSegundo")),
                    TamanhoPromptChars = ParseInt(fields.GetValueOrDefault("TamanhoPromptChars")),
                    DuracaoSegundos = ParseDouble(fields.GetValueOrDefault("DuracaoSegundos")),
                    SimilaridadeFewShot = ParseDouble(fields.GetValueOrDefault("SimilaridadeFewShot")),
                    TagOverlapRatio = ParseDouble(fields.GetValueOrDefault("TagOverlapRatio")),
                    TextSimilarityRatio = ParseDouble(fields.GetValueOrDefault("TextSimilarityRatio")),
                    XsdValido = ParseNullableBool(fields.GetValueOrDefault("XsdValido")),
                    CypressValidado = ParseNullableBool(fields.GetValueOrDefault("CypressValidado")),
                    CStatPollux = ParseNullableString(fields.GetValueOrDefault("CStatPollux")),
                    Sucesso = ParseBool(fields.GetValueOrDefault("Sucesso"))
                };
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Falha ao parsear linha AiMetrics, ignorada: {Message}", message);
                return null;
            }
        }

        /// <summary>
        /// Parseia uma linha "Cypress validado. Chave=Valor ..." (gravada pelo Endpoint 3 —
        /// POST /api/ai-metrics/cypress-result). Retorna null pra qualquer coisa fora do formato
        /// esperado (nunca lança, mesmo padrão do parser irmão).
        /// </summary>
        /// <remarks>
        /// ✅ FIX (QA/Quinn 2026-07-30): NÃO usa a tokenização por espaço do <see cref="TryParseGeracao"/>.
        /// Esta linha carrega um campo de TEXTO LIVRE (Observacao), e com split(' ') + last-wins uma
        /// observação honesta como "Rejeitado: esperava CStatPollux=100 e veio 110" forjava o campo
        /// real e o painel exibia cStat 100 pra uma REJEIÇÃO. O regex é ancorado nas chaves conhecidas
        /// e Observacao= captura greedy até o fim da linha, então nada depois dela vira campo.
        /// </remarks>
        private CypressResultEntry? TryParseCypress(string message, DateTime timestamp)
        {
            if (string.IsNullOrWhiteSpace(message) || !message.StartsWith(CypressPrefix, StringComparison.Ordinal))
                return null;

            try
            {
                var match = CypressLinePattern.Match(message);
                if (!match.Success)
                    return null;

                var layout = match.Groups["layout"].Value;
                if (string.IsNullOrWhiteSpace(layout))
                    return null;

                return new CypressResultEntry(
                    layout,
                    ParseNullableBool(GroupValueOrNull(match, "cypressValidado")),
                    ParseNullableString(GroupValueOrNull(match, "cstat")),
                    timestamp);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Falha ao parsear linha de resultado do Cypress, ignorada: {Message}", message);
                return null;
            }
        }

        private static string DeriveDocType(string layout)
        {
            if (string.IsNullOrWhiteSpace(layout))
                return string.Empty;

            var separatorIndex = layout.IndexOfAny(new[] { '\\', '/' });
            return separatorIndex > 0 ? layout[..separatorIndex] : layout;
        }

        // ✅ FIX (QA/Quinn): NumberStyles.Float aceita os literais "NaN"/"Infinity"/"-Infinity"
        // (independe do NumberStyles, é o NumberFormatInfo invariante) — plausível em produção
        // quando DuracaoSegundos ~0 (timeout instantâneo do Ollama), gerando TokensPorSegundo=NaN
        // no log real. System.Text.Json lança ArgumentException ao serializar NaN/Infinity, e
        // isso acontece DEPOIS do controller já ter retornado Ok(result) — fora do try/catch,
        // derrubando a resposta inteira com 500 cru. Normaliza pra 0: não há leitura útil de
        // NaN/Infinity num painel de métricas.
        private static double ParseDouble(string? raw)
        {
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return 0;

            return double.IsNaN(value) || double.IsInfinity(value) ? 0 : value;
        }

        private static int ParseInt(string? raw)
            => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

        // Grupo opcional do regex ausente na linha != grupo presente e vazio: devolve null pros dois
        // (os ParseNullable* já tratam null/"null"/vazio como "não informado").
        private static string? GroupValueOrNull(Match match, string groupName)
        {
            var group = match.Groups[groupName];
            return group.Success ? group.Value : null;
        }

        private static bool ParseBool(string? raw)
            => bool.TryParse(raw, out var value) && value;

        // ✅ O log grava literalmente "null" (via {Xsd=null}) quando o valor C# é null — trata
        // esse literal como ausência, não como erro de parse.
        private static bool? ParseNullableBool(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "null", StringComparison.OrdinalIgnoreCase))
                return null;

            return bool.TryParse(raw, out var value) ? value : null;
        }

        /// <summary>
        /// Parseia o campo opcional <c>Timestamp=</c> (instante real da geração). O arquivo de log
        /// grava a hora LOCAL do servidor sem fuso, então um valor explicitamente UTC é convertido —
        /// misturar bases faria o merge do Cypress (<c>cypress &gt;= geracao</c>) falhar em silêncio.
        /// Valor ausente/ilegível retorna null, e o chamador cai no timestamp da linha.
        /// </summary>
        private static DateTime? ParseTimestamp(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "null", StringComparison.OrdinalIgnoreCase))
                return null;

            if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
                return null;

            return value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value;
        }

        private static string? ParseNullableString(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "null", StringComparison.OrdinalIgnoreCase))
                return null;

            return raw;
        }
    }
}
