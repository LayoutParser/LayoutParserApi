using LayoutParserApi.Models.Database;
using LayoutParserApi.Services.Interfaces;

using Microsoft.Data.SqlClient;

using System.Xml.Linq;

namespace LayoutParserApi.Services.Database
{

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

            // Configurar connection string com timeout e SSL adequado
            // Encrypt=false desabilita SSL/TLS (pode resolver timeout)
            // Ou Encrypt=true com TrustServerCertificate=true para SSL sem validação
            var encrypt = configuration["Database:Encrypt"]?.ToLower() ?? "false";
            var connectionTimeout = configuration["Database:ConnectionTimeout"] ?? "30";
            var commandTimeout = configuration["Database:CommandTimeout"] ?? "30";

            _connectionString = $"Server={server};Database={database};User Id={userId};Password={password};" +
                              $"TrustServerCertificate=true;Encrypt={encrypt};" +
                              $"Connection Timeout={connectionTimeout};Command Timeout={commandTimeout};" +
                              $"Pooling=true;Min Pool Size=5;Max Pool Size=100;";

            _logger.LogInformation("LayoutDatabaseService configurado para servidor: {Server}", server);
        }

        public async Task<LayoutSearchResponse> SearchLayoutsAsync(LayoutSearchRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.SearchTerm))
                    _logger.LogInformation("Buscando todos os layouts (sem filtro)");
                else
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

                // Se SearchTerm estiver vazio ou null, buscar todos os layouts
                bool hasSearchTerm = !string.IsNullOrWhiteSpace(request.SearchTerm);

                string query;
                if (hasSearchTerm)
                {
                    // Busca com filtro por nome na tabela tbLayout
                    query = @"
                        SELECT TOP (200) 
                            [Id], [LayoutGuid], [PackageGuid], [Name], [Description], 
                            [LayoutType], [ValueContent], [XmlShemaValidatorPath], 
                            [ProjectId], [LastUpdateDate]
                        FROM [ConnectUS_Macgyver].[dbo].[tbLayout] WITH (NOLOCK)
                        WHERE [ProjectId] = 2 AND [Name] LIKE @SearchPattern
                        ORDER BY [LastUpdateDate] DESC";
                }
                else
                {
                    // Busca todos os layouts da tabela tbLayout com ProjectId = 2 (TOP 200)
                    query = @"
                        SELECT TOP (200) 
                            [Id], [LayoutGuid], [PackageGuid], [Name], [Description], 
                            [LayoutType], [ValueContent], [XmlShemaValidatorPath], 
                            [ProjectId], [LastUpdateDate]
                        FROM [ConnectUS_Macgyver].[dbo].[tbLayout] WITH (NOLOCK)
                        WHERE [ProjectId] = 2
                        ORDER BY [LastUpdateDate] DESC";
                }

                using var command = new SqlCommand(query, connection);
                if (hasSearchTerm)
                    command.Parameters.AddWithValue("@SearchPattern", $"%{request.SearchTerm}%");

                // Nota: TOP (200) é fixo na query, não usa parâmetro @MaxResults

                var layouts = new List<LayoutRecord>();
                var totalRead = 0;
                var textPositionalCount = 0;
                var skippedCount = 0;

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    totalRead++;
                    var layout = new LayoutRecord
                    {
                        Id = reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                        LayoutGuid = reader.IsDBNull(1) ? Guid.Empty : SafeParseGuid(SafeGetString(reader, 1)),
                        PackageGuid = reader.IsDBNull(2) ? Guid.Empty : SafeParseGuid(SafeGetString(reader, 2)),
                        Name = SafeGetString(reader, 3),
                        Description = reader.IsDBNull(4) ? "" : SafeGetString(reader, 4),
                        LayoutType = reader.IsDBNull(5) ? "" : SafeGetString(reader, 5),
                        ValueContent = reader.IsDBNull(6) ? "" : SafeGetString(reader, 6),
                        XmlShemaValidatorPath = reader.IsDBNull(7) ? "" : SafeGetString(reader, 7),
                        ProjectId = reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                        LastUpdateDate = reader.IsDBNull(9) ? DateTime.MinValue : reader.GetDateTime(9)
                    };

                    // Descriptografar o conteúdo apenas se não for vazio
                    if (!string.IsNullOrEmpty(layout.ValueContent))
                    {
                        try
                        {
                            layout.DecryptedContent = _decryptionService.DecryptContent(layout.ValueContent);

                            // Extrair LayoutGuid do XML descriptografado (sempre priorizar XML sobre banco)
                            ExtractLayoutGuidFromDecryptedContent(layout);

                            // Verificar se o layout é do tipo TextPositional
                            // Apenas layouts TextPositional devem ser incluídos no Redis
                            if (!IsTextPositionalLayout(layout))
                            {
                                skippedCount++;
                                _logger.LogDebug("Layout {Id} ({Name}) ignorado - nao e TextPositional", layout.Id, layout.Name);
                                continue; // Pular este layout, não adicionar à lista
                            }

                            textPositionalCount++;
                        }
                        catch (Exception ex)
                        {
                            skippedCount++;
                            _logger.LogWarning(ex, "Erro ao descriptografar conteudo do layout {Id} ({Name}). Continuando sem conteudo descriptografado.", layout.Id, layout.Name);
                            layout.DecryptedContent = "";
                            // Se não conseguiu descriptografar, não pode verificar o tipo, então pula
                            continue;
                        }
                    }
                    else
                    {
                        skippedCount++;
                        layout.DecryptedContent = "";
                        // Se não tem conteúdo, não pode verificar o tipo, então pula
                        continue;
                    }

                    layouts.Add(layout);
                }

                _logger.LogInformation("✅ Processamento de layouts concluido: {TotalRead} lidos do banco, {TextPositionalCount} TextPositional incluidos, {SkippedCount} ignorados (nao-TextPositional ou sem conteudo)", totalRead, textPositionalCount, skippedCount);
                _logger.LogInformation("Encontrados {Count} layouts TextPositional para incluir no Redis", layouts.Count);

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
                        Id = reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                        LayoutGuid = reader.IsDBNull(1) ? Guid.Empty : SafeParseGuid(SafeGetString(reader, 1)),
                        PackageGuid = reader.IsDBNull(2) ? Guid.Empty : SafeParseGuid(SafeGetString(reader, 2)),
                        Name = SafeGetString(reader, 3),
                        Description = reader.IsDBNull(4) ? "" : SafeGetString(reader, 4),
                        LayoutType = reader.IsDBNull(5) ? "" : SafeGetString(reader, 5),
                        ValueContent = reader.IsDBNull(6) ? "" : SafeGetString(reader, 6),
                        XmlShemaValidatorPath = reader.IsDBNull(7) ? "" : SafeGetString(reader, 7),
                        ProjectId = reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                        LastUpdateDate = reader.IsDBNull(9) ? DateTime.MinValue : reader.GetDateTime(9)
                    };

                    // Descriptografar o conteúdo apenas se não for vazio
                    if (!string.IsNullOrEmpty(layout.ValueContent))
                    {
                        try
                        {
                            layout.DecryptedContent = _decryptionService.DecryptContent(layout.ValueContent);

                            // Extrair LayoutGuid do XML descriptografado (sempre priorizar XML sobre banco)
                            ExtractLayoutGuidFromDecryptedContent(layout);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Erro ao descriptografar conteudo do layout {Id} ({Name}). Continuando sem conteudo descriptografado.", layout.Id, layout.Name);
                            layout.DecryptedContent = "";
                        }
                    }
                    else
                        layout.DecryptedContent = "";


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

        /// <summary>
        /// Extrai LayoutGuid do XML descriptografado do layout usando XPath /LayoutVO/LayoutGuid
        /// O LayoutGuid do XML é sempre priorizado sobre o do banco de dados
        /// Se o LayoutGuid do banco estiver zerado, será preenchido com o valor do XML
        /// </summary>
        private void ExtractLayoutGuidFromDecryptedContent(LayoutRecord layout)
        {
            try
            {
                if (string.IsNullOrEmpty(layout.DecryptedContent))
                {
                    _logger.LogWarning("DecryptedContent vazio para layout {Id} ({Name}). Nao e possivel extrair LayoutGuid do XML.",
                        layout.Id, layout.Name);
                    return;
                }

                // Guardar o LayoutGuid do banco para log
                var layoutGuidFromDb = layout.LayoutGuid;
                var wasEmpty = layout.LayoutGuid == Guid.Empty;

                // Parse do XML descriptografado
                var doc = XDocument.Parse(layout.DecryptedContent);
                var root = doc.Root;

                if (root == null)
                {
                    _logger.LogWarning("XML root e nulo para layout {Id} ({Name})", layout.Id, layout.Name);
                    return;
                }

                // Usar XPath /LayoutVO/LayoutGuid conforme especificado
                // O root pode ser LayoutVO ou pode ter um elemento LayoutVO filho
                XElement? layoutVo = null;
                XElement? layoutGuidElement = null;

                // Primeiro, verificar se o root é LayoutVO
                if (root.Name.LocalName == "LayoutVO")
                {
                    layoutVo = root;
                    layoutGuidElement = root.Element("LayoutGuid");
                }
                else
                {
                    // Tentar buscar elemento LayoutVO filho
                    layoutVo = root.Element("LayoutVO");
                    if (layoutVo != null)
                        layoutGuidElement = layoutVo.Element("LayoutGuid");
                    else
                    {
                        // Tentar buscar LayoutGuid diretamente no root (caso o XML tenha estrutura diferente)
                        layoutGuidElement = root.Element("LayoutGuid");
                        if (layoutGuidElement != null)
                            layoutVo = root;

                    }
                }

                if (layoutVo == null || layoutGuidElement == null)
                {
                    _logger.LogWarning("Elemento LayoutVO ou LayoutGuid nao encontrado no XML (XPath /LayoutVO/LayoutGuid) para layout {Id} ({Name})", layout.Id, layout.Name);
                    return;
                }

                // Se encontrado no XML, usar esse valor (sempre priorizar XML sobre banco)
                if (!string.IsNullOrEmpty(layoutGuidElement.Value))
                {
                    var layoutGuidFromXml = layoutGuidElement.Value.Trim();
                    _logger.LogInformation("LayoutGuid encontrado no XML (XPath /LayoutVO/LayoutGuid) para layout {Id} ({Name}): {Guid}", layout.Id, layout.Name, layoutGuidFromXml);

                    var guidString = layoutGuidFromXml;
                    if (guidString.StartsWith("LAY_", StringComparison.OrdinalIgnoreCase))
                    {
                        guidString = guidString.Substring(4);
                        _logger.LogInformation("Prefixo LAY_ removido: {Guid}", guidString);
                    }

                    // Tentar converter para Guid
                    if (Guid.TryParse(guidString, out var parsedGuid))
                    {
                        // SEMPRE atualizar com o valor do XML (priorizar XML sobre banco)
                        // Isso é especialmente importante quando o banco tem Guid zerado
                        layout.LayoutGuid = parsedGuid;

                        if (wasEmpty)
                            _logger.LogInformation("LayoutGuid PREENCHIDO do XML para layout {Id} ({Name}): {Guid} (estava ZERADO no banco)", layout.Id, layout.Name, layout.LayoutGuid);
                        else if (layoutGuidFromDb != parsedGuid)
                            _logger.LogInformation("LayoutGuid ATUALIZADO do XML para layout {Id} ({Name}): {Guid} (banco tinha: {DbGuid})", layout.Id, layout.Name, layout.LayoutGuid, layoutGuidFromDb);
                        else
                            _logger.LogDebug("LayoutGuid do XML confere com o do banco para layout {Id} ({Name}): {Guid}", layout.Id, layout.Name, layout.LayoutGuid);

                    }
                    else
                        _logger.LogWarning("Nao foi possivel converter LayoutGuid do XML para Guid: {GuidString} (layout {Id} - {Name})", guidString, layout.Id, layout.Name);

                }
                else
                    _logger.LogWarning("❌ LayoutGuid nao encontrado no XML (XPath /LayoutVO/LayoutGuid) para layout {Id} ({Name}). Mantendo valor do banco: {DbGuid}", layout.Id, layout.Name, layoutGuidFromDb);

            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "❌ Erro ao extrair LayoutGuid do XML descriptografado para layout {Id} ({Name}). Continuando com LayoutGuid do banco: {DbGuid}",
                    layout.Id, layout.Name, layout.LayoutGuid);
            }
        }

        /// <summary>
        /// Verifica se o layout é do tipo TextPositional usando XPath /LayoutVO/LayoutType
        /// Apenas layouts TextPositional devem ser salvos no Redis
        /// </summary>
        private bool IsTextPositionalLayout(LayoutRecord layout)
        {
            try
            {
                if (string.IsNullOrEmpty(layout.DecryptedContent))
                {
                    _logger.LogWarning("DecryptedContent vazio para layout {Id} ({Name}). Nao e possivel verificar LayoutType.", layout.Id, layout.Name);
                    return false; // Sem conteúdo, não pode verificar
                }

                // Parse do XML descriptografado
                var doc = XDocument.Parse(layout.DecryptedContent);
                var root = doc.Root;

                if (root == null)
                {
                    _logger.LogWarning("XML root e nulo para layout {Id} ({Name})", layout.Id, layout.Name);
                    return false;
                }

                // Usar XPath /LayoutVO/LayoutType conforme especificado
                XElement? layoutVo = null;
                XElement? layoutTypeElement = null;

                // Primeiro, verificar se o root é LayoutVO
                if (root.Name.LocalName == "LayoutVO")
                {
                    layoutVo = root;
                    layoutTypeElement = root.Element("LayoutType");
                }
                else
                {
                    // Tentar buscar elemento LayoutVO filho
                    layoutVo = root.Element("LayoutVO");
                    if (layoutVo != null)
                        layoutTypeElement = layoutVo.Element("LayoutType");
                    else
                    {
                        // Tentar buscar LayoutType diretamente no root (caso o XML tenha estrutura diferente)
                        layoutTypeElement = root.Element("LayoutType");
                        if (layoutTypeElement != null)
                            layoutVo = root;

                    }
                }

                if (layoutVo == null || layoutTypeElement == null)
                {
                    _logger.LogWarning("Elemento LayoutVO ou LayoutType nao encontrado no XML (XPath /LayoutVO/LayoutType) para layout {Id} ({Name})", layout.Id, layout.Name);
                    return false;
                }

                // Verificar se o LayoutType é "TextPositional"
                var layoutType = layoutTypeElement.Value.Trim();
                var isTextPositional = string.Equals(layoutType, "TextPositional", StringComparison.OrdinalIgnoreCase);

                if (isTextPositional)
                    _logger.LogDebug("Layout {Id} ({Name}) e TextPositional - sera incluido no Redis", layout.Id, layout.Name);
                else
                    _logger.LogDebug("Layout {Id} ({Name}) nao e TextPositional (tipo: {LayoutType}) - sera ignorado", layout.Id, layout.Name, layoutType);

                return isTextPositional;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erro ao verificar LayoutType do XML descriptografado para layout {Id} ({Name}). Considerando como nao-TextPositional.", layout.Id, layout.Name);
                return false; // Em caso de erro, não incluir no Redis
            }
        }
    }
}