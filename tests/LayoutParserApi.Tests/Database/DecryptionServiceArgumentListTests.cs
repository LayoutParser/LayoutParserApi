using System.Diagnostics;

using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.Logging;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Database
{
    /// <summary>
    /// Fecha o mesmo <c>cs/command-line-injection</c> (CodeQL) tratado em
    /// <c>LowCodeTransformationServiceArgumentListTests</c>, agora para
    /// <c>DecryptionService.CallLegacyDecryptorAsync</c>: antes do fix, <c>ProcessStartInfo.Arguments</c>
    /// era montado como UMA string concatenada (<c>BuildArgs</c>, com aspas manuais em torno de
    /// <c>inputFile</c>/<c>outputFile</c>/<c>corr</c>/<c>_logDirFromApi</c>) — <c>corr</c> vem do
    /// <c>CorrelationContext</c> (propagado a partir do request HTTP) e os paths de temp file, embora
    /// gerados pela própria API, ainda passavam pelo mesmo parsing de shell vulnerável.
    ///
    /// <para>A correção troca a montagem para <see cref="ProcessStartInfo.ArgumentList"/>: cada
    /// argumento vira um item da lista, sem re-interpretação de shell. Este teste trava que um valor
    /// malicioso chega ao processo filho como UM ÚNICO argumento literal — não fatiado em comandos
    /// separados — e que <c>Arguments</c> (string) permanece vazio.</para>
    /// </summary>
    public class DecryptionServiceArgumentListTests
    {
        [Theory]
        [InlineData("qualquer-corr; rm -rf /")]
        [InlineData("qualquer-corr\" && calc.exe")]
        [InlineData("qualquer-corr & del C:\\*.* /f /q")]
        [InlineData("qualquer-corr`whoami`")]
        public async Task CorrelationId_malicioso_chega_como_argumento_literal_unico(string corrMalicioso)
        {
            var svc = CriarServicoComCaptura();

            // CorrelationContext.CurrentId é AsyncLocal — setar antes do await propaga pro fluxo async.
            CorrelationContext.CurrentId = corrMalicioso;
            try
            {
                await svc.DecryptContentAsync("conteudo-cifrado-qualquer");
            }
            finally
            {
                CorrelationContext.CurrentId = null;
            }

            Assert.NotNull(svc.PsiCapturado);
            Assert.Contains(corrMalicioso, svc.PsiCapturado!.ArgumentList);

            // A montagem antiga (Arguments = string única via BuildArgs) não pode voltar.
            Assert.True(string.IsNullOrEmpty(svc.PsiCapturado.Arguments));
        }

        [Fact]
        public async Task Paths_de_temp_file_chegam_como_argumentos_literais_separados()
        {
            var svc = CriarServicoComCaptura();

            await svc.DecryptContentAsync("conteudo-cifrado-qualquer");

            Assert.NotNull(svc.PsiCapturado);
            // input, output, corr, logDir — quatro itens distintos no ArgumentList, nenhum
            // concatenado com aspas manuais numa string só.
            Assert.Equal(4, svc.PsiCapturado!.ArgumentList.Count);
            Assert.True(string.IsNullOrEmpty(svc.PsiCapturado.Arguments));
        }

        // ─────────────────────────────── infraestrutura ───────────────────────────────

        private static ServicoComCaptura CriarServicoComCaptura()
        {
            var executavelFake = Path.Combine(Path.GetTempPath(), "lp-tests", "decrypt-arglist", "LayoutParserDecrypt.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(executavelFake)!);
            File.WriteAllBytes(executavelFake, Array.Empty<byte>());

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["LayoutParserDecrypt:Path"] = executavelFake,
                    ["Logging:File:Directory"] = Path.Combine(Path.GetTempPath(), "lp-tests", "decrypt-arglist-logs")
                })
                .Build();

            return new ServicoComCaptura(NullLogger<DecryptionService>.Instance, config);
        }

        /// <summary>
        /// Substitui só o ciclo de vida do processo externo para capturar o <see cref="ProcessStartInfo"/>
        /// real construído por <c>CallLegacyDecryptorAsync</c>, sem depender do <c>.exe</c> legado
        /// (o arquivo fake em <see cref="CriarServicoComCaptura"/> só precisa existir para passar em
        /// <c>IsDecryptorAvailable</c> — nunca é executado de verdade).
        /// </summary>
        private sealed class ServicoComCaptura : DecryptionService
        {
            public ProcessStartInfo? PsiCapturado { get; private set; }

            public ServicoComCaptura(ILogger<DecryptionService> logger, IConfiguration configuration)
                : base(logger, configuration)
            {
            }

            protected override Task<(int ExitCode, string Stdout, string Stderr)> ExecuteDecryptorProcessAsync(
                ProcessStartInfo processStartInfo,
                string correlationId)
            {
                PsiCapturado = processStartInfo;
                return Task.FromResult((0, string.Empty, string.Empty));
            }
        }
    }
}
