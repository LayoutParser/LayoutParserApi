using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

using XslSynth.Core.StructuralResolution;

namespace LayoutParserApi.Services.Transformation.StructuralResolution
{
    /// <summary>
    /// Item 2 (parte de cache) da divisão de trabalho da issue #140 (design §2.3, §8): o parse do
    /// XSD NF-e via <see cref="XmlLayoutStructureParser"/> compila um <c>XmlSchemaSet</c> inteiro —
    /// caro para rodar a cada request. Cacheia o <see cref="XmlLayoutCatalog"/> resultante, chaveado
    /// por <c>TargetLayoutGuid</c> (hoje só NF-e, mas a chave já inclui o tipo de documento para não
    /// exigir mudança de forma quando outro tipo entrar).
    ///
    /// Mesmo padrão de cache opcional já usado no projeto (<c>MapperCacheService</c>/
    /// <c>CachedLayoutService</c>): aqui a "opcionalidade" é sobre o PRÓPRIO catálogo — se o XSD não
    /// está configurado/legível/inválido, o serviço loga e devolve <c>null</c>, nunca lança. Quem
    /// chama (<see cref="FieldMappingCompositionService"/>) já sabe degradar para "sem field mappings
    /// nesta resposta" sem derrubar o request principal (dotnet-standards.md, resiliência).
    /// </summary>
    public class StructuralXmlCatalogCacheService
    {
        // Só NF-e é suportado por ora (decisão do dono, 2026-08-27) — a chave já carrega o tipo de
        // documento para não exigir migração de forma quando um segundo tipo (CT-e etc.) for
        // adicionado, mesmo que hoje todo TargetLayoutGuid caia no mesmo catálogo NF-e.
        private const string NfeDocumentType = "NFe";

        private readonly IMemoryCache _cache;
        private readonly StructuralResolutionOptions _options;
        private readonly ILogger<StructuralXmlCatalogCacheService> _logger;

        public StructuralXmlCatalogCacheService(
            IMemoryCache cache,
            IOptions<StructuralResolutionOptions> options,
            ILogger<StructuralXmlCatalogCacheService> logger)
        {
            _cache = cache;
            _options = options.Value;
            _logger = logger;
        }

        /// <summary>Resolve (do cache, ou construindo e cacheando) o catálogo XML de destino para o
        /// <paramref name="targetLayoutGuid"/> informado. Retorna <c>null</c> quando o XSD não está
        /// configurado/disponível neste host — degrada graciosamente.</summary>
        public XmlLayoutCatalog? GetOrBuildCatalog(string? targetLayoutGuid)
        {
            if (string.IsNullOrWhiteSpace(_options.NfeSchemaPath))
            {
                _logger.LogWarning(
                    "StructuralResolution:NfeSchemaPath não configurado — catálogo XML de destino indisponível (TargetLayoutGuid={TargetLayoutGuid})",
                    targetLayoutGuid);
                return null;
            }

            var cacheKey = $"structural-xml-catalog:{NfeDocumentType}:{targetLayoutGuid ?? "default"}";
            if (_cache.TryGetValue(cacheKey, out XmlLayoutCatalog? cached))
            {
                return cached;
            }

            try
            {
                var parser = new XmlLayoutStructureParser();
                var root = parser.Parse(_options.NfeSchemaPath, _options.NfeRootElementName);
                var catalog = new XmlLayoutCatalog(root);

                // Estático por versão de layout/XSD (design §2.3) — sem expiração por tempo; quem
                // precisar invalidar (troca de XSD em disco) reinicia o processo, mesmo padrão de
                // outros catálogos carregados uma vez por host.
                _cache.Set(cacheKey, catalog, new MemoryCacheEntryOptions
                {
                    Priority = CacheItemPriority.NeverRemove
                });

                _logger.LogInformation(
                    "Catálogo XML de destino (NF-e) construído e cacheado para TargetLayoutGuid={TargetLayoutGuid}",
                    targetLayoutGuid);
                return catalog;
            }
            catch (Exception ex)
            {
                // XSD ausente/corrompido/mal configurado é uma falha de host, não deve derrubar a
                // composição de field mappings (dotnet-standards.md) — quem chama trata null como
                // "sem catálogo disponível" e devolve mapeamentos vazios/best-effort.
                _logger.LogError(ex,
                    "Falha ao construir o catálogo XML de destino (NF-e) a partir de {SchemaPath} — field mappings indisponíveis para TargetLayoutGuid={TargetLayoutGuid}",
                    _options.NfeSchemaPath, targetLayoutGuid);
                return null;
            }
        }
    }
}
