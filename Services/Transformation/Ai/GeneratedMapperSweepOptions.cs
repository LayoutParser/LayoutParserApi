namespace LayoutParserApi.Services.Transformation.Ai
{
    /// <summary>
    /// Config do job periódico de geração automática de TCL/XSL/XSLT (issue #473, fase 2 do trigger
    /// lazy #438). Seção <c>GeneratedMapperSweep</c> do <c>appsettings.json</c>. <see cref="MaxConcurrency"/>
    /// é o limite ÚNICO compartilhado com o trigger lazy (<c>GeneratedMapperArtifactService</c>) — ver
    /// <see cref="GeneratedMapperGenerationLimiter"/>, ADR <c>adr-geracao-automatica-gabarito-sysmiddle.md</c> §3/§6.
    /// </summary>
    public sealed class GeneratedMapperSweepOptions
    {
        /// <summary>Cadência padrão da varredura, em horas — mapper Sysmiddle muda raramente (dias/semanas).</summary>
        public const int DefaultIntervalHours = 6;

        /// <summary>
        /// Teto padrão de mappers EXAMINADOS por rodada — evita avalanche de chamadas ao Ollama
        /// (CPU-only, produção) na primeira subida, quando o catálogo inteiro (~180 mappers) ainda
        /// não tem candidato. O restante do catálogo é examinado nas rodadas seguintes.
        /// </summary>
        public const int DefaultMaxMappersPerRound = 20;

        /// <summary>
        /// Concorrência máxima de geração simultânea — padrão "1-2 por vez" do ADR §3/§6, para não
        /// saturar o Ollama de produção (BRNDDAPPBLD01, CPU-only) somado ao trigger lazy.
        /// </summary>
        public const int DefaultMaxConcurrency = 2;

        /// <summary>A cada quantas horas a varredura roda. Default <see cref="DefaultIntervalHours"/> se ausente/inválido.</summary>
        public int IntervalHours { get; set; } = DefaultIntervalHours;

        /// <summary>Quantos mappers do catálogo são examinados por rodada. Default <see cref="DefaultMaxMappersPerRound"/> se ausente/inválido.</summary>
        public int MaxMappersPerRound { get; set; } = DefaultMaxMappersPerRound;

        /// <summary>
        /// Concorrência máxima de geração simultânea, COMPARTILHADA entre o job periódico e o trigger
        /// lazy (não são dois semáforos independentes). Default <see cref="DefaultMaxConcurrency"/> se ausente/inválido.
        /// </summary>
        public int MaxConcurrency { get; set; } = DefaultMaxConcurrency;
    }
}
