using LayoutParserApi.Services.Transformation.LowCode;

namespace LayoutParserApi.Tests.Transformation
{
    /// <summary>
    /// Trava os defaults de <see cref="LowCodeRunnerOptions"/> que precisam ser seguros SOZINHOS.
    ///
    /// <para>Motivo de existirem: o deploy preserva o <c>appsettings.json</c> do destino, então o
    /// valor do repositório não chega ao servidor. Quando o canal de variável de ambiente falha, o
    /// default do código é o que roda em produção — ele é a última linha de defesa, não uma
    /// formalidade. O default de 15s vinha de medir o bootstrap do runner (~1s) em vez da
    /// transformação (48-137s medidos), e matava todo trabalho no meio.</para>
    /// </summary>
    public class LowCodeRunnerOptionsDefaultsTests
    {
        /// <summary>
        /// 60s é o piso empírico: só o <c>ExecuteMapper</c> leva 38-73s na medição A/B por fase.
        /// Qualquer default abaixo disso mata a transformação no meio de forma sistemática.
        /// </summary>
        [Fact]
        public void Timeout_do_runner_cobre_a_transformacao_real_medida()
        {
            var opt = new LowCodeRunnerOptions();

            Assert.True(opt.RunnerTimeoutSeconds >= 60,
                $"RunnerTimeoutSeconds default = {opt.RunnerTimeoutSeconds}s. A transformação real leva 48-137s " +
                "medidos (ExecuteMapper sozinho 38-73s); abaixo de 60s o runner é morto no meio SEMPRE.");
        }

        /// <summary>
        /// O teto da request precisa ser menor que o do motor: são perguntas diferentes. Se alguém
        /// igualar os dois, o cliente HTTP volta a esperar o pior caso do runner.
        /// </summary>
        [Fact]
        public void Teto_da_request_de_candidatos_e_menor_que_o_teto_do_runner()
        {
            var opt = new LowCodeRunnerOptions();

            Assert.True(opt.CandidatesRequestTimeoutSeconds > 0);
            Assert.True(opt.CandidatesRequestTimeoutSeconds < opt.RunnerTimeoutSeconds,
                "O teto da request deve ser menor que o do runner — o cliente não espera o pior caso do motor.");
        }

        /// <summary>
        /// Mesma relação, um nível acima: a entrega síncrona dentro do parse é o teto mais apertado
        /// de todos, porque ali a resposta principal é o documento parseado, não a transformação.
        /// </summary>
        [Fact]
        public void Entrega_sincrona_no_parse_e_o_teto_mais_apertado()
        {
            var opt = new LowCodeRunnerOptions();

            Assert.True(opt.SyncDeliveryTimeoutSeconds < opt.CandidatesRequestTimeoutSeconds);
            Assert.True(opt.SyncDeliveryTimeoutSeconds < opt.RunnerTimeoutSeconds);
        }

        /// <summary>
        /// <c>Package</c> segue com default vazio DE PROPÓSITO: é identificador de instância, não tem
        /// valor universal correto, e chutar um levaria a "mapeador não encontrado" longe da causa.
        /// O contrato é falhar explícito (runner sai com exit 9) — este teste trava essa decisão para
        /// que ninguém "conserte" o vazio inventando um default.
        /// </summary>
        [Fact]
        public void Package_permanece_sem_default_para_falhar_explicito()
        {
            var opt = new LowCodeRunnerOptions();

            Assert.Equal(string.Empty, opt.Package);
        }
    }
}
