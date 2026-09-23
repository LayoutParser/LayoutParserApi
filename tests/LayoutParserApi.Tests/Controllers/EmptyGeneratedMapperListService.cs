using LayoutParserApi.Services.Transformation.Ai;

namespace LayoutParserApi.Tests.Controllers
{
    /// <summary>Dublê "sem auto-gerados" para testes de controller que não exercitam a unificação (issue #438).</summary>
    public sealed class EmptyGeneratedMapperListService : IGeneratedMapperListService
    {
        public Task<GeneratedMapperListPage> ListAsync(string? status, int skip, int take, CancellationToken cancellationToken)
            => Task.FromResult(GeneratedMapperListPage.Empty);
    }
}
