using Microsoft.Extensions.Options;

using LayoutParserApi.Services.Interfaces;

namespace LayoutParserApi.Services.Transformation.Ai
{
    /// <summary>
    /// Job periódico de geração automática de TCL/XSL/XSLT (issue #473, fase 2 do trigger lazy #438 —
    /// ADR <c>docs/architecture/adr-geracao-automatica-gabarito-sysmiddle.md</c> §3/§4/§6). Varre o
    /// catálogo <c>tbMapper</c> (somente leitura, <c>172.31.249.51</c>) periodicamente e dispara geração
    /// para mappers sem candidato ou com candidato <c>stale</c>, para que o time de operações já veja
    /// pronto ao abrir o painel — sem depender de ser o primeiro a pedir.
    ///
    /// <para><b>Reaproveitamento deliberado (Protocolo IDS):</b> a decisão real de
    /// none/generating/ready/stale e o disparo em si continuam 100% em
    /// <see cref="IGeneratedMapperArtifactService.GetOrTriggerAsync"/> — mesmo <see cref="GeneratedMapperArtifactService.ComputeMapperVoHash"/>
    /// e mesmo lock por mapper (<see cref="IGeneratedMapperArtifactStore.TryBeginGeneratingAsync"/>) do
    /// trigger lazy. Este serviço só decide QUAIS mappers examinar em cada rodada (catálogo menos os já
    /// em geração, priorizados por recência, limitados por rodada) — não duplica o cálculo de hash nem
    /// reintroduz o sidecar de proveniência do desenho original do ADR (já substituído em #459).</para>
    /// </summary>
    public sealed class GeneratedMapperArtifactSweepService : BackgroundService
    {
        private readonly ILogger<GeneratedMapperArtifactSweepService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly TimeSpan _interval;
        private readonly int _maxMappersPerRound;

        public GeneratedMapperArtifactSweepService(
            ILogger<GeneratedMapperArtifactSweepService> logger,
            IServiceScopeFactory scopeFactory,
            IOptions<GeneratedMapperSweepOptions> options)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;

            var hours = options.Value.IntervalHours > 0
                ? options.Value.IntervalHours
                : GeneratedMapperSweepOptions.DefaultIntervalHours;
            _interval = TimeSpan.FromHours(hours);

            _maxMappersPerRound = options.Value.MaxMappersPerRound > 0
                ? options.Value.MaxMappersPerRound
                : GeneratedMapperSweepOptions.DefaultMaxMappersPerRound;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Cede o startup: não concorre com warm-up de cache/conexões (mesmo padrão de
            // FiscalAnalysisPurgeBackgroundService).
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _logger.LogInformation(
                "Varredura periódica de geração automática de TCL/XSL/XSLT ativa (a cada {IntervalHours}h, máx. {MaxPorRodada} mapper(es)/rodada)",
                _interval.TotalHours, _maxMappersPerRound);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunSweepOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Resiliência (dotnet-standards.md): SQL/Ollama podem falhar — nunca derruba o host
                    // nem interrompe o loop; tenta de novo na próxima rodada.
                    _logger.LogWarning(ex, "Falha na varredura periódica de geração automática de TCL/XSL/XSLT — tentando de novo na próxima rodada");
                }

                try
                {
                    await Task.Delay(_interval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Executa uma rodada da varredura. Público (não só <c>protected override ExecuteAsync</c>) para
        /// testabilidade — chamado em loop por <see cref="ExecuteAsync"/>, mas testável isoladamente com
        /// um <see cref="IServiceScopeFactory"/> de fakes.
        /// </summary>
        public async Task RunSweepOnceAsync(CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var mapperService = scope.ServiceProvider.GetRequiredService<ICachedMapperService>();
            var store = scope.ServiceProvider.GetRequiredService<IGeneratedMapperArtifactStore>();
            var artifactService = scope.ServiceProvider.GetRequiredService<IGeneratedMapperArtifactService>();

            var catalog = await mapperService.GetAllMappersAsync();
            if (catalog.Count == 0)
            {
                _logger.LogDebug("Varredura periódica: catálogo tbMapper vazio — nada a fazer.");
                return;
            }

            // Só precisamos saber QUEM já está em geração (persistido) para não disparar de novo —
            // o restante (none/stale/ready) é decidido por GetOrTriggerAsync mapper a mapper, que já
            // faz o hash check. ListAsync evita N idas ao banco (1 GetAsync por mapper).
            var (generatingRecords, _) = await store.ListAsync(
                GeneratedMapperArtifactStatus.Generating, skip: 0, take: int.MaxValue, cancellationToken);
            var generatingGuids = generatingRecords
                .Select(r => r.MapperGuid)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var candidates = SelectCandidatesForSweep(catalog, generatingGuids, _maxMappersPerRound);
            if (candidates.Count == 0)
            {
                _logger.LogDebug("Varredura periódica: nenhum mapper candidato nesta rodada (todos já em geração, ou catálogo vazio).");
                return;
            }

            _logger.LogInformation("Varredura periódica examinando {Count} mapper(es) do catálogo tbMapper nesta rodada.", candidates.Count);

            var dispatched = 0;
            foreach (var mapper in candidates)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                var safeMapperGuid = Services.Logging.LogMessageSanitizer.Sanitize(mapper.MapperGuid);
                var correlationId = $"sweep-{Guid.NewGuid():N}";
                try
                {
                    var response = await artifactService.GetOrTriggerAsync(mapper.MapperGuid, correlationId, cancellationToken);
                    if (response?.Status == GeneratedMapperArtifactStatus.Generating)
                        dispatched++;
                }
                catch (Exception ex)
                {
                    // Resiliência: uma falha isolada (mapper malformado, Ollama fora do ar propagando
                    // exceção não tratada em algum ponto do fluxo) nunca derruba a rodada inteira — segue
                    // para o próximo mapper.
                    _logger.LogWarning(ex,
                        "Falha ao avaliar/disparar geração automática para o mapper {MapperGuid} na varredura periódica — seguindo para o próximo.",
                        safeMapperGuid);
                }
            }

            if (dispatched > 0)
                _logger.LogInformation("Varredura periódica disparou/confirmou geração em andamento para {Dispatched} mapper(es).", dispatched);
        }

        /// <summary>
        /// Lógica pura (sem I/O, testável com fakes): candidatos = catálogo MENOS os já em geração
        /// persistida, priorizados por mais recente primeiro (<see cref="LayoutParserApi.Models.Entities.Mapper.LastUpdateDate"/> —
        /// <c>tbMapper</c> não expõe uma data de criação separada; usamos a de última atualização como
        /// proxy de "recém-adicionado/alterado no catálogo", em linha com o pedido do ADR §3 de priorizar
        /// mappers recentes), limitados a <paramref name="maxPerRound"/> por rodada.
        ///
        /// <para>A decisão real de none/stale/ready (que exige decifrar e fazer hash do <c>MapperVo</c>)
        /// fica deliberadamente FORA desta função — é responsabilidade de
        /// <see cref="IGeneratedMapperArtifactService.GetOrTriggerAsync"/>, chamado pelo chamador para
        /// cada candidato retornado aqui.</para>
        /// </summary>
        public static IReadOnlyList<LayoutParserApi.Models.Entities.Mapper> SelectCandidatesForSweep(
            IReadOnlyList<LayoutParserApi.Models.Entities.Mapper> catalog,
            IReadOnlySet<string> generatingMapperGuids,
            int maxPerRound)
        {
            if (catalog is null || catalog.Count == 0 || maxPerRound <= 0)
                return Array.Empty<LayoutParserApi.Models.Entities.Mapper>();

            return catalog
                .Where(m => !string.IsNullOrWhiteSpace(m.MapperGuid)
                    && !(generatingMapperGuids?.Contains(m.MapperGuid) ?? false))
                .OrderByDescending(m => m.LastUpdateDate)
                .Take(maxPerRound)
                .ToList();
        }
    }
}
