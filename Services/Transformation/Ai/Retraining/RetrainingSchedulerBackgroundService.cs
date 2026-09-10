using Microsoft.Extensions.Options;

namespace LayoutParserApi.Services.Transformation.Ai.Retraining
{
    /// <summary>
    /// Avaliação periódica do gatilho de retraining (F4.2, issue #351). Timer previsível e
    /// independente de tráfego — mesmo padrão de <see cref="AiCandidateStoreCleanupBackgroundService"/>.
    /// O trabalho pesado (disparar/detectar) é todo do <see cref="IRetrainingCoordinator"/>; aqui
    /// só há o laço e o intervalo.
    ///
    /// <para>O incremento do contador NÃO passa por aqui — vem do hook de F3
    /// (<see cref="TrainingDataCaptureService"/>) a cada convergência real. Este serviço só decide
    /// "já é hora de treinar?" e detecta a conclusão de um treino anterior.</para>
    /// </summary>
    public class RetrainingSchedulerBackgroundService : BackgroundService
    {
        private readonly ILogger<RetrainingSchedulerBackgroundService> _logger;
        private readonly IRetrainingCoordinator _coordinator;
        private readonly TimeSpan _interval;

        public RetrainingSchedulerBackgroundService(
            ILogger<RetrainingSchedulerBackgroundService> logger,
            IRetrainingCoordinator coordinator,
            IOptions<RetrainingOptions> options)
        {
            _logger = logger;
            _coordinator = coordinator;

            var hours = options.Value.EvaluationIntervalHours;
            _interval = TimeSpan.FromHours(
                hours > 0 ? hours : RetrainingOptions.DefaultEvaluationIntervalHours);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _logger.LogInformation(
                "Scheduler de retraining ativo (avaliação a cada {IntervaloHoras}h)", _interval.TotalHours);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _coordinator.EvaluateAndMaybeTriggerAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Falha no ciclo de avaliação do gatilho de retraining");
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
    }
}
