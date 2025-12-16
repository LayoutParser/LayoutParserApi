using LayoutParserApi.Models.Database;
using LayoutParserApi.Services.Database;

namespace LayoutParserApi.Services.Interfaces
{
    public interface ICachedLayoutService
    {
        Task<LayoutSearchResponse> SearchLayoutsAsync(LayoutSearchRequest request);
        Task<LayoutRecord?> GetLayoutByIdAsync(int id);
        Task<LayoutRecord?> GetLayoutByGuidAsync(string layoutGuid);
        Task RefreshCacheFromDatabaseAsync();
        Task ClearCacheAsync();
        ILayoutDatabaseService GetLayoutDatabaseService();
    }
}
