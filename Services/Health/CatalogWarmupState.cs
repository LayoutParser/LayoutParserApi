namespace LayoutParserApi.Services.Health
{
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
    /// </summary>
    public sealed class CatalogWarmupState
    {
        private volatile bool _completed;
        private int _layoutCount = -1; // -1 = warm-up ainda não concluiu

        /// <summary>Warm-up já concluiu (com sucesso ou com contagem zero)?</summary>
        public bool Completed => _completed;

        /// <summary>Quantidade de layouts que o warm-up conseguiu carregar. -1 enquanto não concluiu.</summary>
        public int LayoutCount => Volatile.Read(ref _layoutCount);

        /// <summary>Registra o resultado do warm-up. Chamado uma vez, ao fim da população do cache.</summary>
        public void SetResult(int layoutCount)
        {
            Volatile.Write(ref _layoutCount, layoutCount);
            _completed = true;
        }
    }
}
