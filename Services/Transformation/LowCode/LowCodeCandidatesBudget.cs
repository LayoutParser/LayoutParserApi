namespace LayoutParserApi.Services.Transformation.LowCode
{
    /// <summary>
    /// Calcula o teto de tempo da request de multi-candidato
    /// (<c>POST /api/transformation-execution/execute-candidates</c>).
    ///
    /// <para>Separa duas perguntas que o controller antes respondia com um número só:
    /// <b>quanto o trabalho pode demorar</b> e <b>quanto o cliente HTTP pode esperar</b>. A fórmula
    /// anterior — <c>RunnerTimeoutSeconds * MaxConcurrentRunners</c> — errava as duas: com o timeout
    /// do runner corrigido para 180s ela pedia 360s de espera do cliente, e <b>crescia</b> ao se
    /// aumentar <c>MaxConcurrentRunners</c>, quando mais slots deveriam encurtar a fila e não
    /// alongar o teto.</para>
    ///
    /// <para>Mora aqui, e não inline no controller, pelo mesmo motivo de
    /// <see cref="LowCodeTransformationEligibility"/>: o controller arrasta banco, pipeline e o
    /// runner x86 e não é testável; este arquivo é puro e roda na suíte.</para>
    /// </summary>
    public static class LowCodeCandidatesBudget
    {
        /// <summary>Teto de request usado quando a configuração vem inválida (zero ou negativa).</summary>
        public const int DefaultRequestTimeoutSeconds = 90;

        /// <summary>
        /// Devolve o budget em segundos e as parcelas que o produziram (para log).
        /// </summary>
        /// <param name="multiCandidateTopN">Teto de candidatos disparados (<c>LowCode:MultiCandidateTopN</c>).</param>
        /// <param name="maxConcurrentRunners">Slots simultâneos do runner (<c>LowCode:MaxConcurrentRunners</c>).</param>
        /// <param name="runnerTimeoutSeconds">Teto por invocação do runner (<c>LowCode:RunnerTimeoutSeconds</c>).</param>
        /// <param name="candidatesRequestTimeoutSeconds">Teto absoluto da request (<c>LowCode:CandidatesRequestTimeoutSeconds</c>).</param>
        public static LowCodeCandidatesBudgetResult Calculate(
            int multiCandidateTopN,
            int maxConcurrentRunners,
            int runnerTimeoutSeconds,
            int candidatesRequestTimeoutSeconds)
        {
            // Cada onda ocupa todos os slots; o pior caso é a última onda estourar o timeout do runner.
            // Os Max(1, ...) tratam config inválida (0/negativa) como "pelo menos uma unidade" em vez de
            // dividir por zero ou zerar o budget — degradar, nunca derrubar.
            var slots = Math.Max(1, maxConcurrentRunners);
            var candidatos = Math.Max(1, multiCandidateTopN);
            var ondas = (int)Math.Ceiling(candidatos / (double)slots);

            var budgetTrabalho = ondas * Math.Max(1, runnerTimeoutSeconds);

            var tetoRequest = candidatesRequestTimeoutSeconds > 0
                ? candidatesRequestTimeoutSeconds
                : DefaultRequestTimeoutSeconds;

            return new LowCodeCandidatesBudgetResult
            {
                Ondas = ondas,
                BudgetTrabalhoSeconds = budgetTrabalho,
                TetoRequestSeconds = tetoRequest,
                // O teto do cliente NUNCA é ultrapassado; quando o trabalho cabe em menos, vale o menor.
                EffectiveSeconds = Math.Min(budgetTrabalho, tetoRequest)
            };
        }
    }

    /// <summary>Budget efetivo e as parcelas que o formaram — as parcelas existem para o log explicar o número.</summary>
    public class LowCodeCandidatesBudgetResult
    {
        /// <summary>Quantas ondas de execução paralela o teto de candidatos exige.</summary>
        public int Ondas { get; set; }

        /// <summary>Pior caso plausível do trabalho: ondas x timeout do runner.</summary>
        public int BudgetTrabalhoSeconds { get; set; }

        /// <summary>Teto absoluto da request HTTP, independente do trabalho.</summary>
        public int TetoRequestSeconds { get; set; }

        /// <summary>O que efetivamente vale: o menor entre trabalho e teto de request.</summary>
        public int EffectiveSeconds { get; set; }
    }
}
