using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using LayoutParserApi.Models.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

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
                _logger.LogInformation("🔍 Conectando ao banco de dados para buscar todos os mapeadores...");
                _logger.LogInformation("Connection string: Server={Server}, Database={Database}", 
                    _connectionString.Contains("Server=") ? _connectionString.Split(';').FirstOrDefault(s => s.StartsWith("Server=")) : "N/A",
                    _connectionString.Contains("Database=") ? _connectionString.Split(';').FirstOrDefault(s => s.StartsWith("Database=")) : "N/A");
                
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();
                _logger.LogInformation("✅ Conexão com banco de dados estabelecida");

                var query = @"
                    SELECT 
                        [Id], [MapperGuid], [PackageGuid], [Name], [Description],
                        [IsXPathMapper], [InputLayoutGuid], [TargetLayoutGuid],
                        [ValueContent], [ProjectId], [LastUpdateDate]
                    FROM [ConnectUS_Macgyver].[dbo].[tbMapper]
                    ORDER BY [LastUpdateDate] DESC";

                _logger.LogInformation("📝 Executando query para buscar mapeadores...");
                using var command = new SqlCommand(query, connection);
                using var reader = await command.ExecuteReaderAsync();

                int count = 0;
                while (await reader.ReadAsync())
                {
                    try
                    {
                        var mapper = MapReaderToMapper(reader);
                        mappers.Add(mapper);
                        count++;
                        
                        if (count <= 3)
                        {
                            _logger.LogInformation("  - Mapeador {Count}: Id={Id}, Name={Name}, InputGuid={InputGuid}, TargetGuid={TargetGuid}", 
                                count, mapper.Id, mapper.Name,
                                mapper.InputLayoutGuidFromXml ?? mapper.InputLayoutGuid ?? "null",
                                mapper.TargetLayoutGuidFromXml ?? mapper.TargetLayoutGuid ?? "null");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ Erro ao mapear mapeador (linha {Count})", count + 1);
                    }
                }

                _logger.LogInformation("✅ Total de mapeadores encontrados: {Count}", mappers.Count);
                return mappers;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erro ao buscar todos os mapeadores: {Message}", ex.Message);
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
                    {
                        matches = true;
                    }
                    
                    // Verificar XML descriptografado (mais confiável)
                    if (!string.IsNullOrEmpty(mapper.InputLayoutGuidFromXml) && mapper.InputLayoutGuidFromXml == layoutGuid)
                    {
                        matches = true;
                    }
                    
                    if (!string.IsNullOrEmpty(mapper.TargetLayoutGuidFromXml) && mapper.TargetLayoutGuidFromXml == layoutGuid)
                    {
                        matches = true;
                    }
                    
                    if (matches)
                    {
                        mappers.Add(mapper);
                    }
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
            return mappers.FirstOrDefault(m => 
                m.InputLayoutGuid == inputLayoutGuid || 
                m.InputLayoutGuidFromXml == inputLayoutGuid);
        }

        /// <summary>
        /// Busca mapeador onde o layoutGuid é o TargetLayoutGuid (saída)
        /// </summary>
        public async Task<Mapper> GetMapperByTargetLayoutGuidAsync(string targetLayoutGuid)
        {
            var mappers = await GetMappersByLayoutGuidAsync(targetLayoutGuid);
            return mappers.FirstOrDefault(m => 
                m.TargetLayoutGuid == targetLayoutGuid || 
                m.TargetLayoutGuidFromXml == targetLayoutGuid);
        }

        /// <summary>
        /// Mapeia SqlDataReader para Mapper, descriptografando ValueContent se necessário
        /// </summary>
        private Mapper MapReaderToMapper(SqlDataReader reader)
        {
            var mapper = new Mapper
            {
                Id = reader.GetInt32("Id"),
                MapperGuid = reader.IsDBNull("MapperGuid") ? null : reader.GetString("MapperGuid"),
                PackageGuid = reader.IsDBNull("PackageGuid") ? null : reader.GetString("PackageGuid"),
                Name = reader.IsDBNull("Name") ? null : reader.GetString("Name"),
                Description = reader.IsDBNull("Description") ? null : reader.GetString("Description"),
                IsXPathMapper = reader.IsDBNull("IsXPathMapper") ? false : reader.GetBoolean("IsXPathMapper"),
                InputLayoutGuid = reader.IsDBNull("InputLayoutGuid") ? null : reader.GetString("InputLayoutGuid"),
                TargetLayoutGuid = reader.IsDBNull("TargetLayoutGuid") ? null : reader.GetString("TargetLayoutGuid"),
                ValueContent = reader.IsDBNull("ValueContent") ? null : reader.GetString("ValueContent"),
                ProjectId = reader.IsDBNull("ProjectId") ? null : reader.GetString("ProjectId"),
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
            {
                mapper.DecryptedContent = "";
            }

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
                var mapperVo = root.Name.LocalName == "MapperVO" ? root : root.Element("MapperVO");
                if (mapperVo == null)
                {
                    // Tentar buscar diretamente no root
                    mapperVo = root;
                }

                var inputLayoutGuidElement = mapperVo.Element("InputLayoutGuid");
                var targetLayoutGuidElement = mapperVo.Element("TargetLayoutGuid");

                // Se encontrados no XML, usar esses valores (podem ser mais precisos que as colunas do banco)
                if (inputLayoutGuidElement != null && !string.IsNullOrEmpty(inputLayoutGuidElement.Value))
                {
                    mapper.InputLayoutGuidFromXml = inputLayoutGuidElement.Value;
                    // Atualizar também InputLayoutGuid se estiver vazio na coluna
                    if (string.IsNullOrEmpty(mapper.InputLayoutGuid))
                    {
                        mapper.InputLayoutGuid = mapper.InputLayoutGuidFromXml;
                    }
                }

                if (targetLayoutGuidElement != null && !string.IsNullOrEmpty(targetLayoutGuidElement.Value))
                {
                    mapper.TargetLayoutGuidFromXml = targetLayoutGuidElement.Value;
                    // Atualizar também TargetLayoutGuid se estiver vazio na coluna
                    if (string.IsNullOrEmpty(mapper.TargetLayoutGuid))
                    {
                        mapper.TargetLayoutGuid = mapper.TargetLayoutGuidFromXml;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao extrair LayoutGuids do XML descriptografado do mapeador {Id}", mapper.Id);
            }
        }
    }
}

