using LayoutParserApi.Models.Entities;
using LayoutParserApi.Services.Cache;
using LayoutParserApi.Services.Interfaces;

using System.Linq;

namespace LayoutParserApi.Services.Database
{
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
                _logger.LogInformation("Buscando mapeadores por InputLayoutGuid: {Guid}", inputLayoutGuid);
                
                // Normalizar o GUID (remover prefixo LAY_ se houver)
                var normalizedGuid = NormalizeLayoutGuid(inputLayoutGuid);
                _logger.LogInformation("GUID normalizado: {NormalizedGuid}", normalizedGuid);
                
                // Primeiro, buscar todos os mapeadores do cache permanente
                var allMappers = await GetAllMappersAsync();
                
                if (allMappers != null && allMappers.Any())
                {
                    _logger.LogInformation("Total de mapeadores no cache: {Count}", allMappers.Count);
                    
                    // Filtrar mapeadores que têm este layout como entrada
                    // PRIORIZAR InputLayoutGuidFromXml (do XML descriptografado) sobre InputLayoutGuid (da coluna do banco)
                    var matchingMappers = allMappers.Where(m =>
                    {
                        // Usar InputLayoutGuidFromXml (do XML descriptografado) como fonte primária
                        var mapperInputGuid = m.InputLayoutGuidFromXml ?? m.InputLayoutGuid ?? "";
                        var normalizedMapperInputGuid = NormalizeLayoutGuid(mapperInputGuid);
                        
                        // Comparar com o GUID normalizado recebido
                        var matches = GuidMatches(normalizedMapperInputGuid, normalizedGuid) || GuidMatches(normalizedMapperInputGuid, inputLayoutGuid);
                        
                        if (matches)
                            _logger.LogInformation("Mapeador encontrado: {Name} (ID: {Id}) - InputGuid (XML): {InputXml}, InputGuid (DB): {InputDb}", m.Name, m.Id, m.InputLayoutGuidFromXml ?? "null", m.InputLayoutGuid ?? "null");
                        
                        
                        return matches;
                    }).ToList();

                    if (matchingMappers.Any())
                    {
                        _logger.LogInformation("Mapeadores com InputLayoutGuid {Guid} encontrados no cache: {Count}", inputLayoutGuid, matchingMappers.Count);
                        
                        // Log dos mapeadores encontrados
                        foreach (var mapper in matchingMappers)
                            _logger.LogInformation("  - {Name} (ID: {Id}) - TargetGuid (XML): {TargetXml}", mapper.Name, mapper.Id, mapper.TargetLayoutGuidFromXml ?? "null");
                        
                        
                        return matchingMappers;
                    }
                    else
                    {
                        _logger.LogWarning("Nenhum mapeador encontrado no cache para InputLayoutGuid: {Guid}", inputLayoutGuid);
                        _logger.LogWarning("Verificando primeiros 3 mapeadores para debug:");
                        
                        foreach (var mapper in allMappers.Take(3))
                        {
                            var mapperInputGuid = mapper.InputLayoutGuidFromXml ?? mapper.InputLayoutGuid ?? "null";
                            var normalizedMapperInputGuid = NormalizeLayoutGuid(mapperInputGuid);
                            _logger.LogWarning("  - {Name} - InputGuid: {InputGuid} (normalizado: {Normalized})", mapper.Name, mapperInputGuid, normalizedMapperInputGuid);
                        }
                    }
                }

                // Se não encontrou no cache, buscar do banco
                _logger.LogInformation("Mapeadores com InputLayoutGuid {Guid} nao encontrados no cache, buscando do banco", inputLayoutGuid);
                var mapperFromDb = await _mapperDatabaseService.GetMapperByInputLayoutGuidAsync(inputLayoutGuid);
                
                var mappers = mapperFromDb != null ? new List<Mapper> { mapperFromDb } : new List<Mapper>();
                
                // Se encontrou no banco, atualizar o cache permanente
                if (mappers.Any())
                {
                    _logger.LogInformation("Mapeador encontrado no banco, atualizando cache...");
                    // Recarregar todos os mapeadores e atualizar cache
                    await RefreshCacheFromDatabaseAsync();
                }
                else
                    _logger.LogWarning("Nenhum mapeador encontrado no banco para InputLayoutGuid: {Guid}", inputLayoutGuid);

                return mappers;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar mapeadores por InputLayoutGuid: {Guid} - {Message}", inputLayoutGuid, ex.Message);
                _logger.LogError(ex, "Stack trace: {StackTrace}", ex.StackTrace);
                return new List<Mapper>();
            }
        }

        private string NormalizeLayoutGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid))
                return "";
            
            // Remover prefixos comuns (LAY_, TAG_, GRT_, MAP_, etc.) se houver
            var normalized = guid.Trim();
            
            // Remover prefixo LAY_ se houver (case-insensitive)
            if (normalized.StartsWith("LAY_", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(4);
            
            // Remover prefixo MAP_ se houver (para mapeadores)
            if (normalized.StartsWith("MAP_", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(4);
            
            // Remover espaços e converter para minúsculas para comparação
            normalized = normalized.Trim().ToLowerInvariant();
            
            // Remover chaves {} se houver (caso venha de Guid.ToString())
            normalized = normalized.Replace("{", "").Replace("}", "");
            
            // Remover espaços em branco extras
            normalized = normalized.Trim();
            
            return normalized;
        }

        private bool GuidMatches(string guid1, string guid2)
        {
            if (string.IsNullOrEmpty(guid1) || string.IsNullOrEmpty(guid2))
                return false;
            
            // Normalizar ambos os GUIDs
            var norm1 = NormalizeLayoutGuid(guid1);
            var norm2 = NormalizeLayoutGuid(guid2);
            
            // Comparação exata após normalização
            if (string.Equals(norm1, norm2, StringComparison.OrdinalIgnoreCase))
                return true;
            
            // Tentar extrair apenas a parte do GUID (sem prefixo LAY_ ou outros)
            // Ex: "LAY_50efd04d-8604-45fd-88ad-c7c5111cc127" vs "50efd04d-8604-45fd-88ad-c7c5111cc127"
            var guid1Only = ExtractGuidOnly(norm1);
            var guid2Only = ExtractGuidOnly(norm2);
            
            // Se conseguiu extrair GUIDs válidos, comparar
            if (!string.IsNullOrEmpty(guid1Only) && !string.IsNullOrEmpty(guid2Only))
                if (string.Equals(guid1Only, guid2Only, StringComparison.OrdinalIgnoreCase))
                    return true;
            
            
            // Comparação parcial (caso um tenha prefixo e outro não)
            // Ex: "50efd04d-8604-45fd-88ad-c7c5111cc127" contém "50efd04d-8604-45fd-88ad-c7c5111cc127"
            if (norm1.Contains(norm2, StringComparison.OrdinalIgnoreCase) || norm2.Contains(norm1, StringComparison.OrdinalIgnoreCase))
                // Verificar se não é apenas uma coincidência parcial (ex: "50" contém "50")
                // Garantir que pelo menos um dos GUIDs extraídos é válido
                if (!string.IsNullOrEmpty(guid1Only) || !string.IsNullOrEmpty(guid2Only))
                    return true;
            
            return false;
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

                        return GuidMatches(mapperTargetGuid, targetLayoutGuid) || GuidMatches(mapperTargetGuid, normalizedGuid);

                    }).ToList();

                    if (matchingMappers.Any())
                    {
                        _logger.LogInformation("Mapeadores com TargetLayoutGuid {Guid} encontrados no cache: {Count}", targetLayoutGuid, matchingMappers.Count);
                        return matchingMappers;
                    }
                }

                // Se não encontrou no cache, buscar do banco
                _logger.LogInformation("Mapeadores com TargetLayoutGuid {Guid} não encontrados no cache, buscando do banco", targetLayoutGuid);
                var mapperFromDb = await _mapperDatabaseService.GetMapperByTargetLayoutGuidAsync(targetLayoutGuid);
                
                var mappers = mapperFromDb != null ? new List<Mapper> { mapperFromDb } : new List<Mapper>();
                
                // Se encontrou no banco, atualizar o cache permanente
                if (mappers.Any())
                    // Recarregar todos os mapeadores e atualizar cache
                    await RefreshCacheFromDatabaseAsync();

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
                _logger.LogInformation("Iniciando atualizacao do cache de mapeadores a partir do banco de dados");
                var mappers = await _mapperDatabaseService.GetAllMappersAsync();
                
                _logger.LogInformation("Mapeadores encontrados no banco: {Count}", mappers?.Count ?? 0);
                
                if (mappers != null && mappers.Any())
                {
                    // Log dos primeiros mapeadores para debug
                    foreach (var mapper in mappers.Take(3))
                        _logger.LogInformation("  - Mapeador: {Name}, InputGuid: {InputGuid}, TargetGuid: {TargetGuid}", mapper.Name,mapper.InputLayoutGuidFromXml ?? mapper.InputLayoutGuid ?? "null",mapper.TargetLayoutGuidFromXml ?? mapper.TargetLayoutGuid ?? "null");
                    
                    
                    // Popular cache permanente "mappers:search:all"
                    await _cacheService.SetAllCachedMappersAsync(mappers);
                    _logger.LogInformation("Cache permanente de mapeadores atualizado: {Count} mapeadores", mappers.Count);
                }
                else
                    _logger.LogWarning("Nenhum mapeador encontrado no banco de dados para atualizar o cache");
                
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao atualizar cache de mapeadores a partir do banco de dados: {Message}", ex.Message);
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

