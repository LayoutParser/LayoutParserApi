namespace LayoutParserApi.Services.Interfaces
{
    /// <summary>
    /// Resposta livre do revisor a UMA pergunta em aberto da IA (<c>MappingDraftRule.OpenQuestions</c>) —
    /// issue #422. A pergunta não tem id próprio (é <c>string</c> em lista), então a chave é
    /// <c>DraftId + RuleId + QuestionIndex</c> (índice 0-based na lista da regra).
    /// <paramref name="QuestionText"/> é um SNAPSHOT do texto no momento da resposta, para o histórico
    /// continuar legível mesmo que a regra seja substituída. <paramref name="Version"/> começa em 1 e
    /// cresce a cada resposta com texto diferente (histórico append-only).
    /// </summary>
    public sealed record MappingRuleAnswer(
        Guid AnswerId,
        Guid WorkspaceId,
        Guid DraftId,
        Guid RuleId,
        int QuestionIndex,
        string QuestionText,
        string AnswerText,
        Guid AnsweredByUserId,
        string? AnsweredByName,
        DateTimeOffset AnsweredAt,
        int Version);

    /// <summary>
    /// Persistência das respostas do revisor (issue #422). Separado de <see cref="IMappingDraftStore"/>
    /// (mesmo critério de <c>IFieldCorrectionStore</c>): domínio próprio e evita quebrar todas as
    /// implementações dessa interface. O isolamento por workspace é checado pelo controller via
    /// <c>IMappingDraftStore.GetDraftIfMemberAsync</c> ANTES de chamar este store.
    /// </summary>
    public interface IMappingRuleAnswerStore
    {
        /// <summary>
        /// Registra a resposta. Idempotente: se o texto é idêntico ao da versão mais recente, NÃO cria
        /// versão nova e devolve a existente (<c>Created=false</c>); se difere, insere versão N+1.
        /// </summary>
        Task<(MappingRuleAnswer Answer, bool Created)> SaveAnswerAsync(
            Guid workspaceId, Guid draftId, Guid ruleId, int questionIndex, string questionText,
            string answerText, Guid userId, string? userName, CancellationToken cancellationToken);

        /// <summary>
        /// Lê respostas de um draft (opcionalmente filtradas por regra). Por padrão só a versão mais
        /// recente de cada pergunta; <paramref name="includeHistory"/> devolve todas as versões.
        /// </summary>
        Task<IReadOnlyList<MappingRuleAnswer>> ListAsync(
            Guid draftId, Guid? ruleId, bool includeHistory, CancellationToken cancellationToken);
    }
}
