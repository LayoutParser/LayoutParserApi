using LayoutParserApi.Services.Interfaces;
using LayoutParserApi.Services.Transformation.LowCode;

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

using StackExchange.Redis;

namespace LayoutParserApi.Services.Health
{
    /// <summary>
    /// Sondas de readiness (P1.3 do plano de segurança). Cada uma testa UMA dependência de verdade,
    /// ao contrário do health anterior que devolvia 200 sem tocar em nada.
    ///
    /// <para><b>Severidade e mapeamento HTTP</b> (em /health/ready): Unhealthy → 503, Degraded e
    /// Healthy → 200. Redis e runner ausentes são <b>Degraded</b> (a app serve sem eles — resiliência
    /// é princípio do projeto); SQL fora e catálogo vazio são <b>Unhealthy</b> (a API não consegue
    /// servir layout).</para>
    /// </summary>
    public sealed class SqlServerHealthCheck : IHealthCheck
    {
        private readonly IConfiguration _configuration;

        public SqlServerHealthCheck(IConfiguration configuration) => _configuration = configuration;

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            // Connection string com timeout CURTO de propósito: é sonda, não caminho de dados (que
            // usa 30s em LayoutDatabaseService). Uma sonda que espera 30s não serve de sonda.
            var server = _configuration["Database:Server"] ?? "";
            var database = _configuration["Database:Database"] ?? "";
            var userId = _configuration["Database:UserId"] ?? "";
            var password = _configuration["Database:Password"] ?? string.Empty;
            var encrypt = _configuration["Database:Encrypt"]?.ToLower() ?? "false";

            var connectionString =
                $"Server={server};Database={database};User Id={userId};Password={password};" +
                $"TrustServerCertificate=true;Encrypt={encrypt};" +
                "Connection Timeout=3;Command Timeout=3;Pooling=true;";

            try
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = new SqlCommand("SELECT 1", connection) { CommandTimeout = 3 };
                await command.ExecuteScalarAsync(cancellationToken);
                return HealthCheckResult.Healthy("SQL Server respondeu SELECT 1.");
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy("SQL Server indisponivel (SELECT 1 falhou).", ex);
            }
        }
    }

    /// <summary>Redis ausente/desconectado = <b>Degraded</b> (cache opera por disco/banco), nunca Unhealthy.</summary>
    public sealed class RedisHealthCheck : IHealthCheck
    {
        private readonly IConnectionMultiplexer? _redis;

        // Injeção OPCIONAL: IConnectionMultiplexer só está no container quando a conexão subiu.
        public RedisHealthCheck(IConnectionMultiplexer? redis = null) => _redis = redis;

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            if (_redis is null || !_redis.IsConnected)
                return HealthCheckResult.Degraded("Redis ausente/desconectado — cache opera sem Redis (por disco/banco).");

            try
            {
                var latency = await _redis.GetDatabase().PingAsync();
                return HealthCheckResult.Healthy($"Redis respondeu PING ({latency.TotalMilliseconds:F0}ms).");
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Degraded("Redis falhou no PING — cache degradado.", ex);
            }
        }
    }

    /// <summary>
    /// Existência do executável de descriptografia. Ausente = <b>Degraded</b> (informativo): o gate
    /// duro é o <see cref="CatalogHealthCheck"/> — sem decryptor o catálogo volta vazio e AQUELE
    /// vira Unhealthy. Este só explica a causa.
    /// </summary>
    public sealed class DecryptorHealthCheck : IHealthCheck
    {
        private readonly IDecryptionService _decryption;

        public DecryptorHealthCheck(IDecryptionService decryption) => _decryption = decryption;

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_decryption.IsDecryptorAvailable
                ? HealthCheckResult.Healthy("Executavel de descriptografia encontrado.")
                : HealthCheckResult.Degraded("LayoutParserDecrypt.exe ausente — catalogo tende a ficar vazio (ver catalog)."));
        }
    }

    /// <summary>Existência do runner low-code. Ausente = <b>Degraded</b> (transformação indisponível, mas parse/catálogo servem).</summary>
    public sealed class LowCodeRunnerHealthCheck : IHealthCheck
    {
        private readonly IOptions<LowCodeRunnerOptions> _options;

        public LowCodeRunnerHealthCheck(IOptions<LowCodeRunnerOptions> options) => _options = options;

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            var path = _options.Value.RunnerPath;

            if (string.IsNullOrWhiteSpace(path))
                return Task.FromResult(HealthCheckResult.Degraded("LowCode:RunnerPath nao configurado — transformacao low-code indisponivel."));

            if (!File.Exists(path))
                return Task.FromResult(HealthCheckResult.Degraded($"LowCode:RunnerPath nao existe ({path}) — transformacao low-code indisponivel."));

            return Task.FromResult(HealthCheckResult.Healthy("Runner low-code encontrado."));
        }
    }

    /// <summary>
    /// Contagem do warm-up. Catálogo vazio (ou warm-up não concluído) = <b>Unhealthy</b> → readiness
    /// falha (503). É o gate que fecha a classe "deploy publica versão inoperante e declara sucesso".
    /// </summary>
    public sealed class CatalogHealthCheck : IHealthCheck
    {
        private readonly CatalogWarmupState _state;

        public CatalogHealthCheck(CatalogWarmupState state) => _state = state;

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            if (!_state.Completed)
                return Task.FromResult(HealthCheckResult.Unhealthy("Warm-up do catalogo ainda nao concluiu — instancia nao esta pronta."));

            if (_state.LayoutCount <= 0)
                return Task.FromResult(HealthCheckResult.Unhealthy("Catalogo de layouts VAZIO apos warm-up (SQL/decryptor fora?) — instancia nao esta pronta."));

            return Task.FromResult(HealthCheckResult.Healthy($"Catalogo com {_state.LayoutCount} layouts."));
        }
    }
}
