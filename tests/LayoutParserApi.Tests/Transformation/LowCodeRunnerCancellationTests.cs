using System.Diagnostics;

using LayoutParserApi.Services.Transformation.LowCode;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Tests.Transformation
{
    /// <summary>
    /// Disciplina de slot do runner low-code (<c>spec-entrega-da-transformacao-no-parse.md</c> §1.1).
    ///
    /// <para>O defeito: <c>WaitAsync()</c> sem token e <c>TransformAsync</c> sem
    /// <c>CancellationToken</c>. Quando o <c>ParseController</c> desistia no teto de 6s, o trabalho
    /// abandonado continuava vivo <b>segurando um dos <c>MaxConcurrentRunners</c></b> — o gargalo é
    /// o slot, e nenhum cache resolve isso.</para>
    ///
    /// <para>O que estes testes exercitam é o código real (semáforo, token, liberação no
    /// <c>finally</c>); só o <c>Process.Start</c> do <c>.exe</c> x86 é substituído — ele não existe
    /// na máquina de teste, e não é ele que está sob julgamento.</para>
    /// </summary>
    public class LowCodeRunnerCancellationTests
    {
        [Fact]
        public async Task Cancelar_no_teto_libera_o_slot_para_o_proximo_upload()
        {
            // MaxConcurrentRunners = 1: com um slot só, "o próximo entrou" é prova direta de que o
            // anterior devolveu o recurso.
            var runner = CriarRunner(maxConcorrentes: 1);

            using var ctsPrimeiro = new CancellationTokenSource();
            var primeiro = runner.TransformAsync("doc", mapperId: "M1", cancellationToken: ctsPrimeiro.Token);
            await EsperarAsync(() => runner.Entradas >= 1, "o primeiro upload nunca entrou no runner");

            using var ctsSegundo = new CancellationTokenSource();
            var segundo = runner.TransformAsync("doc", mapperId: "M2", cancellationToken: ctsSegundo.Token);

            // Enquanto o primeiro segura o slot, o segundo fica na fila — é o estado que hoje dura
            // até o runner do abandonado morrer sozinho.
            await Task.Delay(150);
            Assert.Equal(1, runner.Entradas);

            // Teto síncrono estourou:
            ctsPrimeiro.Cancel();
            await DeveCancelarAsync(primeiro, "o primeiro nao observou o cancelamento");

            // ...e o slot volta para a fila.
            await EsperarAsync(() => runner.Entradas >= 2, "o slot nao foi liberado apos o cancelamento");

            ctsSegundo.Cancel();
            await DeveCancelarAsync(segundo, "o segundo nao observou o cancelamento");
        }

        [Fact]
        public async Task Cancelar_na_fila_nao_consome_slot_nem_deixa_o_chamador_pendurado()
        {
            var runner = CriarRunner(maxConcorrentes: 1);

            using var ctsPrimeiro = new CancellationTokenSource();
            var primeiro = runner.TransformAsync("doc", mapperId: "M1", cancellationToken: ctsPrimeiro.Token);
            await EsperarAsync(() => runner.Entradas >= 1, "o primeiro upload nunca entrou no runner");

            using var ctsSegundo = new CancellationTokenSource();
            var segundo = runner.TransformAsync("doc", mapperId: "M2", cancellationToken: ctsSegundo.Token);
            await Task.Delay(100);

            // Quem desistiu enquanto esperava vaga sai da fila NA HORA (é o WaitAsync(token)) — sem
            // isso, ele acordaria depois e tomaria um slot para produzir trabalho que ninguém quer.
            ctsSegundo.Cancel();

            await DeveCancelarAsync(segundo, "o cancelamento na fila do semaforo nao foi observado", segundos: 3);

            // E nunca chegou a entrar no runner.
            Assert.Equal(1, runner.Entradas);

            ctsPrimeiro.Cancel();
            await DeveCancelarAsync(primeiro, "o primeiro nao observou o cancelamento");
        }

        // ─────────────────────────────── infraestrutura ───────────────────────────────

        private static RunnerBloqueado CriarRunner(int maxConcorrentes)
        {
            // Nunca aponte log de teste para o diretório de produção: tudo em pasta temporária.
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Logging:File:Directory"] = Path.Combine(Path.GetTempPath(), "lp-tests", "runner-logs")
                })
                .Build();

            var opcoes = Options.Create(new LowCodeRunnerOptions
            {
                // Caminhos só precisam ser não-vazios: o processo nunca é iniciado (ver RunnerBloqueado).
                RunnerPath = "runner-inexistente.exe",
                SysmiddleDir = Path.GetTempPath(),
                GlobalFolder = Path.GetTempPath(),
                MaxConcurrentRunners = maxConcorrentes,
                // Alto de propósito: quem tem de encerrar a espera é o CANCELAMENTO, não o timeout —
                // se este teste passasse por timeout, não provaria nada sobre o token.
                RunnerTimeoutSeconds = 600
            });

            return new RunnerBloqueado(opcoes, config);
        }

        /// <summary>
        /// Espera o cancelamento com teto. O teto não é decoração: sob o defeito que este arquivo
        /// vigia (token que não chega ao runner), a task NUNCA completa — e um teste que trava é
        /// pior que um teste que falha, porque a suíte inteira fica pendurada sem diagnóstico.
        /// </summary>
        private static async Task DeveCancelarAsync(Task tarefa, string mensagemDeFalha, int segundos = 5)
        {
            if (await Task.WhenAny(tarefa, Task.Delay(TimeSpan.FromSeconds(segundos))) != tarefa)
                Assert.Fail(mensagemDeFalha);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tarefa);
        }

        /// <summary>
        /// Teto de 10s (mesmo valor usado pelo <c>PollUntilAsync</c> de
        /// <c>AiTransformationCandidateServiceTests</c>) — 5s era curto demais para CI sob carga:
        /// a asserção depende só de agendamento de continuação (semáforo + <c>Task.Run</c> da
        /// entrada no runner), não de nenhum I/O real, mas em runner de CI compartilhado o
        /// thread pool pode demorar a atender sob concorrência de toda a suíte (482 testes),
        /// gerando falso negativo sem que o comportamento real (liberação do slot) esteja quebrado.
        /// </summary>
        private static async Task EsperarAsync(Func<bool> condicao, string mensagemDeFalha)
        {
            var limite = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < limite)
            {
                if (condicao())
                    return;
                await Task.Delay(20);
            }

            Assert.Fail(mensagemDeFalha);
        }

        /// <summary>
        /// Substitui só o ciclo de vida do processo externo: entra, sinaliza e fica preso até ser
        /// cancelado. Tudo o que interessa ao teste — semáforo, token, <c>finally</c> que solta o
        /// slot — continua sendo o código de produção.
        /// </summary>
        private sealed class RunnerBloqueado : LowCodeTransformationService
        {
            private int _entradas;

            public RunnerBloqueado(IOptions<LowCodeRunnerOptions> opcoes, IConfiguration config)
                : base(NullLogger<LowCodeTransformationService>.Instance, opcoes, config)
            {
            }

            public int Entradas => Volatile.Read(ref _entradas);

            protected override async Task<LowCodeRunnerExecution> ExecuteRunnerProcessAsync(
                ProcessStartInfo psi,
                int timeoutSeconds,
                string correlationId,
                string? mapperId,
                string? mapperName,
                CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _entradas);
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return default;
            }
        }
    }
}
