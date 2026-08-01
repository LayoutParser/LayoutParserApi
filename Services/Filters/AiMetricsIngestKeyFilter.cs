using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LayoutParserApi.Services.Filters
{
    /// <summary>
    /// Chave compartilhada para os endpoints de ESCRITA de métricas de IA
    /// (POST api/ai-metrics/generations/ingest e POST api/ai-metrics/cypress-result). O que eles
    /// gravam vira número no painel apresentado à diretoria, e a app não tem pipeline de
    /// autenticação (<c>UseAuthorization</c> segue comentado em Program.cs) — sem isto, qualquer
    /// um na rede injeta métrica falsa.
    /// </summary>
    /// <remarks>
    /// Decisão de arquitetura (@lp-architect, 2026-07-31): chave em header, <b>não</b> allowlist de
    /// IP. O IP da VM produtora já mudou 3 vezes em 2 semanas por DHCP (.30 → .31 → .3); uma
    /// allowlist viraria bloqueio silencioso na próxima troca. Também não é OIDC/JWT: seria
    /// desproporcional ao resto do projeto. É proporcional, barato e melhor que "aberto".
    /// <para>
    /// <b>Fail-closed:</b> chave NÃO configurada = escrita recusada (403), não liberada. Seguro
    /// hoje porque nenhum produtor chama estes endpoints ainda; quando o job da VM passar a
    /// chamar, o operador precisa provisionar <c>AiMetrics__IngestApiKey</c> no ambiente do
    /// serviço (o ci-dev.yml preserva o appsettings.json do destino, então a chave NÃO chega ao
    /// servidor pelo arquivo — é env var, mesmo mecanismo de <c>Database__Password</c>).
    /// </para>
    /// <para>
    /// O valor da chave <b>nunca</b> é logado (nem em Debug): o log é relido como fonte de dados
    /// do painel e é exposto por GET api/logs.
    /// </para>
    /// </remarks>
    public class AiMetricsIngestKeyFilter : IAuthorizationFilter
    {
        /// <summary>Header que carrega a chave compartilhada.</summary>
        public const string HeaderName = "X-AiMetrics-Key";

        /// <summary>Chave de configuração (env var equivalente: <c>AiMetrics__IngestApiKey</c>).</summary>
        public const string ConfigKey = "AiMetrics:IngestApiKey";

        private readonly IConfiguration _configuration;
        private readonly ILogger<AiMetricsIngestKeyFilter> _logger;

        public AiMetricsIngestKeyFilter(IConfiguration configuration, ILogger<AiMetricsIngestKeyFilter> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        // IAuthorizationFilter (e não IActionFilter) de propósito: roda ANTES do model binding, então
        // um POST não autorizado é recusado sem desserializar o corpo (o lote pode ter até 1000 itens).
        public void OnAuthorization(AuthorizationFilterContext context)
        {
            var endpoint = context.HttpContext.Request.Path.Value ?? string.Empty;
            var chaveEsperada = _configuration[ConfigKey];

            if (string.IsNullOrWhiteSpace(chaveEsperada))
            {
                // ✅ Fail-closed. Warning (não Error) porque é config ausente, não falha da app —
                // e o texto diz ao operador exatamente o que provisionar, sem citar valor nenhum.
                _logger.LogWarning(
                    "Escrita de métricas de IA recusada em {Endpoint}: chave de ingestão não configurada. Defina a variável de ambiente AiMetrics__IngestApiKey no serviço.",
                    endpoint);

                context.Result = Recusar(
                    StatusCodes.Status403Forbidden,
                    "Escrita de métricas de IA desabilitada: chave de ingestão não configurada no servidor.");
                return;
            }

            var chaveRecebida = context.HttpContext.Request.Headers[HeaderName].ToString();

            if (!ChaveConfere(chaveRecebida, chaveEsperada))
            {
                // Loga só a SITUAÇÃO do header (ausente/inválido), nunca o valor recebido nem o esperado.
                _logger.LogWarning(
                    "Escrita de métricas de IA recusada em {Endpoint}: header {Header} {Situacao}.",
                    endpoint,
                    HeaderName,
                    string.IsNullOrEmpty(chaveRecebida) ? "ausente" : "inválido");

                context.Result = Recusar(
                    StatusCodes.Status401Unauthorized,
                    $"Header '{HeaderName}' ausente ou inválido.");
            }
        }

        private static ObjectResult Recusar(int statusCode, string erro)
            => new(new { success = false, error = erro }) { StatusCode = statusCode };

        // Comparação em tempo constante: FixedTimeEquals já devolve false pra tamanhos diferentes,
        // sem lançar. Barato aqui e evita o vazamento de prefixo por tempo de resposta.
        private static bool ChaveConfere(string? recebida, string esperada)
        {
            if (string.IsNullOrEmpty(recebida))
                return false;

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(recebida),
                Encoding.UTF8.GetBytes(esperada));
        }
    }
}
