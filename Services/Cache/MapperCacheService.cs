using StackExchange.Redis;
using System.Text.Json;
using System.Text.Encodings.Web;
using LayoutParserApi.Models.Entities;

namespace LayoutParserApi.Services.Cache
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

    public class MapperCacheService : IMapperCacheService
    {
        private readonly IDatabase? _redis;
        private readonly ILogger<MapperCacheService> _logger;
        private readonly TimeSpan _defaultExpiry = TimeSpan.FromHours(24); // Cache por 24 horas (fixo para múltiplos computadores)
        private readonly bool _redisAvailable;
        
        // Chave fixa para todos os mapeadores (compartilhada entre múltiplos computadores)
        // Usar apenas "mappers:search:all" para compatibilidade com front-end
        private const string ALL_MAPPERS_SEARCH_KEY = "mappers:search:all";
        
        // Opções de serialização JSON que não escapam caracteres XML/HTML
        // Isso preserva o XML intacto no JSON (não converte < para \u003C, etc.)
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping // Não escapa <, >, &, etc.
        };

        public MapperCacheService(
            IConnectionMultiplexer? redis,
            ILogger<MapperCacheService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            
            if (redis != null && redis.IsConnected)
            {
                try
                {
                    _redis = redis.GetDatabase();
                    _redisAvailable = true;
                    _logger.LogInformation("MapperCacheService initialized with Redis");
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
                _logger.LogWarning("Redis is not available. MapperCacheService will operate without caching.");
            }
        }

        public async Task<List<Mapper>?> GetAllCachedMappersAsync()
        {
            if (!_redisAvailable || _redis == null)
            {
                _logger.LogWarning("⚠️ Redis não disponível - não é possível buscar cache de mapeadores");
                return null;
            }

            try
            {
                _logger.LogInformation("🔍 Buscando mapeadores no cache (chave: {Key})...", 
                    ALL_MAPPERS_SEARCH_KEY);
                
                // Verificar se a chave existe
                var exists = await _redis.KeyExistsAsync(ALL_MAPPERS_SEARCH_KEY);
                _logger.LogInformation("🔍 Verificação de chave no Redis: {Key}={Exists}", 
                    ALL_MAPPERS_SEARCH_KEY, exists);
                
                // Buscar na chave "mappers:search:all"
                var cachedData = await _redis.StringGetAsync(ALL_MAPPERS_SEARCH_KEY);
                
                if (cachedData.HasValue)
                {
                    _logger.LogInformation("✅ Cache encontrado - tamanho: {Size} bytes", cachedData.Length());
                    
                    var mappers = JsonSerializer.Deserialize<List<Mapper>>(cachedData.ToString(), JsonOptions);
                    _logger.LogInformation("✅ Cache hit para todos os mapeadores - {Count} mapeadores", mappers?.Count ?? 0);
                    return mappers;
                }

                _logger.LogWarning("⚠️ Cache miss para todos os mapeadores - nenhuma chave encontrada");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erro ao buscar todos os mapeadores no cache: {Message}", ex.Message);
                _logger.LogError(ex, "Stack trace: {StackTrace}", ex.StackTrace);
                return null;
            }
        }

        public async Task SetAllCachedMappersAsync(List<Mapper> mappers, TimeSpan? expiry = null)
        {
            if (!_redisAvailable || _redis == null)
            {
                _logger.LogWarning("Redis não disponível - não é possível salvar cache de mapeadores");
                return;
            }

            try
            {
                if (mappers == null || !mappers.Any())
                {
                    _logger.LogWarning("⚠️ Tentativa de salvar lista vazia de mapeadores no cache");
                    return;
                }

                var jsonData = JsonSerializer.Serialize(mappers, JsonOptions);
                _logger.LogInformation("📦 Serializando {Count} mapeadores para cache (tamanho JSON: {Size} bytes)", 
                    mappers.Count, jsonData.Length);
                
                // Verificar se o Redis está realmente disponível
                if (_redis == null)
                {
                    _logger.LogError("❌ Redis não está disponível (null)");
                    return;
                }

                _logger.LogInformation("🔄 Salvando cache no Redis (chave: {Key})...", 
                    ALL_MAPPERS_SEARCH_KEY);
                
                // Cache permanente (sem expiração) para múltiplos computadores
                // A chave "mappers:search:all" deve ser permanente no Redis
                var result = await _redis.StringSetAsync(ALL_MAPPERS_SEARCH_KEY, jsonData);
                
                if (result)
                {
                    _logger.LogInformation("✅ Cache permanente atualizado com sucesso para todos os mapeadores - {Count} mapeadores (chave: {Key})", 
                        mappers.Count, ALL_MAPPERS_SEARCH_KEY);
                    
                    // Verificar se a chave foi realmente criada
                    await Task.Delay(100); // Pequeno delay para garantir que o Redis processou
                    var verify = await _redis.KeyExistsAsync(ALL_MAPPERS_SEARCH_KEY);
                    _logger.LogInformation("🔍 Verificação de chave no Redis: {Key}={Exists}", 
                        ALL_MAPPERS_SEARCH_KEY, verify);
                    
                    if (verify)
                    {
                        // Verificar o tamanho da chave
                        var length = await _redis.StringLengthAsync(ALL_MAPPERS_SEARCH_KEY);
                        _logger.LogInformation("📊 Tamanho da chave no Redis: {Key}={Length} bytes", 
                            ALL_MAPPERS_SEARCH_KEY, length);
                    }
                }
                else
                {
                    _logger.LogError("❌ Falha ao salvar cache de mapeadores no Redis. Result={Result}", 
                        result);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erro ao salvar todos os mapeadores no cache: {Message}", ex.Message);
                _logger.LogError(ex, "Stack trace: {StackTrace}", ex.StackTrace);
            }
        }

        public async Task<Mapper?> GetCachedMapperByIdAsync(int id)
        {
            if (!_redisAvailable || _redis == null)
            {
                return null;
            }

            try
            {
                var cacheKey = $"mapper:id:{id}";
                var cachedData = await _redis.StringGetAsync(cacheKey);
                
                if (cachedData.HasValue)
                {
                    var mapper = JsonSerializer.Deserialize<Mapper>(cachedData.ToString(), JsonOptions);
                    _logger.LogInformation("Cache hit para mapeador ID: {Id}", id);
                    return mapper;
                }

                _logger.LogInformation("Cache miss para mapeador ID: {Id}", id);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar mapeador no cache para ID: {Id}", id);
                return null;
            }
        }

        public async Task SetCachedMapperByIdAsync(int id, Mapper mapper, TimeSpan? expiry = null)
        {
            if (!_redisAvailable || _redis == null)
            {
                return;
            }

            try
            {
                var cacheKey = $"mapper:id:{id}";
                var jsonData = JsonSerializer.Serialize(mapper, JsonOptions);
                
                await _redis.StringSetAsync(cacheKey, jsonData, expiry ?? _defaultExpiry);
                _logger.LogInformation("Cache atualizado para mapeador ID: {Id}", id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao salvar mapeador no cache para ID: {Id}", id);
            }
        }

        public async Task<List<Mapper>?> GetCachedMappersByInputLayoutGuidAsync(string inputLayoutGuid)
        {
            if (!_redisAvailable || _redis == null)
            {
                return null;
            }

            try
            {
                var cacheKey = $"mappers:input:{inputLayoutGuid}";
                var cachedData = await _redis.StringGetAsync(cacheKey);
                
                if (cachedData.HasValue)
                {
                    var mappers = JsonSerializer.Deserialize<List<Mapper>>(cachedData.ToString(), JsonOptions);
                    _logger.LogInformation("Cache hit para mapeadores com InputLayoutGuid: {Guid} - {Count} mapeadores", inputLayoutGuid, mappers?.Count ?? 0);
                    return mappers;
                }

                _logger.LogInformation("Cache miss para mapeadores com InputLayoutGuid: {Guid}", inputLayoutGuid);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar mapeadores no cache para InputLayoutGuid: {Guid}", inputLayoutGuid);
                return null;
            }
        }

        public async Task<List<Mapper>?> GetCachedMappersByTargetLayoutGuidAsync(string targetLayoutGuid)
        {
            if (!_redisAvailable || _redis == null)
            {
                return null;
            }

            try
            {
                var cacheKey = $"mappers:target:{targetLayoutGuid}";
                var cachedData = await _redis.StringGetAsync(cacheKey);
                
                if (cachedData.HasValue)
                {
                    var mappers = JsonSerializer.Deserialize<List<Mapper>>(cachedData.ToString(), JsonOptions);
                    _logger.LogInformation("Cache hit para mapeadores com TargetLayoutGuid: {Guid} - {Count} mapeadores", targetLayoutGuid, mappers?.Count ?? 0);
                    return mappers;
                }

                _logger.LogInformation("Cache miss para mapeadores com TargetLayoutGuid: {Guid}", targetLayoutGuid);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar mapeadores no cache para TargetLayoutGuid: {Guid}", targetLayoutGuid);
                return null;
            }
        }

        public async Task SetCachedMappersByInputLayoutGuidAsync(string inputLayoutGuid, List<Mapper> mappers, TimeSpan? expiry = null)
        {
            if (!_redisAvailable || _redis == null)
            {
                return;
            }

            try
            {
                var cacheKey = $"mappers:input:{inputLayoutGuid}";
                var jsonData = JsonSerializer.Serialize(mappers, JsonOptions);
                
                await _redis.StringSetAsync(cacheKey, jsonData, expiry ?? _defaultExpiry);
                _logger.LogInformation("Cache atualizado para mapeadores com InputLayoutGuid: {Guid} - {Count} mapeadores", inputLayoutGuid, mappers.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao salvar mapeadores no cache para InputLayoutGuid: {Guid}", inputLayoutGuid);
            }
        }

        public async Task SetCachedMappersByTargetLayoutGuidAsync(string targetLayoutGuid, List<Mapper> mappers, TimeSpan? expiry = null)
        {
            if (!_redisAvailable || _redis == null)
            {
                return;
            }

            try
            {
                var cacheKey = $"mappers:target:{targetLayoutGuid}";
                var jsonData = JsonSerializer.Serialize(mappers, JsonOptions);
                
                await _redis.StringSetAsync(cacheKey, jsonData, expiry ?? _defaultExpiry);
                _logger.LogInformation("Cache atualizado para mapeadores com TargetLayoutGuid: {Guid} - {Count} mapeadores", targetLayoutGuid, mappers.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao salvar mapeadores no cache para TargetLayoutGuid: {Guid}", targetLayoutGuid);
            }
        }

        public async Task ClearCacheAsync()
        {
            if (!_redisAvailable || _redis == null)
            {
                return;
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

