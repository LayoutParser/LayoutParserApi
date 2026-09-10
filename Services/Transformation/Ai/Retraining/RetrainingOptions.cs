namespace LayoutParserApi.Services.Transformation.Ai.Retraining
{
    /// <summary>
    /// Configuração do retraining automatizado do modelo fine-tuned (F4 do ADR
    /// docs/architecture/adr-backfill-catalogo-e-retraining-automatizado-2026-09-08.md, issue #351).
    /// Seção <c>XslSynth:Retraining</c> do appsettings.
    ///
    /// <para><b>Persistência:</b> segue o mesmo padrão de F3 (arquivos na árvore de
    /// <c>XslSynth:TrainingDataPath</c>, na VM <c>172.25.32.5</c>) — NÃO usa o SQL compartilhado
    /// <c>172.31.249.51</c> (read-only, ver <c>.claude/rules/security.md</c>). O estado (contador,
    /// timestamps) é um JSON durável ao lado do JSONL incremental.</para>
    ///
    /// <para><b>Desligado por padrão</b> (<see cref="Enabled"/> = false): o disparo real de
    /// <c>train_lora.py</c> e o <c>retraining.lock</c> físico dependem de fiação no lado da VM
    /// (cron/script), que é handoff pro <c>@lp-devops</c>. Enquanto isso não existe, a API só
    /// mantém o contador e checa o lock (se o arquivo aparecer), sem tentar disparar nada.</para>
    /// </summary>
    public class RetrainingOptions
    {
        /// <summary>Nome da seção no appsettings.</summary>
        public const string SectionName = "XslSynth:Retraining";

        /// <summary>Default do gatilho por volume — exemplos novos (F3) desde o último treino.</summary>
        public const int DefaultNewExampleThreshold = 300;

        /// <summary>Default do teto de agenda, em dias, desde o último treino concluído.</summary>
        public const int DefaultMaxDaysBetweenTrainings = 90;

        /// <summary>Default do intervalo entre avaliações do gatilho pelo background service, em horas.</summary>
        public const int DefaultEvaluationIntervalHours = 6;

        /// <summary>
        /// Liga a avaliação periódica do gatilho e a escrita do arquivo-marcador de disparo. Com
        /// <c>false</c> (default), o <see cref="RetrainingCoordinator"/> ainda incrementa o contador
        /// a cada convergência capturada por F3 (barato, sem efeito colateral), mas nunca dispara
        /// treino — seguro até o lado da VM estar pronto.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Dispara o retraining quando o contador de exemplos novos capturados por F3 desde o
        /// último treino atinge este valor. Valor &lt;= 0 cai no default
        /// (<see cref="DefaultNewExampleThreshold"/>). ADR §6.1: F3 cresce devagar (convergência
        /// real, não batch), então a ordem de grandeza é centenas, não o tamanho do dataset atual.
        /// </summary>
        public int NewExampleThreshold { get; set; } = DefaultNewExampleThreshold;

        /// <summary>
        /// Rede de segurança (ADR §6.1): se o volume não bater <see cref="NewExampleThreshold"/>
        /// dentro deste número de dias desde o último treino concluído, dispara mesmo assim com o
        /// que houver acumulado (desde que haja pelo menos 1 exemplo novo). Valor &lt;= 0 cai no
        /// default (<see cref="DefaultMaxDaysBetweenTrainings"/>).
        /// </summary>
        public int MaxDaysBetweenTrainings { get; set; } = DefaultMaxDaysBetweenTrainings;

        /// <summary>Intervalo entre avaliações do gatilho. Valor &lt;= 0 cai no default (6h).</summary>
        public int EvaluationIntervalHours { get; set; } = DefaultEvaluationIntervalHours;

        /// <summary>
        /// Caminho do JSON de estado durável (contador + timestamps). Default: <c>retraining-state.json</c>
        /// na árvore de <c>XslSynth:TrainingDataPath</c>.
        /// </summary>
        public string? StateFilePath { get; set; }

        /// <summary>
        /// Caminho do arquivo-marcador que o cron da VM observa pra disparar <c>train_lora.py</c>.
        /// Default: <c>retraining.trigger.json</c> na árvore de <c>XslSynth:TrainingDataPath</c>.
        /// O script da VM deve APAGAR este arquivo ao terminar o treino (é assim que a API detecta
        /// a conclusão — ver <see cref="RetrainingCoordinator"/>).
        /// </summary>
        public string? TriggerFilePath { get; set; }

        /// <summary>
        /// Caminho do lock de exclusão mútua Ollama-inferência × treino (ADR §6.2). Default:
        /// <c>retraining.lock</c> na árvore de <c>XslSynth:TrainingDataPath</c>. Enquanto este
        /// arquivo existir, o <see cref="RepairOrchestratorXslSynthesizerService"/> recusa a
        /// síntese via Ollama (graciosamente, sem exceção) e o coordenador não dispara treino novo.
        /// </summary>
        public string? LockFilePath { get; set; }

        /// <summary>
        /// Idade (em horas) a partir da qual o <c>retraining.lock</c> é considerado órfão e apenas
        /// logado como warning (não é removido automaticamente — remoção é decisão operacional).
        /// Default 60h (~40h de treino + margem). Valor &lt;= 0 desliga a checagem de idade.
        /// </summary>
        public int StaleLockAfterHours { get; set; } = 60;
    }
}
