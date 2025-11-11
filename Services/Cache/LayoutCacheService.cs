using StackExchange.Redis;
using System.Text.Json;
using LayoutParserApi.Models.Database;

namespace LayoutParserApi.Services.Cache
{
    public interface ILayoutCacheService
    {
        Task<List<LayoutRecord>?> GetCachedLayoutsAsync(string searchTerm);
        Task SetCachedLayoutsAsync(string searchTerm, List<LayoutRecord> layouts, TimeSpan? expiry = null);
        Task<LayoutRecord?> GetCachedLayoutByIdAsync(int id);
        Task SetCachedLayoutByIdAsync(int id, LayoutRecord layout, TimeSpan? expiry = null);
        Task ClearCacheAsync();
    }

    public class LayoutCacheService : ILayoutCacheService
    {
        private readonly IDatabase? _redis;
        private readonly ILogger<LayoutCacheService> _logger;
        private readonly TimeSpan _defaultExpiry = TimeSpan.FromHours(1); // Cache por 1 hora
        private readonly bool _redisAvailable;

        public LayoutCacheService(
            IConnectionMultiplexer? redis,
            ILogger<LayoutCacheService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            
            if (redis != null && redis.IsConnected)
            {
                try
                {
                    _redis = redis.GetDatabase();
                    _redisAvailable = true;
                    _logger.LogInformation("LayoutCacheService initialized with Redis");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Redis is available but failed to get database. Caching will be disabled.");
                    _redisAvailable = false;
                }
            }
            else
            {
                _redisAvailable = false;
                _logger.LogWarning("Redis is not available. LayoutCacheService will operate without caching.");
            }
        }

        public async Task<List<LayoutRecord>?> GetCachedLayoutsAsync(string searchTerm)
        {
            if (!_redisAvailable || _redis == null)
            {
                return null; // Cache não disponível
            }

            try
            {
                var cacheKey = $"layouts:search:{searchTerm}";
                var cachedData = await _redis.StringGetAsync(cacheKey);
                
                if (cachedData.HasValue)
                {
                    var layouts = JsonSerializer.Deserialize<List<LayoutRecord>>(cachedData.ToString());
                    _logger.LogInformation("Cache hit para busca: {SearchTerm} - {Count} layouts", searchTerm, layouts?.Count ?? 0);
                    return layouts;
                }

                _logger.LogInformation("Cache miss para busca: {SearchTerm}", searchTerm);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar layouts no cache para: {SearchTerm}", searchTerm);
                return null;
            }
        }

        public async Task SetCachedLayoutsAsync(string searchTerm, List<LayoutRecord> layouts, TimeSpan? expiry = null)
        {
            if (!_redisAvailable || _redis == null)
            {
                return; // Cache não disponível
            }

            try
            {
                var cacheKey = $"layouts:search:{searchTerm}";
                var jsonData = JsonSerializer.Serialize(layouts);
                
                await _redis.StringSetAsync(cacheKey, jsonData, expiry ?? _defaultExpiry);
                _logger.LogInformation("Cache atualizado para busca: {SearchTerm} - {Count} layouts", searchTerm, layouts.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao salvar layouts no cache para: {SearchTerm}", searchTerm);
            }
        }

        public async Task<LayoutRecord?> GetCachedLayoutByIdAsync(int id)
        {
            if (!_redisAvailable || _redis == null)
            {
                return null; // Cache não disponível
            }

            try
            {
                var cacheKey = $"layout:id:{id}";
                var cachedData = await _redis.StringGetAsync(cacheKey);
                
                if (cachedData.HasValue)
                {
                    var layout = JsonSerializer.Deserialize<LayoutRecord>(cachedData.ToString());
                    _logger.LogInformation("Cache hit para layout ID: {Id}", id);
                    return layout;
                }

                _logger.LogInformation("Cache miss para layout ID: {Id}", id);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar layout no cache para ID: {Id}", id);
                return null;
            }
        }

        public async Task SetCachedLayoutByIdAsync(int id, LayoutRecord layout, TimeSpan? expiry = null)
        {
            if (!_redisAvailable || _redis == null)
            {
                return; // Cache não disponível
            }

            try
            {
                var cacheKey = $"layout:id:{id}";
                var jsonData = JsonSerializer.Serialize(layout);
                
                await _redis.StringSetAsync(cacheKey, jsonData, expiry ?? _defaultExpiry);
                _logger.LogInformation("Cache atualizado para layout ID: {Id}", id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao salvar layout no cache para ID: {Id}", id);
            }
        }

        public async Task ClearCacheAsync()
        {
            if (!_redisAvailable || _redis == null)
            {
                return; // Cache não disponível
            }

            try
            {
                var server = _redis.Multiplexer.GetServer(_redis.Multiplexer.GetEndPoints().First());
                await server.FlushDatabaseAsync();
                _logger.LogInformation("Cache Redis limpo com sucesso");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao limpar cache Redis");
            }
        }

    }
}
