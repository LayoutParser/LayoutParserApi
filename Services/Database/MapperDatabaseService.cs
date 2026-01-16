using LayoutParserApi.Models.Entities;
using LayoutParserApi.Services.Interfaces;

using Microsoft.Data.SqlClient;

using System.Data;
using System.Xml.Linq;

namespace LayoutParserApi.Services.Database
{
    /// <summary>
    /// Serviço para buscar mapeadores do banco de dados
    /// </summary>
    public class MapperDatabaseService
    {
        private readonly ILogger<MapperDatabaseService> _logger;
        private readonly IDecryptionService _decryptionService;
        private readonly string _connectionString;

        public MapperDatabaseService(
            ILogger<MapperDatabaseService> logger,
            IDecryptionService decryptionService,
            IConfiguration configuration)
        {
            _logger = logger;
            _decryptionService = decryptionService;
            var server = configuration["Database:Server"];
            var database = configuration["Database:Database"];
            var userId = configuration["Database:UserId"];
            var password = configuration["Database:Password"];

            _connectionString = $"Server={server};Database={database};User Id={userId};Password={password};TrustServerCertificate=True;";
        }


        /// <summary>
        /// Busca todos os mapeadores
        /// </summary>
        public async Task<List<Mapper>> GetAllMappersAsync()
        {
            var mappers = new List<Mapper>();

            try
            {
                _logger.LogInformation("Conectando ao banco de dados para buscar todos os mapeadores...");
                _logger.LogInformation("Connection string: Server={Server}, Database={Database}",
                    _connectionString.Contains("Server=") ? _connectionString.Split(';').FirstOrDefault(s => s.StartsWith("Server=")) : "N/A",
                    _connectionString.Contains("Database=") ? _connectionString.Split(';').FirstOrDefault(s => s.StartsWith("Database=")) : "N/A");

                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();
                _logger.LogInformation("Conexao com banco de dados estabelecida");

                var query = @"
                    SELECT 
                        [Id], [MapperGuid], [PackageGuid], [Name], [Description],
                        [IsXPathMapper], [InputLayoutGuid], [TargetLayoutGuid],
                        [ValueContent], [ProjectId], [LastUpdateDate]
                    FROM [ConnectUS_Macgyver].[dbo].[tbMapper]
                    ORDER BY [LastUpdateDate] DESC";

                _logger.LogInformation("Executando query para buscar mapeadores...");
                using var command = new SqlCommand(query, connection);
                using var reader = await command.ExecuteReaderAsync();

                int count = 0;
                int errorCount = 0;
                while (await reader.ReadAsync())
                {
                    try
                    {
                        var mapper = MapReaderToMapper(reader);
                        mappers.Add(mapper);
                        count++;

                        if (count <= 3)
                            _logger.LogInformation("  - Mapeador {Count}: Id={Id}, Name={Name}, InputGuid={InputGuid}, TargetGuid={TargetGuid}",count, mapper.Id, mapper.Name,mapper.InputLayoutGuidFromXml ?? mapper.InputLayoutGuid ?? "null",mapper.TargetLayoutGuidFromXml ?? mapper.TargetLayoutGuid ?? "null");
                        
                    }
                    catch (Exception ex)
                    {
                        errorCount++;
                        _logger.LogError(ex, "Erro ao mapear mapeador (linha {Count}): {Message}", count + 1, ex.Message);
                        _logger.LogError(ex, "Stack trace: {StackTrace}", ex.StackTrace);
                        // Continuar processando os próximos mapeadores mesmo se um falhar
                    }
                }

                if (errorCount > 0)
                    _logger.LogWarning("Total de erros ao mapear mapeadores: {ErrorCount} de {Total}", errorCount, count + errorCount);
                

                _logger.LogInformation("Total de mapeadores encontrados: {Count}", mappers.Count);
                return mappers;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar todos os mapeadores: {Message}", ex.Message);
                _logger.LogError(ex, "Stack trace: {StackTrace}", ex.StackTrace);
                return mappers;
            }
        }

        /// <summary>
        /// Busca mapeadores por InputLayoutGuid ou TargetLayoutGuid
        /// </summary>
        public async Task<List<Mapper>> GetMappersByLayoutGuidAsync(string layoutGuid)
        {
            var mappers = new List<Mapper>();

            try
            {
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();

                var query = @"
                    SELECT 
                        [Id], [MapperGuid], [PackageGuid], [Name], [Description],
                        [IsXPathMapper], [InputLayoutGuid], [TargetLayoutGuid],
                        [ValueContent], [ProjectId], [LastUpdateDate]
                    FROM [ConnectUS_Macgyver].[dbo].[tbMapper]
                    WHERE [InputLayoutGuid] = @LayoutGuid 
                       OR [TargetLayoutGuid] = @LayoutGuid
                    ORDER BY [LastUpdateDate] DESC";

                using var command = new SqlCommand(query, connection);
                command.Parameters.AddWithValue("@LayoutGuid", layoutGuid);

                using var reader = await command.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    var mapper = MapReaderToMapper(reader);

                    // Verificar se o layoutGuid corresponde ao InputLayoutGuid ou TargetLayoutGuid
                    // Tanto das colunas quanto do XML descriptografado
                    bool matches = false;

                    // Verificar colunas do banco
                    if (mapper.InputLayoutGuid == layoutGuid || mapper.TargetLayoutGuid == layoutGuid)
                        matches = true;

                    // Verificar XML descriptografado (mais confiável)
                    if (!string.IsNullOrEmpty(mapper.InputLayoutGuidFromXml) && mapper.InputLayoutGuidFromXml == layoutGuid)
                        matches = true;

                    if (!string.IsNullOrEmpty(mapper.TargetLayoutGuidFromXml) && mapper.TargetLayoutGuidFromXml == layoutGuid)
                        matches = true;

                    if (matches)
                        mappers.Add(mapper);
                }

                return mappers;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar mapeadores por LayoutGuid: {LayoutGuid}", layoutGuid);
                return mappers;
            }
        }

        /// <summary>
        /// Busca mapeador onde o layoutGuid é o InputLayoutGuid (entrada)
        /// </summary>
        public async Task<Mapper> GetMapperByInputLayoutGuidAsync(string inputLayoutGuid)
        {
            var mappers = await GetMappersByLayoutGuidAsync(inputLayoutGuid);
            return mappers.FirstOrDefault(m => m.InputLayoutGuid == inputLayoutGuid || m.InputLayoutGuidFromXml == inputLayoutGuid);
        }

        /// <summary>
        /// Busca mapeador onde o layoutGuid é o TargetLayoutGuid (saída)
        /// </summary>
        public async Task<Mapper> GetMapperByTargetLayoutGuidAsync(string targetLayoutGuid)
        {
            var mappers = await GetMappersByLayoutGuidAsync(targetLayoutGuid);
            return mappers.FirstOrDefault(m => m.TargetLayoutGuid == targetLayoutGuid || m.TargetLayoutGuidFromXml == targetLayoutGuid);
        }

        /// <summary>
        /// Busca o "melhor" mapeador para um layoutGuid, restrito a ProjectId e uma lista de PackageGuids permitidos.
        /// Prioriza mappers onde o layoutGuid é InputLayoutGuid; se não encontrar, tenta TargetLayoutGuid.
        /// </summary>
        public async Task<Mapper?> GetBestMapperForLayoutGuidAsync(string layoutGuid, int projectId, IReadOnlyCollection<string> allowedPackageGuids)
        {
            var candidates = await GetMappersByLayoutGuidForPackagesAsync(layoutGuid, projectId, allowedPackageGuids);
            if (candidates.Count == 0)
                return null;

            string Normalize(string guid)
            {
                if (string.IsNullOrWhiteSpace(guid)) return "";
                var g = guid.Trim();
                if (g.StartsWith("LAY_", StringComparison.OrdinalIgnoreCase)) g = g.Substring(4);
                return g.Trim().ToLowerInvariant();
            }

            var wanted = Normalize(layoutGuid);

            // Preferência: input match
            var inputMatch = candidates
                .Where(m => Normalize(m.InputLayoutGuidFromXml ?? m.InputLayoutGuid ?? "") == wanted)
                .OrderByDescending(m => m.LastUpdateDate)
                .FirstOrDefault();
            if (inputMatch != null)
                return inputMatch;

            var targetMatch = candidates
                .Where(m => Normalize(m.TargetLayoutGuidFromXml ?? m.TargetLayoutGuid ?? "") == wanted)
                .OrderByDescending(m => m.LastUpdateDate)
                .FirstOrDefault();
            if (targetMatch != null)
                return targetMatch;

            // Fallback: mais recente
            return candidates.OrderByDescending(m => m.LastUpdateDate).FirstOrDefault();
        }

        private static string NormalizePackageGuid(string packageGuid)
        {
            if (string.IsNullOrWhiteSpace(packageGuid)) return "";
            var p = packageGuid.Trim();
            if (p.StartsWith("PAC_", StringComparison.OrdinalIgnoreCase)) p = p.Substring(4);
            return p.Trim().ToLowerInvariant();
        }

        /// <summary>
        /// Busca mapeadores por layoutGuid restringindo por ProjectId e PackageGuid (lista permitida).
        /// </summary>
        public async Task<List<Mapper>> GetMappersByLayoutGuidForPackagesAsync(string layoutGuid, int projectId, IReadOnlyCollection<string> allowedPackageGuids)
        {
            var mappers = new List<Mapper>();

            try
            {
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();

                // Normalizar layout guid para cobrir casos com/sem prefixo LAY_
                var layoutNoPrefix = layoutGuid?.Trim() ?? "";
                if (layoutNoPrefix.StartsWith("LAY_", StringComparison.OrdinalIgnoreCase))
                    layoutNoPrefix = layoutNoPrefix.Substring(4);
                var layoutWithPrefix = layoutNoPrefix.StartsWith("LAY_", StringComparison.OrdinalIgnoreCase) ? layoutNoPrefix : $"LAY_{layoutNoPrefix}";

                // Lista de pacotes permitidos (normalizados sem PAC_)
                var allowedNorm = new HashSet<string>(allowedPackageGuids.Select(NormalizePackageGuid), StringComparer.OrdinalIgnoreCase);

                // Montar IN com parâmetros para evitar SQL injection
                var pkgParams = allowedNorm.Select((_, i) => $"@p{i}").ToList();
                var inClause = pkgParams.Count > 0 ? string.Join(", ", pkgParams) : "NULL";

                var query = $@"
                    SELECT 
                        [Id], [MapperGuid], [PackageGuid], [Name], [Description],
                        [IsXPathMapper], [InputLayoutGuid], [TargetLayoutGuid],
                        [ValueContent], [ProjectId], [LastUpdateDate]
                    FROM [ConnectUS_Macgyver].[dbo].[tbMapper]
                    WHERE [ProjectId] = @ProjectId
                      AND (REPLACE(LOWER([PackageGuid]), 'pac_', '') IN ({inClause}))
                      AND (
                            [InputLayoutGuid] = @LayoutNoPrefix OR [InputLayoutGuid] = @LayoutWithPrefix
                         OR [TargetLayoutGuid] = @LayoutNoPrefix OR [TargetLayoutGuid] = @LayoutWithPrefix
                      )
                    ORDER BY [LastUpdateDate] DESC";

                using var command = new SqlCommand(query, connection);
                command.Parameters.AddWithValue("@ProjectId", projectId);
                command.Parameters.AddWithValue("@LayoutNoPrefix", layoutNoPrefix);
                command.Parameters.AddWithValue("@LayoutWithPrefix", layoutWithPrefix);
                for (int i = 0; i < allowedNorm.Count; i++)
                    command.Parameters.AddWithValue(pkgParams[i], allowedNorm.ElementAt(i));

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var mapper = MapReaderToMapper(reader);
                    mappers.Add(mapper);
                }

                // Extra segurança: filtrar também em memória pelo pacote permitido (considerando PAC_ e case)
                mappers = mappers
                    .Where(m => allowedNorm.Contains(NormalizePackageGuid(m.PackageGuid)))
                    .ToList();

                return mappers;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar mapeadores por LayoutGuid (filtrado): {LayoutGuid}", layoutGuid);
                return mappers;
            }
        }

        /// <summary>
        /// Mapeia SqlDataReader para Mapper, descriptografando ValueContent se necessário
        /// </summary>
        private Mapper MapReaderToMapper(SqlDataReader reader)
        {
            // Método auxiliar para ler valores como string de forma segura
            string GetStringValue(string columnName)
            {
                if (reader.IsDBNull(columnName))
                    return null;

                var value = reader[columnName];
                if (value == null)
                    return null;

                // Se for string, retornar diretamente
                if (value is string str)
                    return str;

                // Se for outro tipo, converter para string
                return value.ToString();
            }

            var mapper = new Mapper
            {
                Id = reader.GetInt32("Id"),
                MapperGuid = GetStringValue("MapperGuid"),
                PackageGuid = GetStringValue("PackageGuid"),
                Name = GetStringValue("Name"),
                Description = GetStringValue("Description"),
                IsXPathMapper = reader.IsDBNull("IsXPathMapper") ? false : reader.GetBoolean("IsXPathMapper"),
                InputLayoutGuid = GetStringValue("InputLayoutGuid"),
                TargetLayoutGuid = GetStringValue("TargetLayoutGuid"),
                ValueContent = GetStringValue("ValueContent"),
                ProjectId = GetStringValue("ProjectId"),
                LastUpdateDate = reader.IsDBNull("LastUpdateDate") ? DateTime.MinValue : reader.GetDateTime("LastUpdateDate")
            };

            // Descriptografar ValueContent se não for vazio
            if (!string.IsNullOrEmpty(mapper.ValueContent))
            {
                try
                {
                    mapper.DecryptedContent = _decryptionService.DecryptContent(mapper.ValueContent);

                    // Tentar extrair InputLayoutGuid e TargetLayoutGuid do XML descriptografado
                    ExtractLayoutGuidsFromDecryptedContent(mapper);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Erro ao descriptografar ValueContent do mapeador {Id} ({Name}). Continuando sem conteudo descriptografado.", mapper.Id, mapper.Name);
                    mapper.DecryptedContent = "";
                }
            }
            else
                mapper.DecryptedContent = "";

            return mapper;
        }

        /// <summary>
        /// Extrai InputLayoutGuid e TargetLayoutGuid do XML descriptografado do mapeador
        /// </summary>
        private void ExtractLayoutGuidsFromDecryptedContent(Mapper mapper)
        {
            try
            {
                if (string.IsNullOrEmpty(mapper.DecryptedContent))
                    return;

                var doc = XDocument.Parse(mapper.DecryptedContent);
                var root = doc.Root;

                if (root == null)
                    return;

                // Buscar InputLayoutGuid e TargetLayoutGuid do XML
                // Podem estar diretamente no root ou dentro de um elemento MapperVO
                var mapperVoElement = root.Name.LocalName == "MapperVO" ? root : root.Element("MapperVO");
                if (mapperVoElement == null)
                    // Tentar buscar diretamente no root
                    mapperVoElement = root;
                

                var inputLayoutGuidElement = mapperVoElement.Element("InputLayoutGuid");
                var targetLayoutGuidElement = mapperVoElement.Element("TargetLayoutGuid");

                // Se encontrados no XML, usar esses valores (podem ser mais precisos que as colunas do banco)
                if (inputLayoutGuidElement != null && !string.IsNullOrEmpty(inputLayoutGuidElement.Value))
                {
                    var inputGuidFromXml = inputLayoutGuidElement.Value.Trim();
                    mapper.InputLayoutGuidFromXml = inputGuidFromXml;
                    _logger.LogInformation("InputLayoutGuid extraido do XML para mapeador {Name} (ID: {Id}): {Guid}",mapper.Name, mapper.Id, inputGuidFromXml);

                    // Atualizar também InputLayoutGuid se estiver vazio na coluna
                    if (string.IsNullOrEmpty(mapper.InputLayoutGuid))
                        mapper.InputLayoutGuid = mapper.InputLayoutGuidFromXml;
                }

                if (targetLayoutGuidElement != null && !string.IsNullOrEmpty(targetLayoutGuidElement.Value))
                {
                    var targetGuidFromXml = targetLayoutGuidElement.Value.Trim();
                    mapper.TargetLayoutGuidFromXml = targetGuidFromXml;
                    _logger.LogInformation("TargetLayoutGuid extraido do XML para mapeador {Name} (ID: {Id}): {Guid}",mapper.Name, mapper.Id, targetGuidFromXml);

                    // Atualizar também TargetLayoutGuid se estiver vazio na coluna
                    if (string.IsNullOrEmpty(mapper.TargetLayoutGuid))
                        mapper.TargetLayoutGuid = mapper.TargetLayoutGuidFromXml;
                }

                // Extrair XSL do XML do mapper se existir
                ExtractXslFromDecryptedContent(mapper, doc);

                // Extrair estrutura completa do MapperVO para uso futuro
                // Isso permite processar Rules e LinkMappings adequadamente
                try
                {
                    var parsedMapperVo = MapperVo.FromXml(doc);
                    if (parsedMapperVo != null)
                    {
                        _logger.LogInformation("MapperVO parseado para mapeador {Name} (ID: {Id}): {RulesCount} Rules, {LinkMappingsCount} LinkMappings",mapper.Name, mapper.Id, parsedMapperVo.Rules.Count, parsedMapperVo.LinkMappings.Count);

                        // Se o mapper tem XSL no MapperVO, usar esse
                        if (!string.IsNullOrEmpty(parsedMapperVo.XslContent) && string.IsNullOrEmpty(mapper.XslContent))
                        {
                            mapper.XslContent = parsedMapperVo.XslContent;
                            _logger.LogInformation("XSL encontrado no MapperVO para mapeador {Name} (ID: {Id})", mapper.Name, mapper.Id);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Erro ao parsear MapperVO para mapeador {Id}", mapper.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao extrair LayoutGuids do XML descriptografado do mapeador {Id}", mapper.Id);
            }
        }

        /// <summary>
        /// Extrai XSL do XML descriptografado do mapeador
        /// O XSL pode estar em um elemento XslContent, Xsl, ou XslPath dentro do MapperVO
        /// </summary>
        private void ExtractXslFromDecryptedContent(Mapper mapper, XDocument doc)
        {
            try
            {
                if (doc == null || doc.Root == null)
                    return;

                // Buscar XSL no XML do mapper
                // Pode estar em: MapperVO/XslContent, MapperVO/Xsl, MapperVO/XslPath
                var mapperVo = doc.Root.Name.LocalName == "MapperVO" ? doc.Root : doc.Root.Element("MapperVO");
                if (mapperVo == null)
                    mapperVo = doc.Root;

                // Tentar buscar elemento XslContent (conteúdo XSL completo)
                var xslContentElement = mapperVo.Element("XslContent");
                if (xslContentElement != null && !string.IsNullOrEmpty(xslContentElement.Value))
                {
                    mapper.XslContent = xslContentElement.Value.Trim();
                    _logger.LogInformation("XSL encontrado no XML do mapeador {Name} (ID: {Id}) - tamanho: {Size} chars",mapper.Name, mapper.Id, mapper.XslContent.Length);
                    return;
                }

                // Tentar buscar elemento Xsl (alternativa)
                var xslElement = mapperVo.Element("Xsl");
                if (xslElement != null && !string.IsNullOrEmpty(xslElement.Value))
                {
                    mapper.XslContent = xslElement.Value.Trim();
                    _logger.LogInformation("XSL encontrado no XML do mapeador {Name} (ID: {Id}) - tamanho: {Size} chars",mapper.Name, mapper.Id, mapper.XslContent.Length);
                    return;
                }

                // Tentar buscar XSL dentro de um elemento xsl:stylesheet
                var xslStylesheet = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "stylesheet" && (e.Name.NamespaceName.Contains("XSL/Transform") || e.Parent?.Name.LocalName == "XslContent" || e.Parent?.Name.LocalName == "Xsl"));

                if (xslStylesheet != null)
                {
                    // Extrair XSL completo incluindo o elemento xsl:stylesheet
                    mapper.XslContent = xslStylesheet.ToString();
                    _logger.LogInformation("XSL (stylesheet) encontrado no XML do mapeador {Name} (ID: {Id}) - tamanho: {Size} chars", mapper.Name, mapper.Id, mapper.XslContent.Length);
                    return;
                }

                _logger.LogInformation("XSL nao encontrado no XML do mapeador {Name} (ID: {Id}). Sera gerado se necessario.", mapper.Name, mapper.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao extrair XSL do XML descriptografado do mapeador {Id}", mapper.Id);
            }
        }
    }
}