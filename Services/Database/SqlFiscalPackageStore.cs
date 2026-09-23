using LayoutParserApi.Models.Entities.Fiscal;
using LayoutParserApi.Services.Interfaces;

using Microsoft.Data.SqlClient;

namespace LayoutParserApi.Services.Database
{
    /// <summary>
    /// Implementação SQL de <see cref="IFiscalPackageStore"/> — Slice 2 (issue #229). Migrado do
    /// banco compartilhado <c>ConnectUS_Macgyver</c> (<c>Database:*</c>) para o banco DEDICADO do
    /// projeto (<c>IdentityDatabase:*</c>, mesmo usado por <see cref="SqlIdentityWorkspaceStore"/>)
    /// — a raiz do bug de FK cross-database investigado na PR #312 era justamente essas tabelas
    /// fiscais morarem no banco compartilhado enquanto o workspace real mora em outro servidor
    /// físico. Mesmo padrão ADO.NET cru de <see cref="SqlIdentityWorkspaceStore"/> (DDL idempotente
    /// por processo). Ver <c>.claude/rules/security.md</c> para o histórico da credencial
    /// compartilhada que motivou a separação original de identidade.
    /// </summary>
    public sealed class SqlFiscalPackageStore : IFiscalPackageStore
    {
        private readonly ILogger<SqlFiscalPackageStore> _logger;
        private readonly string _connectionString;

        private static readonly HashSet<int> UniqueViolationErrorNumbers = new() { 2601, 2627 };

        private static bool _schemaEnsured;
        private static readonly SemaphoreSlim _schemaLock = new(1, 1);

        public SqlFiscalPackageStore(ILogger<SqlFiscalPackageStore> logger, IConfiguration configuration)
        {
            _logger = logger;
            // ✅ Banco dedicado do projeto (não mais o ConnectUS_Macgyver compartilhado) — mesmo
            // padrão de SqlIdentityWorkspaceStore.
            var server = configuration["IdentityDatabase:Server"];
            var database = configuration["IdentityDatabase:Database"];
            var userId = configuration["IdentityDatabase:UserId"];
            var password = configuration["IdentityDatabase:Password"];

            _connectionString = $"Server={server};Database={database};User Id={userId};Password={password};TrustServerCertificate=True;";
        }

        public async Task<bool> EnsureProjectExistsAsync(Guid workspaceId, Guid projectId, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            using (var select = new SqlCommand(
                "SELECT 1 FROM dbo.tbFiscalProject WHERE ProjectId = @ProjectId AND WorkspaceId = @WorkspaceId;",
                connection))
            {
                select.Parameters.AddWithValue("@ProjectId", projectId);
                select.Parameters.AddWithValue("@WorkspaceId", workspaceId);
                if (await select.ExecuteScalarAsync(cancellationToken) != null)
                    return true;
            }

            // Cria o FiscalProject mínimo sob demanda (Slice 2 não expõe CRUD dedicado). Corrida entre
            // duas requisições concorrentes para o mesmo ProjectId é possível (não há UNIQUE aqui além
            // da PK) — colisão de PRIMARY KEY é tratada como sucesso (já existe).
            using (var insert = new SqlCommand(
                @"INSERT INTO dbo.tbFiscalProject (ProjectId, WorkspaceId, Name, CreatedAt)
                  VALUES (@ProjectId, @WorkspaceId, @Name, SYSUTCDATETIME());",
                connection))
            {
                insert.Parameters.AddWithValue("@ProjectId", projectId);
                insert.Parameters.AddWithValue("@WorkspaceId", workspaceId);
                insert.Parameters.AddWithValue("@Name", $"Projeto {projectId}");
                try
                {
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                    return true;
                }
                catch (SqlException ex) when (UniqueViolationErrorNumbers.Contains(ex.Number))
                {
                    _logger.LogInformation("Corrida de criação de FiscalProject detectada ({ProjectId}); já existe.", projectId);
                    return true;
                }
            }
        }

        public async Task<PackageDetail> CreatePackageAsync(
            Guid workspaceId,
            Guid projectId,
            Guid createdByUserId,
            string packageName,
            string idempotencyKey,
            IReadOnlyList<PackageArtifact> artifacts,
            CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            var packageId = Guid.NewGuid();
            var revisionId = Guid.NewGuid();
            var createdAt = DateTimeOffset.UtcNow;

            using var tx = connection.BeginTransaction();
            try
            {
                using (var insertPackage = new SqlCommand(
                    @"INSERT INTO dbo.tbFiscalMappingPackage (PackageId, WorkspaceId, ProjectId, Name, IdempotencyKey, CreatedAt)
                      VALUES (@PackageId, @WorkspaceId, @ProjectId, @Name, @IdempotencyKey, SYSUTCDATETIME());",
                    connection, tx))
                {
                    insertPackage.Parameters.AddWithValue("@PackageId", packageId);
                    insertPackage.Parameters.AddWithValue("@WorkspaceId", workspaceId);
                    insertPackage.Parameters.AddWithValue("@ProjectId", projectId);
                    insertPackage.Parameters.AddWithValue("@Name", packageName);
                    insertPackage.Parameters.AddWithValue("@IdempotencyKey", idempotencyKey);
                    await insertPackage.ExecuteNonQueryAsync(cancellationToken);
                }

                using (var insertRevision = new SqlCommand(
                    @"INSERT INTO dbo.tbFiscalMappingPackageRevision (RevisionId, PackageId, RevisionNumber, CreatedByUserId, CreatedAt)
                      VALUES (@RevisionId, @PackageId, 1, @CreatedByUserId, SYSUTCDATETIME());",
                    connection, tx))
                {
                    insertRevision.Parameters.AddWithValue("@RevisionId", revisionId);
                    insertRevision.Parameters.AddWithValue("@PackageId", packageId);
                    insertRevision.Parameters.AddWithValue("@CreatedByUserId", createdByUserId);
                    await insertRevision.ExecuteNonQueryAsync(cancellationToken);
                }

                foreach (var artifact in artifacts)
                {
                    artifact.ArtifactId = artifact.ArtifactId == Guid.Empty ? Guid.NewGuid() : artifact.ArtifactId;
                    artifact.RevisionId = revisionId;

                    using var insertArtifact = new SqlCommand(
                        @"INSERT INTO dbo.tbPackageArtifact
                            (ArtifactId, RevisionId, Kind, Sha256, SizeBytes, OriginalFileName, MimeDeclared, MimeSniffed,
                             UploadedByUserId, UploadedAt, Classification, RetentionPolicy, InspectionStatus, StoragePath, Provenance)
                          VALUES
                            (@ArtifactId, @RevisionId, @Kind, @Sha256, @SizeBytes, @OriginalFileName, @MimeDeclared, @MimeSniffed,
                             @UploadedByUserId, SYSUTCDATETIME(), @Classification, @RetentionPolicy, @InspectionStatus, @StoragePath, @Provenance);",
                        connection, tx);

                    insertArtifact.Parameters.AddWithValue("@ArtifactId", artifact.ArtifactId);
                    insertArtifact.Parameters.AddWithValue("@RevisionId", revisionId);
                    insertArtifact.Parameters.AddWithValue("@Kind", artifact.Kind);
                    insertArtifact.Parameters.AddWithValue("@Sha256", artifact.Sha256);
                    insertArtifact.Parameters.AddWithValue("@SizeBytes", artifact.SizeBytes);
                    insertArtifact.Parameters.AddWithValue("@OriginalFileName", artifact.OriginalFileName);
                    insertArtifact.Parameters.AddWithValue("@MimeDeclared", artifact.MimeDeclared);
                    insertArtifact.Parameters.AddWithValue("@MimeSniffed", artifact.MimeSniffed);
                    insertArtifact.Parameters.AddWithValue("@UploadedByUserId", artifact.UploadedByUserId);
                    insertArtifact.Parameters.AddWithValue("@Classification", (object?)artifact.Classification ?? DBNull.Value);
                    insertArtifact.Parameters.AddWithValue("@RetentionPolicy", (object?)artifact.RetentionPolicy ?? DBNull.Value);
                    insertArtifact.Parameters.AddWithValue("@InspectionStatus", artifact.InspectionStatus);
                    insertArtifact.Parameters.AddWithValue("@StoragePath", artifact.StoragePath);
                    insertArtifact.Parameters.AddWithValue("@Provenance", (object?)artifact.Provenance ?? DBNull.Value);
                    await insertArtifact.ExecuteNonQueryAsync(cancellationToken);
                }

                await tx.CommitAsync(cancellationToken);
            }
            catch (SqlException ex) when (UniqueViolationErrorNumbers.Contains(ex.Number))
            {
                // Corrida entre 2 uploads concorrentes com a mesma IdempotencyKey: o UNIQUE
                // (WorkspaceId, ProjectId, IdempotencyKey) rejeita o segundo INSERT. Mesmo padrão de
                // EnsureProjectExistsAsync — trata como sucesso e devolve o pacote já criado pelo primeiro,
                // em vez de propagar a SqlException (que viraria 503 pro cliente).
                await tx.RollbackAsync(cancellationToken);
                _logger.LogInformation(
                    "Corrida de criação de FiscalMappingPackage detectada (IdempotencyKey {IdempotencyKey}); devolvendo pacote existente.",
                    idempotencyKey);

                var existing = await FindPackageByIdempotencyKeyAsync(workspaceId, projectId, idempotencyKey, cancellationToken);
                if (existing is not null)
                    return existing;

                // Corrida rara: o outro INSERT ainda não commitou visível para esta leitura. Relança para
                // o chamador tratar como falha transitória (o SELECT anterior falhou por timing, não por
                // dado inconsistente).
                throw;
            }
            catch
            {
                await tx.RollbackAsync(cancellationToken);
                throw;
            }

            return new PackageDetail(
                packageId,
                workspaceId,
                projectId,
                packageName,
                createdAt,
                new RevisionSummary(
                    revisionId,
                    1,
                    createdAt,
                    artifacts.Select(a => new ArtifactSummary(a.ArtifactId, a.Kind, a.Sha256, a.SizeBytes, a.OriginalFileName, a.InspectionStatus, createdAt, a.Provenance)).ToList()));
        }

        public async Task<PackageDetail?> GetPackageIfMemberAsync(Guid packageId, Guid userId, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            // Isolamento por workspace: só devolve o pacote se o usuário for membro do workspace dono
            // — mesma cláusula WHERE m.UserId = @UserId do Slice 1.
            using var selectPackage = new SqlCommand(
                @"SELECT p.PackageId, p.WorkspaceId, p.ProjectId, p.Name, p.CreatedAt
                  FROM dbo.tbFiscalMappingPackage p
                  JOIN dbo.tbLpWorkspaceMembership m ON m.WorkspaceId = p.WorkspaceId AND m.UserId = @UserId
                  WHERE p.PackageId = @PackageId;",
                connection);
            selectPackage.Parameters.AddWithValue("@PackageId", packageId);
            selectPackage.Parameters.AddWithValue("@UserId", userId);

            Guid workspaceId, projectId;
            string name;
            DateTimeOffset createdAt;

            using (var reader = await selectPackage.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken))
                    return null; // Não existe OU não é seu — indistinguível, mesmo padrão do Slice 1.

                workspaceId = reader.GetGuid(reader.GetOrdinal("WorkspaceId"));
                projectId = reader.GetGuid(reader.GetOrdinal("ProjectId"));
                name = reader.GetString(reader.GetOrdinal("Name"));
                createdAt = new DateTimeOffset(reader.GetDateTime(reader.GetOrdinal("CreatedAt")), TimeSpan.Zero);
            }

            using var selectRevision = new SqlCommand(
                @"SELECT TOP 1 RevisionId, RevisionNumber, CreatedAt
                  FROM dbo.tbFiscalMappingPackageRevision
                  WHERE PackageId = @PackageId
                  ORDER BY RevisionNumber DESC;",
                connection);
            selectRevision.Parameters.AddWithValue("@PackageId", packageId);

            Guid revisionId;
            int revisionNumber;
            DateTimeOffset revisionCreatedAt;
            using (var reader = await selectRevision.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken))
                    return null; // Pacote sem revisão é estado inconsistente — trata como não encontrado.

                revisionId = reader.GetGuid(reader.GetOrdinal("RevisionId"));
                revisionNumber = reader.GetInt32(reader.GetOrdinal("RevisionNumber"));
                revisionCreatedAt = new DateTimeOffset(reader.GetDateTime(reader.GetOrdinal("CreatedAt")), TimeSpan.Zero);
            }

            var artifacts = new List<ArtifactSummary>();
            using (var selectArtifacts = new SqlCommand(
                @"SELECT ArtifactId, Kind, Sha256, SizeBytes, OriginalFileName, InspectionStatus, UploadedAt, Provenance
                  FROM dbo.tbPackageArtifact
                  WHERE RevisionId = @RevisionId
                  ORDER BY UploadedAt ASC;",
                connection))
            {
                selectArtifacts.Parameters.AddWithValue("@RevisionId", revisionId);
                using var reader = await selectArtifacts.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    artifacts.Add(new ArtifactSummary(
                        reader.GetGuid(reader.GetOrdinal("ArtifactId")),
                        reader.GetString(reader.GetOrdinal("Kind")),
                        reader.GetString(reader.GetOrdinal("Sha256")),
                        reader.GetInt64(reader.GetOrdinal("SizeBytes")),
                        reader.GetString(reader.GetOrdinal("OriginalFileName")),
                        reader.GetString(reader.GetOrdinal("InspectionStatus")),
                        new DateTimeOffset(reader.GetDateTime(reader.GetOrdinal("UploadedAt")), TimeSpan.Zero),
                        reader.IsDBNull(reader.GetOrdinal("Provenance")) ? null : reader.GetString(reader.GetOrdinal("Provenance"))));
                }
            }

            return new PackageDetail(
                packageId, workspaceId, projectId, name, createdAt,
                new RevisionSummary(revisionId, revisionNumber, revisionCreatedAt, artifacts));
        }

        public async Task<PackageDetail?> FindPackageByIdempotencyKeyAsync(Guid workspaceId, Guid projectId, string idempotencyKey, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            using var command = new SqlCommand(
                @"SELECT PackageId FROM dbo.tbFiscalMappingPackage
                  WHERE WorkspaceId = @WorkspaceId AND ProjectId = @ProjectId AND IdempotencyKey = @IdempotencyKey;",
                connection);
            command.Parameters.AddWithValue("@WorkspaceId", workspaceId);
            command.Parameters.AddWithValue("@ProjectId", projectId);
            command.Parameters.AddWithValue("@IdempotencyKey", idempotencyKey);

            var packageId = await command.ExecuteScalarAsync(cancellationToken);
            if (packageId is not Guid guid)
                return null;

            // Idempotência não exige checar membership aqui (chamador já validou antes de chegar
            // neste ponto do fluxo de criação) — reusa a leitura completa via GetPackageIfMemberAsync
            // não é possível sem userId; monta o detalhe direto.
            return await LoadPackageDetailAsync(connection, guid, cancellationToken);
        }

        private static async Task<PackageDetail?> LoadPackageDetailAsync(SqlConnection connection, Guid packageId, CancellationToken cancellationToken)
        {
            using var selectPackage = new SqlCommand(
                "SELECT WorkspaceId, ProjectId, Name, CreatedAt FROM dbo.tbFiscalMappingPackage WHERE PackageId = @PackageId;",
                connection);
            selectPackage.Parameters.AddWithValue("@PackageId", packageId);

            Guid workspaceId, projectId;
            string name;
            DateTimeOffset createdAt;
            using (var reader = await selectPackage.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken))
                    return null;

                workspaceId = reader.GetGuid(reader.GetOrdinal("WorkspaceId"));
                projectId = reader.GetGuid(reader.GetOrdinal("ProjectId"));
                name = reader.GetString(reader.GetOrdinal("Name"));
                createdAt = new DateTimeOffset(reader.GetDateTime(reader.GetOrdinal("CreatedAt")), TimeSpan.Zero);
            }

            using var selectRevision = new SqlCommand(
                @"SELECT TOP 1 RevisionId, RevisionNumber, CreatedAt
                  FROM dbo.tbFiscalMappingPackageRevision WHERE PackageId = @PackageId ORDER BY RevisionNumber DESC;",
                connection);
            selectRevision.Parameters.AddWithValue("@PackageId", packageId);

            Guid revisionId;
            int revisionNumber;
            DateTimeOffset revisionCreatedAt;
            using (var reader = await selectRevision.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken))
                    return null;

                revisionId = reader.GetGuid(reader.GetOrdinal("RevisionId"));
                revisionNumber = reader.GetInt32(reader.GetOrdinal("RevisionNumber"));
                revisionCreatedAt = new DateTimeOffset(reader.GetDateTime(reader.GetOrdinal("CreatedAt")), TimeSpan.Zero);
            }

            var artifacts = new List<ArtifactSummary>();
            using (var selectArtifacts = new SqlCommand(
                @"SELECT ArtifactId, Kind, Sha256, SizeBytes, OriginalFileName, InspectionStatus, UploadedAt, Provenance
                  FROM dbo.tbPackageArtifact WHERE RevisionId = @RevisionId ORDER BY UploadedAt ASC;",
                connection))
            {
                selectArtifacts.Parameters.AddWithValue("@RevisionId", revisionId);
                using var reader = await selectArtifacts.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    artifacts.Add(new ArtifactSummary(
                        reader.GetGuid(reader.GetOrdinal("ArtifactId")),
                        reader.GetString(reader.GetOrdinal("Kind")),
                        reader.GetString(reader.GetOrdinal("Sha256")),
                        reader.GetInt64(reader.GetOrdinal("SizeBytes")),
                        reader.GetString(reader.GetOrdinal("OriginalFileName")),
                        reader.GetString(reader.GetOrdinal("InspectionStatus")),
                        new DateTimeOffset(reader.GetDateTime(reader.GetOrdinal("UploadedAt")), TimeSpan.Zero),
                        reader.IsDBNull(reader.GetOrdinal("Provenance")) ? null : reader.GetString(reader.GetOrdinal("Provenance"))));
                }
            }

            return new PackageDetail(packageId, workspaceId, projectId, name, createdAt,
                new RevisionSummary(revisionId, revisionNumber, revisionCreatedAt, artifacts));
        }

        public async Task<ArtifactSummary?> FindArtifactByHashAsync(Guid packageId, string sha256, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            using var command = new SqlCommand(
                @"SELECT a.ArtifactId, a.Kind, a.Sha256, a.SizeBytes, a.OriginalFileName, a.InspectionStatus, a.UploadedAt, a.Provenance
                  FROM dbo.tbPackageArtifact a
                  JOIN dbo.tbFiscalMappingPackageRevision r ON r.RevisionId = a.RevisionId
                  WHERE r.PackageId = @PackageId AND a.Sha256 = @Sha256
                  ORDER BY a.UploadedAt DESC;",
                connection);
            command.Parameters.AddWithValue("@PackageId", packageId);
            command.Parameters.AddWithValue("@Sha256", sha256);

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            return new ArtifactSummary(
                reader.GetGuid(reader.GetOrdinal("ArtifactId")),
                reader.GetString(reader.GetOrdinal("Kind")),
                reader.GetString(reader.GetOrdinal("Sha256")),
                reader.GetInt64(reader.GetOrdinal("SizeBytes")),
                reader.GetString(reader.GetOrdinal("OriginalFileName")),
                reader.GetString(reader.GetOrdinal("InspectionStatus")),
                new DateTimeOffset(reader.GetDateTime(reader.GetOrdinal("UploadedAt")), TimeSpan.Zero),
                reader.IsDBNull(reader.GetOrdinal("Provenance")) ? null : reader.GetString(reader.GetOrdinal("Provenance")));
        }

        public async Task UpdateInspectionStatusAsync(Guid artifactId, string inspectionStatus, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            using var command = new SqlCommand(
                "UPDATE dbo.tbPackageArtifact SET InspectionStatus = @Status WHERE ArtifactId = @ArtifactId;",
                connection);
            command.Parameters.AddWithValue("@Status", inspectionStatus);
            command.Parameters.AddWithValue("@ArtifactId", artifactId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task<IReadOnlyList<ProjectSummary>> ListProjectsForMemberAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            // Join com tbLpWorkspaceMembership: defesa em profundidade — o controller já checou
            // membership antes de chamar, mas a store nunca confia cegamente no WorkspaceId da rota.
            using var command = new SqlCommand(
                @"SELECT p.ProjectId, p.WorkspaceId, p.Name, p.CreatedAt
                  FROM dbo.tbFiscalProject p
                  JOIN dbo.tbLpWorkspaceMembership m ON m.WorkspaceId = p.WorkspaceId AND m.UserId = @UserId
                  WHERE p.WorkspaceId = @WorkspaceId
                  ORDER BY p.CreatedAt DESC;",
                connection);
            command.Parameters.AddWithValue("@WorkspaceId", workspaceId);
            command.Parameters.AddWithValue("@UserId", userId);

            var projects = new List<ProjectSummary>();
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                projects.Add(new ProjectSummary(
                    reader.GetGuid(reader.GetOrdinal("ProjectId")),
                    reader.GetGuid(reader.GetOrdinal("WorkspaceId")),
                    reader.GetString(reader.GetOrdinal("Name")),
                    new DateTimeOffset(reader.GetDateTime(reader.GetOrdinal("CreatedAt")), TimeSpan.Zero)));
            }

            return projects;
        }

        public async Task<PackageDetail> CreateRevisionAsync(
            Guid packageId,
            Guid createdByUserId,
            IReadOnlyList<PackageArtifact> artifacts,
            CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            var revisionId = Guid.NewGuid();
            var createdAt = DateTimeOffset.UtcNow;

            using var tx = connection.BeginTransaction();
            try
            {
                int revisionNumber;
                using (var selectMax = new SqlCommand(
                    "SELECT ISNULL(MAX(RevisionNumber), 0) FROM dbo.tbFiscalMappingPackageRevision WHERE PackageId = @PackageId;",
                    connection, tx))
                {
                    selectMax.Parameters.AddWithValue("@PackageId", packageId);
                    revisionNumber = (int)await selectMax.ExecuteScalarAsync(cancellationToken) + 1;
                }

                using (var insertRevision = new SqlCommand(
                    @"INSERT INTO dbo.tbFiscalMappingPackageRevision (RevisionId, PackageId, RevisionNumber, CreatedByUserId, CreatedAt)
                      VALUES (@RevisionId, @PackageId, @RevisionNumber, @CreatedByUserId, SYSUTCDATETIME());",
                    connection, tx))
                {
                    insertRevision.Parameters.AddWithValue("@RevisionId", revisionId);
                    insertRevision.Parameters.AddWithValue("@PackageId", packageId);
                    insertRevision.Parameters.AddWithValue("@RevisionNumber", revisionNumber);
                    insertRevision.Parameters.AddWithValue("@CreatedByUserId", createdByUserId);
                    await insertRevision.ExecuteNonQueryAsync(cancellationToken);
                }

                foreach (var artifact in artifacts)
                {
                    artifact.ArtifactId = artifact.ArtifactId == Guid.Empty ? Guid.NewGuid() : artifact.ArtifactId;
                    artifact.RevisionId = revisionId;

                    using var insertArtifact = new SqlCommand(
                        @"INSERT INTO dbo.tbPackageArtifact
                            (ArtifactId, RevisionId, Kind, Sha256, SizeBytes, OriginalFileName, MimeDeclared, MimeSniffed,
                             UploadedByUserId, UploadedAt, Classification, RetentionPolicy, InspectionStatus, StoragePath, Provenance)
                          VALUES
                            (@ArtifactId, @RevisionId, @Kind, @Sha256, @SizeBytes, @OriginalFileName, @MimeDeclared, @MimeSniffed,
                             @UploadedByUserId, SYSUTCDATETIME(), @Classification, @RetentionPolicy, @InspectionStatus, @StoragePath, @Provenance);",
                        connection, tx);

                    insertArtifact.Parameters.AddWithValue("@ArtifactId", artifact.ArtifactId);
                    insertArtifact.Parameters.AddWithValue("@RevisionId", revisionId);
                    insertArtifact.Parameters.AddWithValue("@Kind", artifact.Kind);
                    insertArtifact.Parameters.AddWithValue("@Sha256", artifact.Sha256);
                    insertArtifact.Parameters.AddWithValue("@SizeBytes", artifact.SizeBytes);
                    insertArtifact.Parameters.AddWithValue("@OriginalFileName", artifact.OriginalFileName);
                    insertArtifact.Parameters.AddWithValue("@MimeDeclared", artifact.MimeDeclared);
                    insertArtifact.Parameters.AddWithValue("@MimeSniffed", artifact.MimeSniffed);
                    insertArtifact.Parameters.AddWithValue("@UploadedByUserId", artifact.UploadedByUserId);
                    insertArtifact.Parameters.AddWithValue("@Classification", (object?)artifact.Classification ?? DBNull.Value);
                    insertArtifact.Parameters.AddWithValue("@RetentionPolicy", (object?)artifact.RetentionPolicy ?? DBNull.Value);
                    insertArtifact.Parameters.AddWithValue("@InspectionStatus", artifact.InspectionStatus);
                    insertArtifact.Parameters.AddWithValue("@StoragePath", artifact.StoragePath);
                    insertArtifact.Parameters.AddWithValue("@Provenance", (object?)artifact.Provenance ?? DBNull.Value);
                    await insertArtifact.ExecuteNonQueryAsync(cancellationToken);
                }

                await tx.CommitAsync(cancellationToken);

                var packageDetail = await LoadPackageHeaderAsync(connection, packageId, cancellationToken)
                    ?? throw new InvalidOperationException($"Pacote {packageId} sumiu durante a criação da revisão.");

                return packageDetail with
                {
                    LatestRevision = new RevisionSummary(
                        revisionId,
                        revisionNumber,
                        createdAt,
                        artifacts.Select(a => new ArtifactSummary(a.ArtifactId, a.Kind, a.Sha256, a.SizeBytes, a.OriginalFileName, a.InspectionStatus, createdAt, a.Provenance)).ToList())
                };
            }
            catch
            {
                await tx.RollbackAsync(cancellationToken);
                throw;
            }
        }

        /// <summary>Só o cabeçalho do pacote (sem revisão) — usado internamente por <see cref="CreateRevisionAsync"/>.</summary>
        private static async Task<PackageDetail?> LoadPackageHeaderAsync(SqlConnection connection, Guid packageId, CancellationToken cancellationToken)
        {
            using var command = new SqlCommand(
                "SELECT WorkspaceId, ProjectId, Name, CreatedAt FROM dbo.tbFiscalMappingPackage WHERE PackageId = @PackageId;",
                connection);
            command.Parameters.AddWithValue("@PackageId", packageId);

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            return new PackageDetail(
                packageId,
                reader.GetGuid(reader.GetOrdinal("WorkspaceId")),
                reader.GetGuid(reader.GetOrdinal("ProjectId")),
                reader.GetString(reader.GetOrdinal("Name")),
                new DateTimeOffset(reader.GetDateTime(reader.GetOrdinal("CreatedAt")), TimeSpan.Zero),
                null!); // LatestRevision preenchida pelo chamador.
        }

        public async Task<string?> GetArtifactStoragePathAsync(Guid artifactId, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            using var command = new SqlCommand(
                "SELECT StoragePath FROM dbo.tbPackageArtifact WHERE ArtifactId = @ArtifactId;",
                connection);
            command.Parameters.AddWithValue("@ArtifactId", artifactId);

            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result as string;
        }

        // ✅ Exposta como campo (não mais `const string` local) para o
        // `FiscalSchemaInitializer` poder inicializar o schema fiscal inteiro, em ORDEM, uma única
        // vez no startup — em vez de depender de qual store é exercitado primeiro por uma requisição
        // real (causa raiz do 503 "MappingDraftStore falhou: FK ... references invalid table" da
        // PR #310: `tbMappingDraft` podia ser criado antes de `tbFiscalMappingPackage` existir).
        //
        // ⚠️ `WorkspaceId` NÃO tem `REFERENCES dbo.tbLpFiscalWorkspace(WorkspaceId)` mesmo agora que
        // este store mora no MESMO banco (`IdentityDatabase:*`) que `tbLpFiscalWorkspace` (ver
        // <see cref="SqlIdentityWorkspaceStore"/>). Historicamente essa FK nunca funcionou (apontava
        // para `dbo.tbFiscalWorkspace`, tabela que não existia em banco nenhum) porque este store
        // vivia fisicamente separado (`Database:*`, ConnectUS_Macgyver/Sysmiddle) antes da migração
        // documentada no cabeçalho da classe. Agora que os dois moram juntos, uma FK real seria
        // tecnicamente possível — mantida de fora por ora para não acoplar a ordem de schema deste
        // store à de `SqlIdentityWorkspaceStore` sem necessidade; a validação de que o `WorkspaceId`
        // existe/pertence ao usuário continua responsabilidade da camada de aplicação (via
        // `IIdentityWorkspaceStore`).
        public static readonly string SchemaDdl = @"
IF OBJECT_ID('dbo.tbFiscalProject', 'U') IS NULL
CREATE TABLE dbo.tbFiscalProject (
    ProjectId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    WorkspaceId UNIQUEIDENTIFIER NOT NULL,
    Name NVARCHAR(256) NOT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

IF OBJECT_ID('dbo.tbFiscalMappingPackage', 'U') IS NULL
CREATE TABLE dbo.tbFiscalMappingPackage (
    PackageId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    WorkspaceId UNIQUEIDENTIFIER NOT NULL,
    ProjectId UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.tbFiscalProject(ProjectId),
    Name NVARCHAR(256) NOT NULL,
    IdempotencyKey NVARCHAR(128) NOT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_tbFiscalMappingPackage_Idempotency UNIQUE (WorkspaceId, ProjectId, IdempotencyKey)
);

IF OBJECT_ID('dbo.tbFiscalMappingPackageRevision', 'U') IS NULL
CREATE TABLE dbo.tbFiscalMappingPackageRevision (
    RevisionId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    PackageId UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.tbFiscalMappingPackage(PackageId),
    RevisionNumber INT NOT NULL,
    CreatedByUserId UNIQUEIDENTIFIER NOT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_tbFiscalMappingPackageRevision UNIQUE (PackageId, RevisionNumber)
);

IF OBJECT_ID('dbo.tbPackageArtifact', 'U') IS NULL
CREATE TABLE dbo.tbPackageArtifact (
    ArtifactId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    RevisionId UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.tbFiscalMappingPackageRevision(RevisionId),
    Kind NVARCHAR(32) NOT NULL,
    Sha256 CHAR(64) NOT NULL,
    SizeBytes BIGINT NOT NULL,
    OriginalFileName NVARCHAR(512) NOT NULL,
    MimeDeclared NVARCHAR(128) NOT NULL,
    MimeSniffed NVARCHAR(128) NOT NULL,
    UploadedByUserId UNIQUEIDENTIFIER NOT NULL,
    UploadedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    Classification NVARCHAR(64) NULL,
    RetentionPolicy NVARCHAR(64) NULL,
    InspectionStatus NVARCHAR(16) NOT NULL,
    StoragePath NVARCHAR(1024) NOT NULL
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_tbPackageArtifact_RevisionId' AND object_id = OBJECT_ID('dbo.tbPackageArtifact'))
CREATE INDEX IX_tbPackageArtifact_RevisionId ON dbo.tbPackageArtifact(RevisionId);

-- ✅ issue #341 (F2): coluna nova em tabela que pode já existir num banco antigo — ALTER idempotente
-- separado do CREATE TABLE acima (que só roda se a tabela ainda não existir).
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.tbPackageArtifact') AND name = 'Provenance')
ALTER TABLE dbo.tbPackageArtifact ADD Provenance NVARCHAR(32) NULL;";

        // internal (não mais private): chamado também pelo FiscalSchemaInitializer no startup, além
        // do próprio store por requisição (idempotente — safety net se o initializer não rodou/falhou).
        internal static async Task EnsureSchemaAsync(SqlConnection connection, CancellationToken cancellationToken)
        {
            if (_schemaEnsured)
                return;

            await _schemaLock.WaitAsync(cancellationToken);
            try
            {
                if (_schemaEnsured)
                    return;

                using var command = new SqlCommand(SchemaDdl, connection);
                await command.ExecuteNonQueryAsync(cancellationToken);
                _schemaEnsured = true;
            }
            finally
            {
                _schemaLock.Release();
            }
        }
    }
}
