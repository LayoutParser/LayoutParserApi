namespace LayoutParserApi.Models.Logging
{
    /// <summary>
    /// Uma geração individual (linha "Geracao concluida." com Source=AiMetrics), já parseada e
    /// tipada. Ver contrato definitivo em
    /// docs/architecture/handoff-frontend-gap-3-painel-ia-metrics.md.
    /// </summary>
    public class AiMetricsGeneration
    {
        public string Layout { get; set; } = string.Empty;

        // ✅ Derivado no backend (primeiro segmento de Layout, ex. "CTe\2.00a\..." -> "CTe") —
        // o front-end não deve reimplementar esse parsing.
        public string DocType { get; set; } = string.Empty;

        public string Modelo { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public double TokensPorSegundo { get; set; }
        public int TamanhoPromptChars { get; set; }
        public double DuracaoSegundos { get; set; }
        public double SimilaridadeFewShot { get; set; }
        public double TagOverlapRatio { get; set; }
        public double TextSimilarityRatio { get; set; }

        // Pendentes até validação XSD/Cypress-Pollux existirem no loop (ver handoff) — "null"
        // literal no log até lá, não é erro de parse.
        public bool? XsdValido { get; set; }
        public bool? CypressValidado { get; set; }
        public string? CStatPollux { get; set; }

        public bool Sucesso { get; set; }
    }

    /// <summary>
    /// Filtro de consulta do Endpoint 1 (GET /api/ai-metrics/generations).
    /// </summary>
    public class AiMetricsGenerationFilter
    {
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 20;
        public string? Layout { get; set; }
        public string? Modelo { get; set; }
        public bool? Sucesso { get; set; }
        public DateTime? De { get; set; }
        public DateTime? Ate { get; set; }
    }

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
        public int TotalXsdValidado { get; set; }
        public int TotalCypressValidado { get; set; }
        public int TotalCStatAutorizado { get; set; }
        public List<AiMetricsDocTypeSummary> PorDocType { get; set; } = new();
        public DateTime? UltimaRodada { get; set; }
    }
}
