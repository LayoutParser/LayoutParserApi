using LayoutParserApi.Models.Fiscal;

namespace LayoutParserApi.Services.Fiscal
{
    /// <summary>
    /// Catálogo de exemplos reais de transformação TCL/XSL da Neogrid (corpus de referência/oráculo,
    /// não gerado pelo nosso pipeline e sem vínculo com workspace/governança de release). Ver
    /// <see cref="ReferenceExampleCatalogService"/>.
    /// </summary>
    public interface IReferenceExampleCatalogService
    {
        /// <summary>Lista os exemplos disponíveis, opcionalmente filtrados por tipo de documento. Nunca lança — degrada para lista vazia se o corpus não estiver disponível.</summary>
        Task<IReadOnlyList<ReferenceExample>> ListAsync(string? docType, CancellationToken cancellationToken);

        /// <summary>Retorna o conteúdo (TCL/XSL) de um exemplo específico, ou <c>null</c> se o id não existir mais no corpus.</summary>
        Task<ReferenceExampleContent?> GetContentAsync(string id, CancellationToken cancellationToken);
    }
}
