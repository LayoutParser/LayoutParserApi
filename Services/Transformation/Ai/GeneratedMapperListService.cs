using System.Text.Json;

using LayoutParserApi.Services.Interfaces;

namespace LayoutParserApi.Services.Transformation.Ai
{
    /// <summary>Item auto-gerado já projetado para a listagem unificada (sem <c>Content</c> — o conteúdo vem do detalhe).</summary>
    /// <param name="MapperName">Nome legível do catálogo Sysmiddle (<c>MAP_...</c>); <c>null</c> se o catálogo estiver indisponível/não resolver.</param>
    /// <param name="Status"><c>ready</c> ou <c>generating</c> (status persistido; <c>stale</c> só é calculado no detalhe).</param>
    /// <param name="ValidationBasis">Sempre <c>declared_dsl</c> quando há candidato — cobertura contra o DSL declarado, nunca execução real.</param>
    /// <param name="Coverage">Cobertura já desserializada (era string JSON opaca); <c>null</c> se ainda não gerado/JSON inválido.</param>
    public sealed record GeneratedMapperListItem(
        string MapperGuid,
        string? MapperName,
        string Status,
        string? ValidationBasis,
        JsonElement? Coverage,
        DateTimeOffset? GeneratedAt,
        string? CorrelationId);

    /// <summary>Página de auto-gerados. <see cref="Available"/> = false quando o store falhou (degradação).</summary>
    public sealed record GeneratedMapperListPage(IReadOnlyList<GeneratedMapperListItem> Items, int TotalCount, bool Available)
    {
        public static GeneratedMapperListPage Empty { get; } = new(Array.Empty<GeneratedMapperListItem>(), 0, true);
    }

    /// <summary>
    /// Fonte auto-gerada da listagem unificada <c>GET .../mapping-releases</c> (issue #438, ADR
    /// <c>adr-unificacao-generated-artifact-mapping-release.md</c>, opção b).
    /// </summary>
    public interface IGeneratedMapperListService
    {
        /// <summary>
        /// Devolve até <paramref name="take"/> itens a partir de <paramref name="skip"/> e o total geral
        /// (mesmo com <paramref name="take"/> = 0, para o total da paginação combinada). Nunca lança:
        /// falha do store/catálogo é logada e vira <see cref="GeneratedMapperListPage.Available"/> = false.
        /// </summary>
        Task<GeneratedMapperListPage> ListAsync(string? status, int skip, int take, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Implementação padrão. O vínculo mapper→workspace (#417) é PROVISÓRIO: não há isolamento —
    /// todo artefato gerado aparece para todo workspace, porque o catálogo Sysmiddle já é org-wide
    /// (<c>AllowedPackageGuids</c>). Se #417 fechar um vínculo, o ponto único para filtrar é este
    /// serviço (receberia <c>workspaceId</c>); a assinatura hoje não o exige de propósito.
    /// </summary>
    public sealed class GeneratedMapperListService : IGeneratedMapperListService
    {
        private readonly IGeneratedMapperArtifactStore _store;
        private readonly ICachedMapperService _mapperService;
        private readonly ILogger<GeneratedMapperListService> _logger;

        public GeneratedMapperListService(
            IGeneratedMapperArtifactStore store,
            ICachedMapperService mapperService,
            ILogger<GeneratedMapperListService> logger)
        {
            _store = store;
            _mapperService = mapperService;
            _logger = logger;
        }

        public async Task<GeneratedMapperListPage> ListAsync(string? status, int skip, int take, CancellationToken cancellationToken)
        {
            IReadOnlyList<GeneratedMapperArtifactRecord> records;
            int total;
            try
            {
                // SQL não aceita FETCH NEXT 0: pede pelo menos 1 linha (traz o total) e corta depois.
                var (rows, count) = await _store.ListAsync(status, skip, Math.Max(take, 1), cancellationToken);
                records = rows.Take(Math.Max(take, 0)).ToList();
                total = count;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Resiliência: a lista de releases reais NÃO pode quebrar por causa do store de gerados.
                _logger.LogWarning(ex, "Falha ao listar artefatos auto-gerados para a listagem unificada — devolvendo só releases reais.");
                return new GeneratedMapperListPage(Array.Empty<GeneratedMapperListItem>(), 0, false);
            }

            var names = records.Count == 0
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : await ResolveNamesAsync();

            var items = records.Select(r => new GeneratedMapperListItem(
                r.MapperGuid,
                names.TryGetValue(r.MapperGuid, out var name) ? name : null,
                r.Status,
                r.ValidationBasis,
                ParseCoverage(r.CoverageJson),
                r.GeneratedAt,
                r.CorrelationId)).ToList();

            return new GeneratedMapperListPage(items, total, true);
        }

        /// <summary>
        /// Nome via catálogo em cache (<see cref="ICachedMapperService.GetAllMappersAsync"/>, uma
        /// chamada por página — cache Redis, cai para <c>tbMapper</c> SOMENTE LEITURA). Falha degrada
        /// para "sem nome" (o item segue com <c>mapperGuid</c>).
        /// </summary>
        private async Task<Dictionary<string, string>> ResolveNamesAsync()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var mappers = await _mapperService.GetAllMappersAsync();
                foreach (var m in mappers)
                {
                    if (!string.IsNullOrWhiteSpace(m.MapperGuid) && !string.IsNullOrWhiteSpace(m.Name))
                        result[m.MapperGuid] = m.Name;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Catálogo de mappers indisponível ao resolver nomes da listagem unificada — itens seguem só com mapperGuid.");
            }
            return result;
        }

        private JsonElement? ParseCoverage(string? coverageJson)
        {
            if (string.IsNullOrWhiteSpace(coverageJson))
                return null;
            try
            {
                using var doc = JsonDocument.Parse(coverageJson);
                return doc.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "CoverageJson de artefato gerado não é JSON válido — omitindo 'coverage' do item.");
                return null;
            }
        }
    }
}
