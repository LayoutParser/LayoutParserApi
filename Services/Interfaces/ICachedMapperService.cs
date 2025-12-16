using LayoutParserApi.Models.Entities;

namespace LayoutParserApi.Services.Interfaces
{
    public interface ICachedMapperService
    {
        Task<List<Mapper>> GetAllMappersAsync();
        Task<List<Mapper>> GetMappersByInputLayoutGuidAsync(string inputLayoutGuid);
        Task<List<Mapper>> GetMappersByTargetLayoutGuidAsync(string targetLayoutGuid);
        Task RefreshCacheFromDatabaseAsync();
    }
}
