using LayoutParserApi.Models.Database;
using LayoutParserApi.Services.Database;
using LayoutParserApi.Services.Transformation;
using Microsoft.AspNetCore.Mvc;

namespace LayoutParserApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TransformationController : ControllerBase
    {
        private readonly IMapperTransformationService _transformationService;
        private readonly ICachedMapperService _cachedMapperService;
        private readonly ICachedLayoutService _cachedLayoutService;
        private readonly ILogger<TransformationController> _logger;

        public TransformationController(
            IMapperTransformationService transformationService,
            ICachedMapperService cachedMapperService,
            ICachedLayoutService cachedLayoutService,
            ILogger<TransformationController> logger)
        {
            _transformationService = transformationService;
            _cachedMapperService = cachedMapperService;
            _cachedLayoutService = cachedLayoutService;
            _logger = logger;
        }

        /// <summary>
        /// Busca layouts de destino disponíveis para transformação a partir de um layout de entrada
        /// Pode receber GUID do layout (com ou sem prefixo LAY_) ou nome do layout
        /// </summary>
        [HttpGet("available-targets/{inputLayoutGuid}")]
        public async Task<IActionResult> GetAvailableTargetLayouts(string inputLayoutGuid)
        {
            try
            {
                _logger.LogInformation("🔍 ===== INÍCIO DA BUSCA DE LAYOUTS DE DESTINO =====");
                _logger.LogInformation("🔍 InputLayoutGuid recebido: {Guid}", inputLayoutGuid);
                
                // Tentar também buscar pelo nome do layout se o GUID não funcionar
                // Primeiro, vamos tentar com o GUID recebido

                // Normalizar o GUID recebido (pode vir com ou sem prefixo LAY_, com ou sem chaves {}, etc.)
                var normalizedInputGuid = NormalizeLayoutGuid(inputLayoutGuid);
                _logger.LogInformation("📝 GUID recebido: {OriginalGuid}, GUID normalizado: {NormalizedGuid}", inputLayoutGuid, normalizedInputGuid);
                
                // Extrair apenas a parte do GUID (sem prefixos) para comparação mais robusta
                var extractedInputGuid = ExtractGuidOnly(normalizedInputGuid);
                _logger.LogInformation("📝 GUID extraído (apenas parte do GUID): {ExtractedGuid}", extractedInputGuid);

                // Buscar todos os mapeadores do cache
                var allMappers = await _cachedMapperService.GetAllMappersAsync();
                _logger.LogInformation("📊 Total de mapeadores no cache: {Count}", allMappers?.Count ?? 0);
                
                if (allMappers == null || !allMappers.Any())
                {
                    _logger.LogWarning("⚠️ Nenhum mapeador encontrado no cache");
                    return Ok(new
                    {
                        success = true,
                        targets = new List<object>()
                    });
                }

                // Log detalhado dos primeiros mapeadores para debug
                _logger.LogInformation("📋 Primeiros 5 mapeadores no cache:");
                foreach (var mapper in allMappers.Take(5))
                {
                    var inputGuidFromXml = mapper.InputLayoutGuidFromXml ?? "null";
                    var inputGuidFromDb = mapper.InputLayoutGuid ?? "null";
                    var targetGuidFromXml = mapper.TargetLayoutGuidFromXml ?? "null";
                    var targetGuidFromDb = mapper.TargetLayoutGuid ?? "null";
                    
                    _logger.LogInformation("  - Mapeador: {Name} (ID: {Id})", mapper.Name, mapper.Id);
                    _logger.LogInformation("    InputGuid (DB): {InputDb}, InputGuid (XML): {InputXml}", inputGuidFromDb, inputGuidFromXml);
                    _logger.LogInformation("    TargetGuid (DB): {TargetDb}, TargetGuid (XML): {TargetXml}", targetGuidFromDb, targetGuidFromXml);
                    
                    // Verificar se este mapeador corresponde ao layout de entrada
                    var mapperInputGuid = mapper.InputLayoutGuidFromXml ?? mapper.InputLayoutGuid ?? "";
                    var normalizedMapperInputGuid = NormalizeLayoutGuid(mapperInputGuid);
                    var matches = GuidMatches(normalizedMapperInputGuid, normalizedInputGuid);
                    
                    if (matches)
                    {
                        _logger.LogInformation("    ✅ MATCH encontrado! InputGuid corresponde ao layout de entrada");
                    }
                }

                // Filtrar mapeadores que têm este layout como entrada
                // PRIORIZAR InputLayoutGuidFromXml (do XML descriptografado) sobre InputLayoutGuid (da coluna do banco)
                var matchingMappers = allMappers.Where(m =>
                {
                    // Usar InputLayoutGuidFromXml (do XML descriptografado) como fonte primária
                    var mapperInputGuid = m.InputLayoutGuidFromXml ?? m.InputLayoutGuid ?? "";
                    
                    // Se estiver vazio, pular este mapeador
                    if (string.IsNullOrEmpty(mapperInputGuid))
                    {
                        return false;
                    }
                    
                    var normalizedMapperInputGuid = NormalizeLayoutGuid(mapperInputGuid);
                    var extractedMapperInputGuid = ExtractGuidOnly(normalizedMapperInputGuid);
                    
                    // Comparar de múltiplas formas para garantir que encontramos o match correto
                    // O XML do mapeador pode ter InputLayoutGuid com prefixo LAY_ (ex: LAY_ad4fb6f4-9ff5-44fd-988b-3da5ed56b22c)
                    // Mas o LayoutGuid do layout pode não ter esse prefixo (ex: ad4fb6f4-9ff5-44fd-988b-3da5ed56b22c)
                    var matches = false;
                    
                    // 1. Comparação exata dos GUIDs extraídos (sem prefixos) - MAIS CONFIÁVEL
                    if (!string.IsNullOrEmpty(extractedMapperInputGuid) && !string.IsNullOrEmpty(extractedInputGuid))
                    {
                        matches = string.Equals(extractedMapperInputGuid, extractedInputGuid, StringComparison.OrdinalIgnoreCase);
                        if (matches)
                        {
                            _logger.LogInformation("    ✅ MATCH por GUID extraído: {ExtractedMapper} == {ExtractedInput}", 
                                extractedMapperInputGuid, extractedInputGuid);
                        }
                    }
                    
                    // 2. Comparação exata dos GUIDs normalizados (com prefixos removidos)
                    if (!matches)
                    {
                        matches = string.Equals(normalizedMapperInputGuid, normalizedInputGuid, StringComparison.OrdinalIgnoreCase);
                        if (matches)
                        {
                            _logger.LogInformation("    ✅ MATCH por GUID normalizado: {NormalizedMapper} == {NormalizedInput}", 
                                normalizedMapperInputGuid, normalizedInputGuid);
                        }
                    }
                    
                    // 3. Comparação flexível usando GuidMatches
                    if (!matches)
                    {
                        matches = GuidMatches(normalizedMapperInputGuid, normalizedInputGuid) ||
                                 GuidMatches(extractedMapperInputGuid, extractedInputGuid);
                        if (matches)
                        {
                            _logger.LogInformation("    ✅ MATCH por GuidMatches: {MapperGuid} matches {InputGuid}", 
                                mapperInputGuid, inputLayoutGuid);
                        }
                    }
                    
                    // 4. Comparação direta com o GUID original (caso venha em formato diferente)
                    if (!matches)
                    {
                        matches = string.Equals(normalizedMapperInputGuid, inputLayoutGuid, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(mapperInputGuid, inputLayoutGuid, StringComparison.OrdinalIgnoreCase);
                        if (matches)
                        {
                            _logger.LogInformation("    ✅ MATCH por comparação direta: {MapperGuid} == {InputGuid}", 
                                mapperInputGuid, inputLayoutGuid);
                        }
                    }
                    
                    if (matches)
                    {
                        _logger.LogInformation("✅ Mapeador encontrado: {Name} (ID: {Id})", m.Name, m.Id);
                        _logger.LogInformation("    InputGuid (XML): {InputXml}", m.InputLayoutGuidFromXml ?? "null");
                        _logger.LogInformation("    InputGuid (DB): {InputDb}", m.InputLayoutGuid ?? "null");
                        _logger.LogInformation("    TargetGuid (XML): {TargetXml}", m.TargetLayoutGuidFromXml ?? "null");
                        _logger.LogInformation("    TargetGuid (DB): {TargetDb}", m.TargetLayoutGuid ?? "null");
                    }
                    
                    return matches;
                }).ToList();

                _logger.LogInformation("🎯 Mapeadores encontrados para InputLayoutGuid {Guid}: {Count}", inputLayoutGuid, matchingMappers.Count);
                
                if (!matchingMappers.Any())
                {
                    _logger.LogWarning("⚠️ Nenhum mapeador encontrado na busca direta no cache para InputLayoutGuid: {Guid}", inputLayoutGuid);
                    _logger.LogWarning("⚠️ Tentando buscar diretamente do serviço...");
                    
                    // Tentar buscar diretamente do serviço (pode ter lógica adicional)
                    var mappersFromService = await _cachedMapperService.GetMappersByInputLayoutGuidAsync(inputLayoutGuid);
                    _logger.LogInformation("📊 Mapeadores retornados pelo serviço: {Count}", mappersFromService?.Count ?? 0);
                    
                    if (mappersFromService != null && mappersFromService.Any())
                    {
                        matchingMappers = mappersFromService;
                        _logger.LogInformation("✅ {Count} mapeador(es) encontrado(s) via serviço", matchingMappers.Count);
                    }
                    else
                    {
                        // Se ainda não encontrou, tentar buscar layouts pelo nome ou GUID parcial
                        _logger.LogWarning("⚠️ Tentando busca alternativa...");
                        
                        // Buscar todos os layouts para verificar se o GUID recebido corresponde a algum layout
                        var allLayoutsResponse = await _cachedLayoutService.SearchLayoutsAsync(new LayoutSearchRequest
                        {
                            SearchTerm = "all"
                        });
                        
                        if (allLayoutsResponse.Success && allLayoutsResponse.Layouts != null)
                        {
                            // Tentar encontrar o layout que corresponde ao GUID recebido
                            var matchingLayout = allLayoutsResponse.Layouts.FirstOrDefault(l =>
                            {
                                var layoutGuidStr = l.LayoutGuid.ToString() ?? "";
                                var normalizedLayoutGuid = NormalizeLayoutGuid(layoutGuidStr);
                                var extractedLayoutGuid = ExtractGuidOnly(normalizedLayoutGuid);
                                
                                return 
                                    string.Equals(normalizedLayoutGuid, normalizedInputGuid, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(extractedLayoutGuid, extractedInputGuid, StringComparison.OrdinalIgnoreCase) ||
                                    GuidMatches(normalizedLayoutGuid, normalizedInputGuid) ||
                                    GuidMatches(extractedLayoutGuid, extractedInputGuid);
                            });
                            
                            if (matchingLayout != null)
                            {
                                _logger.LogInformation("✅ Layout encontrado: {Name} (ID: {Id}) - GUID: {Guid}", 
                                    matchingLayout.Name, matchingLayout.Id, matchingLayout.LayoutGuid.ToString() ?? "null");
                                
                                // Tentar buscar mapeadores novamente com o GUID do layout encontrado
                                var layoutGuidForSearch = matchingLayout.LayoutGuid.ToString() ?? inputLayoutGuid;
                                mappersFromService = await _cachedMapperService.GetMappersByInputLayoutGuidAsync(layoutGuidForSearch);
                                
                                if (mappersFromService != null && mappersFromService.Any())
                                {
                                    matchingMappers = mappersFromService;
                                    _logger.LogInformation("✅ {Count} mapeador(es) encontrado(s) usando GUID do layout encontrado", matchingMappers.Count);
                                }
                            }
                        }
                    }
                    
                    if (!matchingMappers.Any())
                    {
                        _logger.LogWarning("⚠️ Nenhum mapeador encontrado após todas as tentativas para InputLayoutGuid: {Guid}", inputLayoutGuid);
                        _logger.LogWarning("⚠️ GUID normalizado: {NormalizedGuid}, GUID extraído: {ExtractedGuid}", normalizedInputGuid, extractedInputGuid);
                        
                        // Log de todos os InputLayoutGuids disponíveis nos mapeadores para debug
                        _logger.LogInformation("📋 Listando todos os InputLayoutGuids disponíveis nos mapeadores:");
                        foreach (var mapper in allMappers.Take(10))
                        {
                            var inputXml = mapper.InputLayoutGuidFromXml ?? "null";
                            var inputDb = mapper.InputLayoutGuid ?? "null";
                            var normalizedXml = NormalizeLayoutGuid(inputXml);
                            var normalizedDb = NormalizeLayoutGuid(inputDb);
                            var extractedXml = ExtractGuidOnly(normalizedXml);
                            var extractedDb = ExtractGuidOnly(normalizedDb);
                            
                            _logger.LogInformation("  - Mapeador: {Name} - InputGuid (XML): {InputXml} (norm: {NormXml}, extr: {ExtrXml})", 
                                mapper.Name, inputXml, normalizedXml, extractedXml);
                            _logger.LogInformation("    InputGuid (DB): {InputDb} (norm: {NormDb}, extr: {ExtrDb})", 
                                inputDb, normalizedDb, extractedDb);
                        }
                        
                        return Ok(new
                        {
                            success = true,
                            targets = new List<object>(),
                            message = $"Nenhum mapeador encontrado para o layout: {inputLayoutGuid}",
                            debug = new
                            {
                                inputLayoutGuid = inputLayoutGuid,
                                normalizedInputGuid = normalizedInputGuid,
                                extractedInputGuid = extractedInputGuid,
                                totalMappers = allMappers.Count
                            }
                        });
                    }
                }

                // Buscar layouts do cache
                var layoutsResponse = await _cachedLayoutService.SearchLayoutsAsync(new LayoutSearchRequest
                {
                    SearchTerm = "all"
                });

                if (!layoutsResponse.Success || layoutsResponse.Layouts == null)
                {
                    _logger.LogError("❌ Não foi possível buscar layouts do cache");
                    return StatusCode(500, new { error = "Não foi possível buscar layouts do cache" });
                }

                _logger.LogInformation("📊 Total de layouts no cache: {Count}", layoutsResponse.Layouts.Count());

                // Filtrar layouts de destino baseado nos mapeadores
                // PRIORIZAR TargetLayoutGuidFromXml (do XML descriptografado) sobre TargetLayoutGuid (da coluna do banco)
                var targetLayoutGuids = matchingMappers
                    .Select(m =>
                    {
                        // Usar TargetLayoutGuidFromXml (do XML descriptografado) como fonte primária
                        var targetGuid = m.TargetLayoutGuidFromXml ?? m.TargetLayoutGuid ?? "";
                        var normalized = NormalizeLayoutGuid(targetGuid);
                        _logger.LogInformation("🎯 TargetGuid encontrado: {TargetGuid} (normalizado: {Normalized}) para mapeador {MapperName}", 
                            targetGuid, normalized, m.Name);
                        return normalized;
                    })
                    .Where(g => !string.IsNullOrEmpty(g))
                    .Distinct()
                    .ToList();

                _logger.LogInformation("🎯 GUIDs de destino únicos encontrados: {Count} - {Guids}", 
                    targetLayoutGuids.Count, string.Join(", ", targetLayoutGuids));

                // Buscar layouts que correspondem aos GUIDs de destino
                var targetLayouts = layoutsResponse.Layouts
                    .Where(l =>
                    {
                        // Converter Guid para string (pode vir em diferentes formatos)
                        var layoutGuidStr = l.LayoutGuid != null ? l.LayoutGuid.ToString() : "";
                        var normalizedLayoutGuid = NormalizeLayoutGuid(layoutGuidStr);
                        var extractedLayoutGuid = ExtractGuidOnly(normalizedLayoutGuid);
                        
                        // Comparar com cada GUID de destino
                        var matches = targetLayoutGuids.Any(tg =>
                        {
                            var normalizedTargetGuid = NormalizeLayoutGuid(tg);
                            var extractedTargetGuid = ExtractGuidOnly(normalizedTargetGuid);
                            
                            // Múltiplas formas de comparação
                            return 
                                // Comparação exata
                                string.Equals(normalizedLayoutGuid, normalizedTargetGuid, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(extractedLayoutGuid, extractedTargetGuid, StringComparison.OrdinalIgnoreCase) ||
                                // Comparação flexível
                                GuidMatches(normalizedLayoutGuid, normalizedTargetGuid) ||
                                GuidMatches(extractedLayoutGuid, extractedTargetGuid) ||
                                // Comparação reversa (caso um tenha prefixo e outro não)
                                (!string.IsNullOrEmpty(extractedLayoutGuid) && normalizedTargetGuid.Contains(extractedLayoutGuid, StringComparison.OrdinalIgnoreCase)) ||
                                (!string.IsNullOrEmpty(extractedTargetGuid) && normalizedLayoutGuid.Contains(extractedTargetGuid, StringComparison.OrdinalIgnoreCase));
                        });
                        
                        if (matches)
                        {
                            _logger.LogInformation("✅ Layout de destino encontrado: {Name} (ID: {Id}) - GUID: {Guid} (normalizado: {Normalized})", 
                                l.Name, l.Id, layoutGuidStr, normalizedLayoutGuid);
                        }
                        
                        return matches;
                    })
                    .Select(l => new
                    {
                        id = l.Id,
                        guid = l.LayoutGuid != null ? l.LayoutGuid.ToString() : "",
                        name = l.Name,
                        description = l.Description
                    })
                    .ToList();

                _logger.LogInformation("✅ Layouts de destino encontrados: {Count}", targetLayouts.Count);

                return Ok(new
                {
                    success = true,
                    targets = targetLayouts,
                    inputLayoutGuid = inputLayoutGuid,
                    normalizedInputGuid = normalizedInputGuid,
                    mappersFound = matchingMappers.Count,
                    targetGuids = targetLayoutGuids
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erro ao buscar layouts de destino: {Message}", ex.Message);
                _logger.LogError(ex, "Stack trace: {StackTrace}", ex.StackTrace);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Transforma texto usando mapeador (txt para xml ou xml para xml)
        /// </summary>
        [HttpPost("transform")]
        public async Task<IActionResult> Transform([FromBody] TransformRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.InputText))
                {
                    return BadRequest(new { error = "InputText é obrigatório" });
                }

                if (string.IsNullOrEmpty(request.InputLayoutGuid))
                {
                    return BadRequest(new { error = "InputLayoutGuid é obrigatório" });
                }

                if (string.IsNullOrEmpty(request.TargetLayoutGuid))
                {
                    return BadRequest(new { error = "TargetLayoutGuid é obrigatório" });
                }

                _logger.LogInformation("Iniciando transformação: InputGuid={InputGuid}, TargetGuid={TargetGuid}",
                    request.InputLayoutGuid, request.TargetLayoutGuid);

                var result = await _transformationService.TransformAsync(
                    request.InputText,
                    request.InputLayoutGuid,
                    request.TargetLayoutGuid,
                    request.MapperXml);

                if (!result.Success)
                {
                    _logger.LogWarning("Transformação falhou: {ErrorCount} erro(s), {WarningCount} aviso(s)", 
                        result.Errors?.Count ?? 0, result.Warnings?.Count ?? 0);
                    
                    if (result.Errors != null && result.Errors.Any())
                    {
                        foreach (var error in result.Errors)
                        {
                            _logger.LogError("Erro: {Error}", error);
                        }
                    }
                    
                    return BadRequest(new
                    {
                        success = false,
                        errors = result.Errors ?? new List<string>(),
                        warnings = result.Warnings ?? new List<string>()
                    });
                }

                _logger.LogInformation("Transformação concluída com sucesso. IntermediateXml: {IntermediateSize} chars, FinalXml: {FinalSize} chars", 
                    result.IntermediateXml?.Length ?? 0, result.FinalXml?.Length ?? 0);

                return Ok(new
                {
                    success = true,
                    intermediateXml = result.IntermediateXml,
                    finalXml = result.FinalXml,
                    warnings = result.Warnings ?? new List<string>()
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro durante transformação: {Message}", ex.Message);
                _logger.LogError(ex, "Stack trace: {StackTrace}", ex.StackTrace);
                return StatusCode(500, new { 
                    success = false,
                    error = ex.Message,
                    errors = new List<string> { ex.Message }
                });
            }
        }

        private static string NormalizeLayoutGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid))
                return "";
            
            // Remover prefixos comuns (LAY_, TAG_, GRT_, etc.) se houver
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

        private static bool GuidMatches(string guid1, string guid2)
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
            {
                if (string.Equals(guid1Only, guid2Only, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            
            // Comparação parcial (caso um tenha prefixo e outro não)
            // Ex: "50efd04d-8604-45fd-88ad-c7c5111cc127" contém "50efd04d-8604-45fd-88ad-c7c5111cc127"
            if (norm1.Contains(norm2, StringComparison.OrdinalIgnoreCase) || 
                norm2.Contains(norm1, StringComparison.OrdinalIgnoreCase))
            {
                // Verificar se não é apenas uma coincidência parcial
                // Garantir que pelo menos um dos GUIDs extraídos é válido
                if (!string.IsNullOrEmpty(guid1Only) || !string.IsNullOrEmpty(guid2Only))
                    return true;
            }
            
            // Comparação reversa: verificar se um GUID contém o outro (útil para GUIDs com prefixos diferentes)
            // Ex: "lay_50efd04d-8604-45fd-88ad-c7c5111cc127" contém "50efd04d-8604-45fd-88ad-c7c5111cc127"
            if (!string.IsNullOrEmpty(guid1Only) && norm2.Contains(guid1Only, StringComparison.OrdinalIgnoreCase))
                return true;
            
            if (!string.IsNullOrEmpty(guid2Only) && norm1.Contains(guid2Only, StringComparison.OrdinalIgnoreCase))
                return true;
            
            return false;
        }

        private static string ExtractGuidOnly(string guid)
        {
            if (string.IsNullOrEmpty(guid))
                return "";
            
            // Tentar extrair apenas a parte do GUID (formato: xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx)
            // Isso funciona mesmo se o GUID estiver com prefixo como "LAY_" ou outros
            var guidPattern = System.Text.RegularExpressions.Regex.Match(guid, 
                @"([a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})", 
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            
            if (guidPattern.Success)
            {
                var extractedGuid = guidPattern.Groups[1].Value.ToLowerInvariant();
                return extractedGuid;
            }
            
            // Se não encontrou padrão GUID, retornar o valor normalizado
            return guid;
        }
    }

    public class TransformRequest
    {
        public string InputText { get; set; }
        public string InputLayoutGuid { get; set; }
        public string TargetLayoutGuid { get; set; }
        public string MapperXml { get; set; }
    }
}
