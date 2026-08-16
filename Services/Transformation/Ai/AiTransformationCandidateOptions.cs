namespace LayoutParserApi.Services.Transformation.Ai
{
    /// <summary>
    /// Configuração do pathway IA de <c>execute-candidates</c> (seção <c>AiTransformationCandidate</c>
    /// do appsettings). Ver docs/architecture/pathway-ia-execute-candidates.md §6 — "sem SLA de
    /// produto" para o loop em si, mas com teto de sanidade técnica para não vazar jobs "running"
    /// eternamente na store.
    /// </summary>
    public class AiTransformationCandidateOptions
    {
        /// <summary>
        /// Retenção default de um ticket na store (issue #51). Escolha de 72h: o job em si morre em
        /// no máximo <see cref="SanityTimeoutMinutes"/> (45min), então nenhum ticket vivo é atingido;
        /// o que sobra é valor de diagnóstico ("por que o job de ontem falhou?"), e 3 dias cobrem a
        /// janela de um ticket criado na sexta à noite e triado na segunda de manhã. Acima disso é
        /// auditoria de longo prazo — papel do log estruturado (Serilog), não desta store.
        /// </summary>
        public const int DefaultTicketTtlHours = 72;

        /// <summary>Intervalo default entre varreduras de limpeza (issue #51).</summary>
        public const int DefaultCleanupIntervalMinutes = 60;

        /// <summary>Máximo de iterações do loop gerar → aplicar → diff → validar → corrigir.</summary>
        public int MaxIterations { get; set; } = 3;

        /// <summary>
        /// Teto de sanidade técnica (não é o timeout de produto, que o dono do projeto disse
        /// explicitamente não querer agora — §2.3 do desenho). Só evita job "running" para sempre
        /// se o Ollama travar/morrer no meio do loop.
        /// </summary>
        public int SanityTimeoutMinutes { get; set; } = 45;

        /// <summary>Onde persistir o job por ticket (padrão: MLData/AiTransformationCandidates).</summary>
        public string? StorePath { get; set; }

        /// <summary>
        /// TTL do ticket na store, em horas (issue #51 — sem isso a store crescia indefinidamente
        /// em memória E em disco). Vale para as duas camadas e é <b>absoluto a partir da última
        /// escrita</b>, não deslizante por leitura: polling de status não mantém um ticket vivo
        /// para sempre. Valor &lt;= 0 cai no default (<see cref="DefaultTicketTtlHours"/>), mesma
        /// convenção de <see cref="MaxIterations"/>/<see cref="SanityTimeoutMinutes"/>.
        /// </summary>
        public int TicketTtlHours { get; set; } = DefaultTicketTtlHours;

        /// <summary>
        /// Intervalo entre varreduras do <c>AiCandidateStoreCleanupBackgroundService</c>, em minutos.
        /// Valor &lt;= 0 cai no default (<see cref="DefaultCleanupIntervalMinutes"/>).
        /// </summary>
        public int CleanupIntervalMinutes { get; set; } = DefaultCleanupIntervalMinutes;

        /// <summary>
        /// Fallback automático de IA (Estado A — "não encontrado/não modelado", ver
        /// docs/architecture/design-fallback-ia-automatico-2026-08-16.md §5): cooldown do
        /// <see cref="IAiFallbackSuppressionGate"/> por <c>LayoutGuid</c> após uma tentativa sem
        /// sucesso. Default 240min (4h) — folgado o bastante para não bloquear retentativa manual
        /// no mesmo dia, curto o bastante para não travar indefinidamente.
        /// </summary>
        public int CooldownMinutes { get; set; } = 240;

        /// <summary>
        /// Teto de iterações do loop no modo "sem gabarito" (Estado A, §6 do desenho) — mais
        /// conservador que <see cref="MaxIterations"/> porque, sem diff canônico para convergir,
        /// iterações extras tendem a não melhorar a confiança do resultado, só o custo.
        /// </summary>
        public int MaxIterationsFallback { get; set; } = 2;
    }
}
