using Microsoft.Extensions.Options;

namespace LayoutParserApi.Services.Transformation.Ai
{
    /// <summary>
    /// Limite de concorrência ÚNICO para a geração automática de TCL/XSL/XSLT — compartilhado entre o
    /// trigger LAZY (<see cref="GeneratedMapperArtifactService.GetOrTriggerAsync"/>, issue #438) e o job
    /// PERIÓDICO (<see cref="GeneratedMapperArtifactSweepService"/>, issue #473).
    ///
    /// <para>ADR <c>docs/architecture/adr-geracao-automatica-gabarito-sysmiddle.md</c> §3/§6 é explícito:
    /// "limitar concorrência total (lazy + job) a um semáforo único no processo, não dois limites
    /// independentes" — senão a soma dos dois ainda satura o Ollama de produção (CPU-only,
    /// <c>BRNDDAPPBLD01</c>). Por isso este limitador é <c>Singleton</c> (não Scoped): um único
    /// <see cref="SemaphoreSlim"/> por processo, injetado nos dois pontos de disparo.</para>
    /// </summary>
    public sealed class GeneratedMapperGenerationLimiter : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;

        public GeneratedMapperGenerationLimiter(IOptions<GeneratedMapperSweepOptions> options)
        {
            var max = options.Value.MaxConcurrency > 0
                ? options.Value.MaxConcurrency
                : GeneratedMapperSweepOptions.DefaultMaxConcurrency;
            _semaphore = new SemaphoreSlim(max, max);
        }

        /// <summary>Aguarda uma vaga de geração. Chamador é responsável por <see cref="Release"/> em <c>finally</c>.</summary>
        public Task WaitAsync(CancellationToken cancellationToken) => _semaphore.WaitAsync(cancellationToken);

        public void Release() => _semaphore.Release();

        public void Dispose() => _semaphore.Dispose();
    }
}
