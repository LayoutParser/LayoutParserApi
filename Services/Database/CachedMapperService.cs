using LayoutParserApi.Models.Entities;
using LayoutParserApi.Services.Cache;
using System.Linq;

namespace LayoutParserApi.Services.Database
{
    public interface ICachedMapperService
    {
        Task<List<Mapper>> GetAllMappersAsync();
        Task<List<Mapper>> GetMappersByInputLayoutGuidAsync(string inputLayoutGuid);
        Task<List<Mapper>> GetMappersByTargetLayoutGuidAsync(string targetLayoutGuid);
        Task RefreshCacheFromDatabaseAsync();
    }

    public class CachedMapperService : ICachedMapperService
    {
        private readonly MapperDatabaseService _mapperDatabaseService;
        private readonly IMapperCacheService _cacheService;
        private readonly ILogger<CachedMapperService> _logger;

        public CachedMapperService(
            MapperDatabaseService mapperDatabaseService,
            IMapperCacheService cacheService,
            ILogger<CachedMapperService> logger)
        {
            _mapperDatabaseService = mapperDatabaseService;
            _cacheService = cacheService;
            _logger = logger;
        }

        public async Task<List<Mapper>> GetAllMappersAsync()
        {
            try
            {
                // Tentar buscar do cache primeiro
                var cachedMappers = await _cacheService.GetAllCachedMappersAsync();
                if (cachedMappers != null && cachedMappers.Any())
                {
                    _logger.LogInformation("Mapeadores retornados do cache: {Count}", cachedMappers.Count);
                    return cachedMappers;
                }

                // Se não estiver no cache, buscar do banco
                _logger.LogInformation("Mapeadores não encontrados no cache, buscando do banco de dados");
                var mappers = await _mapperDatabaseService.GetAllMappersAsync();
                
                // Salvar no cache
                if (mappers != null && mappers.Any())
                {
                    await _cacheService.SetAllCachedMappersAsync(mappers);
                    _logger.LogInformation("Mapeadores salvos no cache: {Count}", mappers.Count);
                }

                return mappers ?? new List<Mapper>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar todos os mapeadores");
                return new List<Mapper>();
            }
        }

        public async Task<List<Mapper>> GetMappersByInputLayoutGuidAsync(string inputLayoutGuid)
        {
            try
            {
                // Tentar buscar do cache primeiro
                var cachedMappers = await _cacheService.GetCachedMappersByInputLayoutGuidAsync(inputLayoutGuid);
                if (cachedMappers != null && cachedMappers.Any())
                {
                    _logger.LogInformation("Mapeadores com InputLayoutGuid {Guid} retornados do cache: {Count}", inputLayoutGuid, cachedMappers.Count);
                    return cachedMappers;
                }

                // Se não estiver no cache, buscar do banco
                _logger.LogInformation("Mapeadores com InputLayoutGuid {Guid} não encontrados no cache, buscando do banco", inputLayoutGuid);
                var mapper = await _mapperDatabaseService.GetMapperByInputLayoutGuidAsync(inputLayoutGuid);
                
                var mappers = mapper != null ? new List<Mapper> { mapper } : new List<Mapper>();
                
                // Salvar no cache (filtrar apenas os que correspondem ao InputLayoutGuid)
                var matchingMappers = mappers.Where(m => 
                    (m.InputLayoutGuid == inputLayoutGuid || m.InputLayoutGuidFromXml == inputLayoutGuid)).ToList();
                
                if (matchingMappers.Any())
                {
                    await _cacheService.SetCachedMappersByInputLayoutGuidAsync(inputLayoutGuid, matchingMappers);
                    return matchingMappers;
                }

                return mappers;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar mapeadores por InputLayoutGuid: {Guid}", inputLayoutGuid);
                return new List<Mapper>();
            }
        }

        public async Task<List<Mapper>> GetMappersByTargetLayoutGuidAsync(string targetLayoutGuid)
        {
            try
            {
                // Tentar buscar do cache primeiro
                var cachedMappers = await _cacheService.GetCachedMappersByTargetLayoutGuidAsync(targetLayoutGuid);
                if (cachedMappers != null && cachedMappers.Any())
                {
                    _logger.LogInformation("Mapeadores com TargetLayoutGuid {Guid} retornados do cache: {Count}", targetLayoutGuid, cachedMappers.Count);
                    return cachedMappers;
                }

                // Se não estiver no cache, buscar do banco
                _logger.LogInformation("Mapeadores com TargetLayoutGuid {Guid} não encontrados no cache, buscando do banco", targetLayoutGuid);
                var mapper = await _mapperDatabaseService.GetMapperByTargetLayoutGuidAsync(targetLayoutGuid);
                
                var mappers = mapper != null ? new List<Mapper> { mapper } : new List<Mapper>();
                
                // Salvar no cache (filtrar apenas os que correspondem ao TargetLayoutGuid)
                var matchingMappers = mappers.Where(m => 
                    (m.TargetLayoutGuid == targetLayoutGuid || m.TargetLayoutGuidFromXml == targetLayoutGuid)).ToList();
                
                if (matchingMappers.Any())
                {
                    await _cacheService.SetCachedMappersByTargetLayoutGuidAsync(targetLayoutGuid, matchingMappers);
                    return matchingMappers;
                }

                return mappers;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar mapeadores por TargetLayoutGuid: {Guid}", targetLayoutGuid);
                return new List<Mapper>();
            }
        }

        public async Task RefreshCacheFromDatabaseAsync()
        {
            try
            {
                _logger.LogInformation("Atualizando cache de mapeadores a partir do banco de dados");
                var mappers = await _mapperDatabaseService.GetAllMappersAsync();
                
                if (mappers != null && mappers.Any())
                {
                    await _cacheService.SetAllCachedMappersAsync(mappers);
                    _logger.LogInformation("Cache de mapeadores atualizado: {Count} mapeadores", mappers.Count);
                }
                else
                {
                    _logger.LogWarning("Nenhum mapeador encontrado no banco de dados");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao atualizar cache de mapeadores a partir do banco de dados");
            }
        }
    }
}

