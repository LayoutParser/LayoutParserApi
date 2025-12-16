using LayoutParserApi.Models.Database;

namespace LayoutParserApi.Services.Interfaces
{
    public interface ILayoutDatabaseService
    {
        Task<LayoutSearchResponse> SearchLayoutsAsync(LayoutSearchRequest request);
        Task<LayoutRecord?> GetLayoutByIdAsync(int id);
    }
}
