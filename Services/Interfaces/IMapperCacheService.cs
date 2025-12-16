using LayoutParserApi.Models.Entities;

namespace LayoutParserApi.Services.Interfaces
{
    public interface IMapperCacheService
    {
        Task<List<Mapper>?> GetAllCachedMappersAsync();
        Task SetAllCachedMappersAsync(List<Mapper> mappers, TimeSpan? expiry = null);
        Task<Mapper?> GetCachedMapperByIdAsync(int id);
        Task SetCachedMapperByIdAsync(int id, Mapper mapper, TimeSpan? expiry = null);
        Task<List<Mapper>?> GetCachedMappersByInputLayoutGuidAsync(string inputLayoutGuid);
        Task<List<Mapper>?> GetCachedMappersByTargetLayoutGuidAsync(string targetLayoutGuid);
        Task SetCachedMappersByInputLayoutGuidAsync(string inputLayoutGuid, List<Mapper> mappers, TimeSpan? expiry = null);
        Task SetCachedMappersByTargetLayoutGuidAsync(string targetLayoutGuid, List<Mapper> mappers, TimeSpan? expiry = null);
        Task ClearCacheAsync();
    }
}
