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

        public async Task<List<Mapper>> GetMappersByInputLayoutGuidAsync(string inputLayoutGuid)
        {
            try
            {
                // Normalizar o GUID (remover prefixo LAY_ se houver)
                var normalizedGuid = NormalizeLayoutGuid(inputLayoutGuid);
                
                // Primeiro, buscar todos os mapeadores do cache permanente
                var allMappers = await GetAllMappersAsync();
                
                if (allMappers != null && allMappers.Any())
                {
                    // Filtrar mapeadores que têm este layout como entrada
                    var matchingMappers = allMappers.Where(m =>
                    {
                        var mapperInputGuid = m.InputLayoutGuid ?? m.InputLayoutGuidFromXml ?? "";
                        return GuidMatches(mapperInputGuid, inputLayoutGuid) ||
                               GuidMatches(mapperInputGuid, normalizedGuid);
                    }).ToList();

                    if (matchingMappers.Any())
                    {
                        _logger.LogInformation("Mapeadores com InputLayoutGuid {Guid} encontrados no cache: {Count}", inputLayoutGuid, matchingMappers.Count);
                        return matchingMappers;
                    }
                }

                // Se não encontrou no cache, buscar do banco
                _logger.LogInformation("Mapeadores com InputLayoutGuid {Guid} não encontrados no cache, buscando do banco", inputLayoutGuid);
                var mapper = await _mapperDatabaseService.GetMapperByInputLayoutGuidAsync(inputLayoutGuid);
                
                var mappers = mapper != null ? new List<Mapper> { mapper } : new List<Mapper>();
                
                // Se encontrou no banco, atualizar o cache permanente
                if (mappers.Any())
                {
                    // Recarregar todos os mapeadores e atualizar cache
                    await RefreshCacheFromDatabaseAsync();
                }

                return mappers;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar mapeadores por InputLayoutGuid: {Guid}", inputLayoutGuid);
                return new List<Mapper>();
            }
        }

        private string NormalizeLayoutGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid))
                return "";
            
            // Remover prefixo LAY_ se houver
            var normalized = guid;
            if (normalized.StartsWith("LAY_", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(4);
            
            // Remover espaços e converter para minúsculas para comparação
            return normalized.Trim().ToLowerInvariant();
        }

        private bool GuidMatches(string guid1, string guid2)
        {
            if (string.IsNullOrEmpty(guid1) || string.IsNullOrEmpty(guid2))
                return false;
            
            var norm1 = NormalizeLayoutGuid(guid1);
            var norm2 = NormalizeLayoutGuid(guid2);
            
            // Comparação exata
            if (norm1 == norm2)
                return true;
            
            // Tentar comparar apenas a parte do GUID (sem prefixo)
            // Ex: "LAY_ad4fb6f4-9ff5-44fd-988b-3da5ed56b22c" vs "ad4fb6f4-9ff5-44fd-988b-3da5ed56b22c"
            var guid1Only = ExtractGuidOnly(norm1);
            var guid2Only = ExtractGuidOnly(norm2);
            
            if (guid1Only == guid2Only && !string.IsNullOrEmpty(guid1Only))
                return true;
            
            // Comparação parcial (caso um tenha o prefixo e outro não)
            return norm1.Contains(norm2) || norm2.Contains(norm1);
        }

        private string ExtractGuidOnly(string guid)
        {
            if (string.IsNullOrEmpty(guid))
                return "";
            
            // Tentar extrair apenas a parte do GUID (formato: xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx)
            var guidPattern = System.Text.RegularExpressions.Regex.Match(guid, @"([a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (guidPattern.Success)
                return guidPattern.Groups[1].Value.ToLowerInvariant();
            
            return guid;
        }

        public async Task<List<Mapper>> GetMappersByTargetLayoutGuidAsync(string targetLayoutGuid)
        {
            try
            {
                // Normalizar o GUID (remover prefixo LAY_ se houver)
                var normalizedGuid = NormalizeLayoutGuid(targetLayoutGuid);
                
                // Primeiro, buscar todos os mapeadores do cache permanente
                var allMappers = await GetAllMappersAsync();
                
                if (allMappers != null && allMappers.Any())
                {
                    // Filtrar mapeadores que têm este layout como destino
                    var matchingMappers = allMappers.Where(m =>
                    {
                        var mapperTargetGuid = m.TargetLayoutGuid ?? m.TargetLayoutGuidFromXml ?? "";
                        return GuidMatches(mapperTargetGuid, targetLayoutGuid) ||
                               GuidMatches(mapperTargetGuid, normalizedGuid);
                    }).ToList();

                    if (matchingMappers.Any())
                    {
                        _logger.LogInformation("Mapeadores com TargetLayoutGuid {Guid} encontrados no cache: {Count}", targetLayoutGuid, matchingMappers.Count);
                        return matchingMappers;
                    }
                }

                // Se não encontrou no cache, buscar do banco
                _logger.LogInformation("Mapeadores com TargetLayoutGuid {Guid} não encontrados no cache, buscando do banco", targetLayoutGuid);
                var mapper = await _mapperDatabaseService.GetMapperByTargetLayoutGuidAsync(targetLayoutGuid);
                
                var mappers = mapper != null ? new List<Mapper> { mapper } : new List<Mapper>();
                
                // Se encontrou no banco, atualizar o cache permanente
                if (mappers.Any())
                {
                    // Recarregar todos os mapeadores e atualizar cache
                    await RefreshCacheFromDatabaseAsync();
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
                _logger.LogInformation("🔄 Iniciando atualização do cache de mapeadores a partir do banco de dados");
                var mappers = await _mapperDatabaseService.GetAllMappersAsync();
                
                _logger.LogInformation("📊 Mapeadores encontrados no banco: {Count}", mappers?.Count ?? 0);
                
                if (mappers != null && mappers.Any())
                {
                    // Log dos primeiros mapeadores para debug
                    foreach (var mapper in mappers.Take(3))
                    {
                        _logger.LogInformation("  - Mapeador: {Name}, InputGuid: {InputGuid}, TargetGuid: {TargetGuid}", 
                            mapper.Name,
                            mapper.InputLayoutGuidFromXml ?? mapper.InputLayoutGuid ?? "null",
                            mapper.TargetLayoutGuidFromXml ?? mapper.TargetLayoutGuid ?? "null");
                    }
                    
                    // Popular cache permanente "mappers:search:all"
                    await _cacheService.SetAllCachedMappersAsync(mappers);
                    _logger.LogInformation("✅ Cache permanente de mapeadores atualizado: {Count} mapeadores", mappers.Count);
                }
                else
                {
                    _logger.LogWarning("⚠️ Nenhum mapeador encontrado no banco de dados para atualizar o cache");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erro ao atualizar cache de mapeadores a partir do banco de dados: {Message}", ex.Message);
                _logger.LogError(ex, "Stack trace: {StackTrace}", ex.StackTrace);
            }
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
                
                // Salvar no cache permanente
                if (mappers != null && mappers.Any())
                {
                    await _cacheService.SetAllCachedMappersAsync(mappers);
                    _logger.LogInformation("Mapeadores salvos no cache permanente: {Count}", mappers.Count);
                }

                return mappers ?? new List<Mapper>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar todos os mapeadores");
                return new List<Mapper>();
            }
        }
    }
}

