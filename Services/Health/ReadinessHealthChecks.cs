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
    /// Contagem do warm-up. É o gate que fecha a classe "deploy publica versão inoperante e declara
    /// sucesso" — e distingue <b>TRÊS</b> estados, não dois:
    ///
    /// <list type="bullet">
    ///   <item><b>Aquecendo</b> (warm-up não concluiu / retry em andamento) → Unhealthy, mas marcado
    ///   como <c>transitorio=true</c>: é "ainda não pronto", não "quebrado".</item>
    ///   <item><b>Pronto</b> (concluiu com layouts &gt; 0) → Healthy. Único estado saudável.</item>
    ///   <item><b>Vazio</b> (concluiu e o catálogo veio VAZIO) → Unhealthy <c>transitorio=false</c>:
    ///   falha definitiva, exige intervenção (SQL/decryptor fora, catálogo sem layouts).</item>
    /// </list>
    ///
    /// <para><b>Por que "Aquecendo" é Unhealthy e não Degraded:</b> Degraded mapeia para 200 em
    /// <c>/health/ready</c> (ver Program.cs) e o smoke test de deploy exige 200 ESTRITO — reportar
    /// Degraded enquanto aquece faria o deploy declarar sucesso com o catálogo ainda vazio, que é
    /// exatamente o bug que esta sonda existe para fechar. O smoke test já trata 503 como "ainda não
    /// pronto" e retenta, então manter Unhealthy não trava deploy nenhum; o que muda é a
    /// <b>descrição/payload</b>, que agora diz claramente qual dos dois 503 é.</para>
    /// </summary>
    public sealed class CatalogHealthCheck : IHealthCheck
    {
        private readonly CatalogWarmupState _state;

        public CatalogHealthCheck(CatalogWarmupState state) => _state = state;

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            var status = _state.Status;

            // Payload legível pelo operador (o corpo do /health/ready aparece no log do smoke test)
            // e por monitoramento — permite alertar só no estado definitivo, sem barulho de warm-up.
            var data = new Dictionary<string, object>
            {
                ["estado"] = status.ToString(),
                ["layoutCount"] = _state.LayoutCount,
                ["transitorio"] = status == CatalogWarmupStatus.Aquecendo,
                ["tentativasFalhas"] = _state.FailedAttempts
            };

            if (!string.IsNullOrEmpty(_state.LastFailureReason))
                data["ultimaFalha"] = _state.LastFailureReason!;

            var resultado = status switch
            {
                CatalogWarmupStatus.Pronto =>
                    HealthCheckResult.Healthy($"Catalogo com {_state.LayoutCount} layouts.", data),

                CatalogWarmupStatus.Vazio =>
                    HealthCheckResult.Unhealthy(
                        "Catalogo de layouts VAZIO apos warm-up CONCLUIDO (SQL/decryptor fora, ou catalogo sem layouts) — " +
                        "falha DEFINITIVA, nao se resolve sozinha: exige intervencao.",
                        exception: null,
                        data: data),

                // Aquecendo (inclui a 1ª tentativa e todo retry com backoff da issue #67).
                _ =>
                    HealthCheckResult.Unhealthy(
                        $"Catalogo AQUECENDO — warm-up ainda nao concluiu ({_state.FailedAttempts} tentativa(s) falharam ate agora). " +
                        "Transitorio: o retry com backoff ainda pode recuperar sozinho, NAO e falha definitiva.",
                        exception: null,
                        data: data)
            };

            return Task.FromResult(resultado);
        }
    }
}
