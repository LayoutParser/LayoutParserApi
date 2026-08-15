namespace LayoutParserApi.Services.Health
{
    /// <summary>
    /// Os TRÊS estados possíveis do warm-up do catálogo. Existem separados de propósito: "ainda
    /// aquecendo" e "aqueceu e veio vazio" respondem o mesmo HTTP (503), mas são problemas
    /// diferentes — o primeiro se resolve sozinho (retry da issue #67), o segundo exige
    /// intervenção (SQL/decryptor fora, catálogo realmente sem layouts).
    /// </summary>
    public enum CatalogWarmupStatus
    {
        /// <summary>Warm-up ainda não concluiu (1ª tentativa ou retry em andamento). <b>Transitório.</b></summary>
        Aquecendo = 0,

        /// <summary>Warm-up concluiu e o catálogo tem layouts (&gt; 0). Único estado saudável.</summary>
        Pronto = 1,

        /// <summary>Warm-up concluiu e o catálogo veio <b>VAZIO</b>. Definitivo — não é saudável.</summary>
        Vazio = 2
    }

    /// <summary>
    /// Estado compartilhado (Singleton) do resultado do warm-up do catálogo de layouts.
    ///
    /// <para>Existe para a sonda de <b>readiness</b> responder a pergunta que importa: "o warm-up
    /// rodou e o catálogo tem layouts?". Antes, com SQL/decryptor fora, o processo subia, o SCM
    /// reportava Running e o health devolvia 200 — o catálogo vazio só aparecia quando um usuário
    /// subia um documento (P1.3 do plano de segurança). O <see cref="CachePermanentWarmupBackgroundService"/>
    /// preenche este estado ao fim do warm-up; o <c>CatalogHealthCheck</c> o lê.</para>
    ///
    /// <para>Deliberadamente independente do Redis: guarda a CONTAGEM que o warm-up conseguiu
    /// carregar do banco, não o tamanho do cache — sem Redis, o catálogo funciona por disco/banco
    /// e a readiness não pode falhar só por Redis ausente (que é Degraded, não Unhealthy).</para>
    ///
    /// <para><b>Três estados, não dois</b> (ver <see cref="CatalogWarmupStatus"/>): só se chama
    /// <see cref="SetResult(int)"/> quando o warm-up REALMENTE concluiu — uma tentativa que falhou
    /// (SQL fora, busca sem sucesso) usa <see cref="RegisterFailedAttempt(string)"/> e mantém o
    /// estado em <see cref="CatalogWarmupStatus.Aquecendo"/>, preservando o retry da issue #67.</para>
    /// </summary>
    public sealed class CatalogWarmupState
    {
        private volatile bool _completed;
        private int _layoutCount = -1; // -1 = warm-up ainda não concluiu
        private int _failedAttempts;
        private volatile string? _lastFailureReason;

        /// <summary>Warm-up já concluiu (com sucesso ou com contagem zero)?</summary>
        public bool Completed => _completed;

        /// <summary>Quantidade de layouts que o warm-up conseguiu carregar. -1 enquanto não concluiu.</summary>
        public int LayoutCount => Volatile.Read(ref _layoutCount);

        /// <summary>Quantas tentativas de warm-up falharam até agora (retry em andamento).</summary>
        public int FailedAttempts => Volatile.Read(ref _failedAttempts);

        /// <summary>
        /// Motivo curto da última tentativa que falhou (ex.: nome do tipo da exceção). Só categoria —
        /// <b>nunca</b> mensagem crua de exceção, porque este valor é exposto anonimamente no corpo
        /// de <c>/health/ready</c> e mensagem de erro de SQL pode carregar detalhe de conexão.
        /// </summary>
        public string? LastFailureReason => _lastFailureReason;

        /// <summary>Classificação em três estados consumida pelo <c>CatalogHealthCheck</c>.</summary>
        public CatalogWarmupStatus Status
        {
            get
            {
                if (!_completed)
                    return CatalogWarmupStatus.Aquecendo;

                return LayoutCount > 0 ? CatalogWarmupStatus.Pronto : CatalogWarmupStatus.Vazio;
            }
        }

        /// <summary>
        /// Registra o resultado de um warm-up que CONCLUIU. Chamado uma vez, ao fim da população do
        /// cache. Contagem zero aqui significa "o catálogo está mesmo vazio" (estado definitivo) —
        /// não use para falha de leitura; para isso existe <see cref="RegisterFailedAttempt(string)"/>.
        /// </summary>
        public void SetResult(int layoutCount)
        {
            Volatile.Write(ref _layoutCount, layoutCount);
            _completed = true;
        }

        /// <summary>
        /// Registra uma tentativa de warm-up que falhou. NÃO conclui o warm-up de propósito: o estado
        /// permanece <see cref="CatalogWarmupStatus.Aquecendo"/> para que a readiness reporte
        /// "ainda não pronto" (transitório, o retry ainda pode recuperar) em vez de "catálogo vazio"
        /// (definitivo). Só serve para diagnóstico — não muda o resultado HTTP.
        /// </summary>
        /// <param name="motivo">Categoria curta e não sensível (ex.: <c>SqlException</c>).</param>
        public void RegisterFailedAttempt(string motivo)
        {
            Interlocked.Increment(ref _failedAttempts);
            _lastFailureReason = motivo;
        }
    }
}
