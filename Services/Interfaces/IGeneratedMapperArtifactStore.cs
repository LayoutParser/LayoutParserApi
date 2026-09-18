namespace LayoutParserApi.Services.Interfaces
{
    /// <summary>
    /// Constantes de status do candidato gerado automaticamente para um mapper Sysmiddle
    /// (issue #438, ADR <c>docs/architecture/adr-geracao-automatica-gabarito-sysmiddle.md</c> §5).
    /// </summary>
    public static class GeneratedMapperArtifactStatus
    {
        /// <summary>Nunca foi pedido/gerado para este mapper.</summary>
        public const string None = "none";

        /// <summary>Geração em andamento (disparada por este ou por outro request).</summary>
        public const string Generating = "generating";

        /// <summary>Candidato disponível e com o hash do <c>MapperVo</c> ainda batendo.</summary>
        public const string Ready = "ready";

        /// <summary>Existia um candidato, mas o mapper mudou de versão desde então (hash divergente).</summary>
        public const string Stale = "stale";
    }

    /// <summary>
    /// Linha persistida de <c>dbo.tbGeneratedMapperArtifact</c> — um candidato TCL/XSL/XSLT por
    /// <c>MapperGuid</c> (chave). <see cref="Status"/> guarda só os estados de escrita
    /// (<c>none</c> nunca é persistido — ausência de linha já significa "none"; <c>stale</c> também
    /// não é persistido — é calculado em leitura comparando <see cref="MapperVoHash"/> com o hash
    /// atual do mapper, ver <see cref="Transformation.Ai.IGeneratedMapperArtifactService"/>).
    /// </summary>
    public sealed record GeneratedMapperArtifactRecord(
        string MapperGuid,
        string Status,
        string? Content,
        string? CoverageJson,
        string? ValidationBasis,
        string? MapperVoHash,
        string? CorrelationId,
        DateTimeOffset? GeneratedAt,
        DateTimeOffset UpdatedAt);

    /// <summary>
    /// Acesso a dado do candidato gerado automaticamente (issue #438). Mesmo padrão ADO.NET cru de
    /// <c>SqlFieldCorrectionStore</c> — tabela autossuficiente, sem FK (mapper vive em
    /// <c>tbMapper</c>, banco compartilhado somente-leitura, ver <c>.claude/rules/security.md</c>).
    /// </summary>
    public interface IGeneratedMapperArtifactStore
    {
        Task<GeneratedMapperArtifactRecord?> GetAsync(string mapperGuid, CancellationToken cancellationToken);

        /// <summary>
        /// Tenta assumir a geração deste mapper de forma atômica (issue #438, item 5 — "concorrência
        /// baixa"): se não existir linha, insere já como <see cref="GeneratedMapperArtifactStatus.Generating"/>;
        /// se existir e o status atual permitir regeração (<c>none</c> nunca fica persistido, então na
        /// prática é qualquer linha existente que não esteja <c>generating</c>), faz UPDATE condicional.
        /// Devolve <c>false</c> se outra chamada concorrente já está gerando — o chamador NÃO deve
        /// disparar uma segunda geração nesse caso.
        /// </summary>
        Task<bool> TryBeginGeneratingAsync(string mapperGuid, string correlationId, CancellationToken cancellationToken);

        /// <summary>Grava o candidato pronto e volta o status para <see cref="GeneratedMapperArtifactStatus.Ready"/>.</summary>
        Task CompleteAsync(
            string mapperGuid, string content, string coverageJson, string validationBasis,
            string mapperVoHash, string correlationId, CancellationToken cancellationToken);

        /// <summary>
        /// Reverte a geração malsucedida — remove a linha (equivalente a voltar para <c>none</c>,
        /// que nunca é persistido) para permitir nova tentativa no próximo GET.
        /// </summary>
        Task FailAsync(string mapperGuid, CancellationToken cancellationToken);
    }
}
