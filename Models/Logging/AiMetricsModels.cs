namespace LayoutParserApi.Models.Logging
{
    /// <summary>
    /// Uma geração individual (linha "Geracao concluida." com Source=AiMetrics), já parseada e
    /// tipada. Ver contrato definitivo em
    /// docs/architecture/handoff-frontend-gap-3-painel-ia-metrics.md.
    /// </summary>
    public class AiMetricsGeneration
    {
        /// <summary>Caminho do layout gerado, ex. <c>CTe\2.00a\CTe200_CancCTe_NeogridToSefaz</c>.</summary>
        public string Layout { get; set; } = string.Empty;

        // ✅ Derivado no backend (primeiro segmento de Layout, ex. "CTe\2.00a\..." -> "CTe") —
        // o front-end não deve reimplementar esse parsing.
        /// <summary>Tipo de documento (NFe/CTe/MDFe), derivado no backend do primeiro segmento de <see cref="Layout"/>.</summary>
        public string DocType { get; set; } = string.Empty;

        /// <summary>Modelo Ollama usado na geração, ex. <c>qwen2.5-coder:7b</c>.</summary>
        public string Modelo { get; set; } = string.Empty;

        /// <summary>Instante em que a geração foi concluída (UTC).</summary>
        public DateTime Timestamp { get; set; }

        /// <summary>Vazão de tokens/segundo reportada pelo Ollama para esta geração.</summary>
        public double TokensPorSegundo { get; set; }

        /// <summary>Tamanho do prompt (em caracteres) enviado ao modelo.</summary>
        public int TamanhoPromptChars { get; set; }

        /// <summary>Duração total da geração, em segundos.</summary>
        public double DuracaoSegundos { get; set; }

        /// <summary>Similaridade do caso com o exemplo few-shot mais próximo usado no prompt (0–1).</summary>
        public double SimilaridadeFewShot { get; set; }

        /// <summary>Sobreposição de tags estruturais entre saída gerada e gabarito (0–1).</summary>
        public double TagOverlapRatio { get; set; }

        /// <summary>Similaridade textual entre saída gerada e gabarito (0–1).</summary>
        public double TextSimilarityRatio { get; set; }

        // Pendentes até validação XSD/Cypress-Pollux existirem no loop (ver handoff) — "null"
        // literal no log até lá, não é erro de parse.
        /// <summary>
        /// Resultado da validação XSD do XML gerado. <c>null</c> = ainda não avaliado (a validação
        /// XSD não está cabeada no job hoje) — tratar como "pendente", nunca como falha.
        /// </summary>
        public bool? XsdValido { get; set; }

        /// <summary>
        /// Resultado da validação via spec Cypress em modo batch contra o Pollux. <c>null</c> =
        /// ainda não avaliado (a spec Cypress em modo batch ainda não existe) — tratar como
        /// "pendente", nunca como falha.
        /// </summary>
        public bool? CypressValidado { get; set; }

        /// <summary>Código <c>cStat</c> retornado pela SEFAZ-fake/Pollux, quando <see cref="CypressValidado"/> existir. <c>null</c> até lá.</summary>
        public string? CStatPollux { get; set; }

        /// <summary>
        /// <c>true</c> se o Ollama retornou saída utilizável para o caso (não indica qualidade da
        /// saída — um caso pode ter <c>Sucesso=true</c> e ainda assim métricas estruturais baixas).
        /// </summary>
        public bool Sucesso { get; set; }
    }

    /// <summary>
    /// Filtro de consulta do Endpoint 1 (GET /api/ai-metrics/generations). Todos os campos são
    /// opcionais; sem filtro, retorna a página mais recente primeiro.
    /// </summary>
    public class AiMetricsGenerationFilter
    {
        /// <summary>Página solicitada (1-based). Padrão 1.</summary>
        public int Page { get; set; } = 1;

        /// <summary>Itens por página. Padrão 20.</summary>
        public int PageSize { get; set; } = 20;

        /// <summary>Filtro parcial pelo caminho do layout, ex. <c>CTe</c>.</summary>
        public string? Layout { get; set; }

        /// <summary>Filtro pelo modelo Ollama usado na geração.</summary>
        public string? Modelo { get; set; }

        /// <summary>Filtro pelo resultado de sucesso/falha da geração.</summary>
        public bool? Sucesso { get; set; }

        /// <summary>Início do período (inclusivo).</summary>
        public DateTime? De { get; set; }

        /// <summary>Fim do período (inclusivo).</summary>
        public DateTime? Ate { get; set; }
    }

    /// <summary>
    /// Resultado paginado do Endpoint 1 (GET /api/ai-metrics/generations).
    /// </summary>
    public class PagedAiMetricsGenerationsResult
    {
        public bool Success { get; set; } = true;
        public int TotalCount { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public List<AiMetricsGeneration> Items { get; set; } = new();
    }

    /// <summary>
    /// Agregado por tipo de documento, usado dentro do resumo (Endpoint 2).
    /// </summary>
    public class AiMetricsDocTypeSummary
    {
        public string DocType { get; set; } = string.Empty;
        public int Total { get; set; }
        public int Sucesso { get; set; }
        public double TokensPorSegundoMedio { get; set; }
    }

    /// <summary>
    /// Resumo agregado (Endpoint 2 — GET /api/ai-metrics/summary).
    /// </summary>
    public class AiMetricsSummary
    {
        public bool Success { get; set; } = true;
        public int TotalGeracoes { get; set; }
        public int TotalSucesso { get; set; }
        public int TotalFalhas { get; set; }
        public double TokensPorSegundoMedio { get; set; }
        public double TagOverlapMedio { get; set; }
        public double TextSimilarityMedia { get; set; }

        // Ficam em 0 até validação XSD/Cypress-Pollux existirem no loop de runtime (ver handoff)
        // — não é erro, é "ainda não chegamos nessa etapa do loop gerar→validar".
        /// <summary>Total de gerações com <see cref="AiMetricsGeneration.XsdValido"/> = true. Fica em 0 até a validação XSD ser cabeada no job.</summary>
        public int TotalXsdValidado { get; set; }

        /// <summary>Total de gerações com <see cref="AiMetricsGeneration.CypressValidado"/> = true. Fica em 0 até a spec Cypress em modo batch existir.</summary>
        public int TotalCypressValidado { get; set; }

        /// <summary>Total de gerações cujo <see cref="AiMetricsGeneration.CStatPollux"/> indica autorização pela SEFAZ-fake/Pollux. Fica em 0 pelo mesmo motivo acima.</summary>
        public int TotalCStatAutorizado { get; set; }

        /// <summary>Quebra do resumo por tipo de documento (NFe/CTe/MDFe).</summary>
        public List<AiMetricsDocTypeSummary> PorDocType { get; set; } = new();

        /// <summary>Timestamp da geração mais recente no período — indica se o job cron ainda está vivo.</summary>
        public DateTime? UltimaRodada { get; set; }
    }
}
