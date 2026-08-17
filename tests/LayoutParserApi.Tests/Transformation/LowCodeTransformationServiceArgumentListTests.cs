using System.Diagnostics;

using LayoutParserApi.Services.Transformation.LowCode;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LayoutParserApi.Tests.Transformation
{
    /// <summary>
    /// Fecha o <c>cs/command-line-injection</c> apontado pelo CodeQL (triagem
    /// <c>docs/architecture/triagem-codeql-166-alertas-2026-08-16.md</c>): antes deste fix,
    /// <c>LowCodeTransformationService.TransformAsync</c> montava <c>ProcessStartInfo.Arguments</c>
    /// como UMA string concatenada (<c>string.Join(" ", args)</c>) com um <c>Quote()</c> manual
    /// fraco — <c>mapperId</c>/<c>fileName</c>/<c>package</c> vêm (direta ou indiretamente) do
    /// request HTTP.
    ///
    /// <para>A correção troca a montagem para <see cref="ProcessStartInfo.ArgumentList"/>: cada
    /// argumento vira um item da lista, passado direto pro processo filho sem re-interpretação de
    /// shell — não há mais parsing de string a ser escapado/injetado. Este teste trava que um
    /// valor malicioso chega ao runner como UM ÚNICO argumento literal, e não como comandos
    /// separados.</para>
    /// </summary>
    public class LowCodeTransformationServiceArgumentListTests
    {
        [Theory]
        [InlineData("MAP_1; rm -rf /")]
        [InlineData("MAP_1\" && calc.exe")]
        [InlineData("MAP_1 & del C:\\*.* /f /q")]
        [InlineData("MAP_1`whoami`")]
        public async Task MapperId_malicioso_chega_como_argumento_literal_unico(string mapperIdMalicioso)
        {
            var runner = CriarRunnerComCaptura();

            // O runner de teste não gera outputFile de verdade (não roda .exe nenhum) — o que
            // importa aqui é o ProcessStartInfo capturado ANTES dessa falha esperada, não o
            // resultado da transformação.
            await Assert.ThrowsAnyAsync<Exception>(() => runner.TransformAsync("conteudo", mapperId: mapperIdMalicioso));

            Assert.NotNull(runner.PsiCapturado);

            // O valor malicioso tem que aparecer INTEIRO, como um item só do ArgumentList — não
            // fatiado em múltiplos argumentos por causa de espaço/aspas/&&/;.
            Assert.Contains(mapperIdMalicioso, runner.PsiCapturado!.ArgumentList);

            // E a montagem antiga (Arguments = string única) não pode voltar: ArgumentList vazio
            // usado junto de Arguments preenchido seria o sinal de regressão.
            Assert.True(string.IsNullOrEmpty(runner.PsiCapturado.Arguments));
        }

        [Fact]
        public async Task FileName_malicioso_chega_como_argumento_literal_unico()
        {
            var runner = CriarRunnerComCaptura();
            const string fileNameMalicioso = "nota.txt\" & shutdown /s /t 0 & \"";

            await Assert.ThrowsAnyAsync<Exception>(() =>
                runner.TransformAsync("conteudo", mapperId: "MAP_1", fileName: fileNameMalicioso));

            Assert.NotNull(runner.PsiCapturado);
            Assert.Contains(fileNameMalicioso, runner.PsiCapturado!.ArgumentList);
        }

        // ─────────────────────────────── infraestrutura ───────────────────────────────

        private static RunnerComCaptura CriarRunnerComCaptura()
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Logging:File:Directory"] = Path.Combine(Path.GetTempPath(), "lp-tests", "runner-logs-arglist")
                })
                .Build();

            var opcoes = Options.Create(new LowCodeRunnerOptions
            {
                RunnerPath = "runner-inexistente.exe",
                SysmiddleDir = Path.GetTempPath(),
                GlobalFolder = Path.GetTempPath(),
                MaxConcurrentRunners = 1,
                RunnerTimeoutSeconds = 60
            });

            return new RunnerComCaptura(opcoes, config);
        }

        /// <summary>
        /// Substitui só o ciclo de vida do processo externo para capturar o <see cref="ProcessStartInfo"/>
        /// real construído por <c>TransformAsync</c>, sem depender do <c>.exe</c> x86 (inexistente na
        /// máquina de teste).
        /// </summary>
        private sealed class RunnerComCaptura : LowCodeTransformationService
        {
            public ProcessStartInfo? PsiCapturado { get; private set; }

            public RunnerComCaptura(IOptions<LowCodeRunnerOptions> opcoes, IConfiguration config)
                : base(NullLogger<LowCodeTransformationService>.Instance, opcoes, config)
            {
            }

            protected override Task<LowCodeRunnerExecution> ExecuteRunnerProcessAsync(
                ProcessStartInfo psi,
                int timeoutSeconds,
                string correlationId,
                string? mapperId,
                string? mapperName,
                CancellationToken cancellationToken)
            {
                PsiCapturado = psi;
                return Task.FromResult(new LowCodeRunnerExecution(0, "", ""));
            }
        }
    }
}
