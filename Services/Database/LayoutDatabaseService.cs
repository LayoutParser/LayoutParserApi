using Microsoft.Data.SqlClient;
using LayoutParserApi.Models.Database;
using LayoutParserApi.Services.Database;

namespace LayoutParserApi.Services.Database
{
    public interface ILayoutDatabaseService
    {
        Task<LayoutSearchResponse> SearchLayoutsAsync(LayoutSearchRequest request);
        Task<LayoutRecord?> GetLayoutByIdAsync(int id);
    }

    public class LayoutDatabaseService : ILayoutDatabaseService
    {
        private readonly ILogger<LayoutDatabaseService> _logger;
        private readonly IDecryptionService _decryptionService;
        private readonly string _connectionString;

        public LayoutDatabaseService(
            ILogger<LayoutDatabaseService> logger,
            IDecryptionService decryptionService,
            IConfiguration configuration)
        {
            _logger = logger;
            _decryptionService = decryptionService;
            
            // Construir connection string
            var server = configuration["Database:Server"] ?? "172.31.249.51";
            var database = configuration["Database:Database"] ?? "ConnectUS_Macgyver";
            var userId = configuration["Database:UserId"] ?? "macgyver";
            var password = configuration["Database:Password"] ?? "eb8XNsww3D@U&HyZe4";
            
            _connectionString = $"Server={server};Database={database};User Id={userId};Password={password};TrustServerCertificate=true;";
            
            _logger.LogInformation("LayoutDatabaseService configurado para servidor: {Server}", server);
        }

        public async Task<LayoutSearchResponse> SearchLayoutsAsync(LayoutSearchRequest request)
        {
            try
            {
                _logger.LogInformation("Buscando layouts com termo: {SearchTerm}", request.SearchTerm);
                return await SearchLayoutsFromDatabase(request);
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

        private async Task<LayoutSearchResponse> SearchLayoutsFromDatabase(LayoutSearchRequest request)
        {
            try
            {
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();

                var query = @"
                    SELECT TOP (@MaxResults) 
                        [Id], [LayoutGuid], [PackageGuid], [Name], [Description], 
                        [LayoutType], [ValueContent], [XmlShemaValidatorPath], 
                        [ProjectId], [LastUpdateDate]
                    FROM [ConnectUS_Macgyver].[dbo].[tbLayout] 
                    WHERE [Name] LIKE @SearchPattern
                    ORDER BY [LastUpdateDate] DESC";

                using var command = new SqlCommand(query, connection);
                command.Parameters.AddWithValue("@SearchPattern", $"%{request.SearchTerm}%");
                command.Parameters.AddWithValue("@MaxResults", request.MaxResults);

                var layouts = new List<LayoutRecord>();

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var layout = new LayoutRecord
                    {
                        Id = reader.GetInt32(0),
                        LayoutGuid = reader.IsDBNull(1) ? Guid.Empty : SafeParseGuid(SafeGetString(reader, 1)),
                        PackageGuid = reader.IsDBNull(2) ? Guid.Empty : SafeParseGuid(SafeGetString(reader, 2)),
                        Name = SafeGetString(reader, 3),
                        Description = reader.IsDBNull(4) ? "" : SafeGetString(reader, 4),
                        LayoutType = reader.IsDBNull(5) ? "" : SafeGetString(reader, 5),
                        ValueContent = reader.GetString(6),
                        XmlShemaValidatorPath = reader.IsDBNull(7) ? "" : SafeGetString(reader, 7),
                        ProjectId = reader.GetInt32(8),
                        LastUpdateDate = reader.GetDateTime(9)
                    };

                    // Descriptografar o conteúdo
                    if (!string.IsNullOrEmpty(layout.ValueContent))
                        layout.DecryptedContent = _decryptionService.DecryptContent(layout.ValueContent);
                    

                    layouts.Add(layout);
                }

                _logger.LogInformation("Encontrados {Count} layouts no banco", layouts.Count);

                return new LayoutSearchResponse
                {
                    Success = true,
                    Layouts = layouts,
                    TotalFound = layouts.Count
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar layouts no banco de dados");
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
                return await GetLayoutByIdFromDatabase(id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar layout por ID: {Id}", id);
                return null;
            }
        }

        private async Task<LayoutRecord?> GetLayoutByIdFromDatabase(int id)
        {
            try
            {
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();

                var query = @"
                    SELECT [Id], [LayoutGuid], [PackageGuid], [Name], [Description], 
                           [LayoutType], [ValueContent], [XmlShemaValidatorPath], 
                           [ProjectId], [LastUpdateDate]
                    FROM [ConnectUS_Macgyver].[dbo].[tbLayout] 
                    WHERE [Id] = @Id";

                using var command = new SqlCommand(query, connection);
                command.Parameters.AddWithValue("@Id", id);

                using var reader = await command.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    var layout = new LayoutRecord
                    {
                        Id = reader.GetInt32(0),
                        LayoutGuid = reader.IsDBNull(1) ? Guid.Empty : SafeParseGuid(SafeGetString(reader, 1)),
                        PackageGuid = reader.IsDBNull(2) ? Guid.Empty : SafeParseGuid(SafeGetString(reader, 2)),
                        Name = SafeGetString(reader, 3),
                        Description = reader.IsDBNull(4) ? "" : SafeGetString(reader, 4),
                        LayoutType = reader.IsDBNull(5) ? "" : SafeGetString(reader, 5),
                        ValueContent = reader.IsDBNull(6) ? "" : SafeGetString(reader, 6),
                        XmlShemaValidatorPath = reader.IsDBNull(7) ? "" : SafeGetString(reader, 7),
                        ProjectId = reader.GetInt32(8),
                        LastUpdateDate = reader.GetDateTime(9)
                    };

                    // Descriptografar o conteúdo
                    if (!string.IsNullOrEmpty(layout.ValueContent))
                        layout.DecryptedContent = _decryptionService.DecryptContent(layout.ValueContent);
                    

                    _logger.LogInformation("Layout encontrado no banco: {Name}", layout.Name);
                    return layout;
                }

                _logger.LogWarning("Layout não encontrado no banco para ID: {Id}", id);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar layout no banco por ID: {Id}", id);
                return null;
            }
        }

        /// <summary>
        /// Converte string para Guid de forma segura
        /// </summary>
        private static Guid SafeParseGuid(string value)
        {
            if (string.IsNullOrEmpty(value))
                return Guid.Empty;

            try
            {
                return Guid.Parse(value);
            }
            catch
            {
                return Guid.Empty;
            }
        }

        /// <summary>
        /// Obtém string do reader de forma segura, convertendo qualquer tipo para string
        /// </summary>
        private static string SafeGetString(SqlDataReader reader, int index)
        {
            try
            {
                if (reader.IsDBNull(index))
                    return "";

                var value = reader.GetValue(index);
                return value?.ToString() ?? "";
            }
            catch
            {
                return "";
            }
        }
    }
}
