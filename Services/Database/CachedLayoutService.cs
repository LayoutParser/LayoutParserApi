using LayoutParserApi.Models.Database;
using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.Cache;

namespace LayoutParserApi.Services.Database
{
    public interface ICachedLayoutService
    {
        Task<LayoutSearchResponse> SearchLayoutsAsync(LayoutSearchRequest request);
        Task<LayoutRecord?> GetLayoutByIdAsync(int id);
        Task RefreshCacheFromDatabaseAsync();
        Task ClearCacheAsync();
    }

    public class CachedLayoutService : ICachedLayoutService
    {
        private readonly ILayoutDatabaseService _layoutDatabaseService;
        private readonly ILayoutCacheService _cacheService;
        private readonly ILogger<CachedLayoutService> _logger;

        public CachedLayoutService(
            ILayoutDatabaseService layoutDatabaseService,
            ILayoutCacheService cacheService,
            ILogger<CachedLayoutService> logger)
        {
            _layoutDatabaseService = layoutDatabaseService;
            _cacheService = cacheService;
            _logger = logger;
        }

        public async Task<LayoutSearchResponse> SearchLayoutsAsync(LayoutSearchRequest request)
        {
            try
            {
                // Normalizar SearchTerm: se vazio ou null, usar "all" como chave de cache
                var searchTerm = string.IsNullOrWhiteSpace(request.SearchTerm) ? "all" : request.SearchTerm;
                var cacheKey = searchTerm;
                
                _logger.LogInformation("Buscando layouts com termo: {SearchTerm}", searchTerm);

                // Tentar buscar no cache primeiro
                var cachedLayouts = await _cacheService.GetCachedLayoutsAsync(cacheKey);
                if (cachedLayouts != null)
                {
                    _logger.LogInformation("Retornando layouts do cache: {Count} layouts", cachedLayouts.Count);
                    return new LayoutSearchResponse
                    {
                        Success = true,
                        Layouts = cachedLayouts,
                        TotalFound = cachedLayouts.Count
                    };
                }

                // Se não estiver no cache, buscar no banco
                _logger.LogInformation("Buscando layouts no banco de dados...");
                var layouts = await _layoutDatabaseService.SearchLayoutsAsync(request);
                
                if (layouts.Success && layouts.Layouts.Any())
                    await _cacheService.SetCachedLayoutsAsync(cacheKey, layouts.Layouts);

                return layouts;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar layouts");
                return new LayoutSearchResponse
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        public async Task<LayoutRecord?> GetLayoutByIdAsync(int id)
        {
            try
            {
                _logger.LogInformation("Buscando layout por ID: {Id}", id);

                // Tentar buscar no cache primeiro
                var cachedLayout = await _cacheService.GetCachedLayoutByIdAsync(id);
                if (cachedLayout != null)
                {
                    _logger.LogInformation("Retornando layout do cache: {Name}", cachedLayout.Name);
                    return cachedLayout;
                }

                // Se não estiver no cache, buscar no banco
                var layout = await _layoutDatabaseService.GetLayoutByIdAsync(id);
                if (layout != null)
                    await _cacheService.SetCachedLayoutByIdAsync(id, layout);

                return layout;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar layout por ID: {Id}", id);
                return null;
            }
        }

        public async Task RefreshCacheFromDatabaseAsync()
        {
            try
            {
                _logger.LogInformation("Iniciando atualização do cache a partir do banco de dados");

                // Buscar todos os layouts do banco (sem filtro WHERE)
                var request = new LayoutSearchRequest
                {
                    SearchTerm = "", // String vazia = buscar todos os layouts
                    MaxResults = 1000
                };

                var response = await _layoutDatabaseService.SearchLayoutsAsync(request);
                
                if (response.Success && response.Layouts.Any())
                {
                    // Salvar no cache
                    await _cacheService.SetCachedLayoutsAsync("all", response.Layouts); // Usar "all" como chave de cache
                    
                    // Salvar layouts individuais no cache
                    foreach (var layout in response.Layouts)
                        await _cacheService.SetCachedLayoutByIdAsync(layout.Id, layout);
                    

                    _logger.LogInformation("Cache atualizado com {Count} layouts do banco de dados", response.Layouts.Count);
                }
                else
                {
                    _logger.LogWarning("Nenhum layout encontrado no banco para atualizar o cache");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao atualizar cache a partir do banco de dados");
            }
        }

        public async Task ClearCacheAsync()
        {
            try
            {
                await _cacheService.ClearCacheAsync();
                _logger.LogInformation("Cache limpo com sucesso");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao limpar cache");
            }
        }
    }
}
