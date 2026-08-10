using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LayoutParserApi.Services.Filters
{
    /// <summary>
    /// Porta de entrada por chave compartilhada para TODA a API. Aplicado globalmente
    /// (<c>MvcOptions.Filters</c> em Program.cs), não por controller.
    /// </summary>
    /// <remarks>
    /// <para><b>Por que existe.</b> A app não tem pipeline de autenticação — <c>UseAuthentication</c>
    /// nunca existiu e <c>UseAuthorization</c> segue comentado. São 18 controllers e, até aqui,
    /// apenas 2 endpoints (ingestão de métricas) exigiam credencial. Todo o resto — upload e parse
    /// de documento, catálogo de layouts, execução de transformação, leitura de log — respondia a
    /// qualquer host da rede, em HTTP puro, servindo NF-e real com CNPJ e valores.</para>
    ///
    /// <para><b>Por que nasce DESLIGADO.</b> Ao contrário do
    /// <see cref="AiMetricsIngestKeyFilter"/>, que é fail-CLOSED, este é fail-OPEN: sem
    /// <c>Security:ApiKey</c> configurada, ele deixa passar. A divergência é deliberada e não é
    /// descuido. O filtro cobre a superfície inteira, inclusive o que o front consome hoje; se
    /// nascesse fail-closed, o primeiro deploy derrubaria o LayoutParserReact e o smoke test do
    /// próprio CI (que chama <c>GET /api/document/layouts</c>, uma rota de negócio) — a API subiria
    /// "segura" e inútil. Um mecanismo que ninguém consegue ligar sem quebrar produção não protege
    /// nada; ele é revertido no dia seguinte.</para>
    ///
    /// <para><b>Como ligar</b> (ordem importa, não pule o passo 1):
    /// <list type="number">
    /// <item>Descubra o que precisa de acesso anônimo e liste em <c>Security:AnonymousPaths</c>
    /// (prefixos de rota). No mínimo, o endpoint usado pelo smoke test do deploy.</item>
    /// <item>Provisione a chave no ambiente do serviço: <c>Security__ApiKey</c>. NÃO adiante pelo
    /// appsettings.json — o deploy preserva o do destino e o valor não chegaria ao servidor.</item>
    /// <item>Configure o front (header <c>X-Api-Key</c>) e o secret do CI, antes de ligar em prod.</item>
    /// </list></para>
    ///
    /// <para>Isto é um degrau, não o destino: chave compartilhada não identifica quem chamou nem
    /// permite revogação individual. O destino recomendado é autenticação integrada
    /// (Windows/Negotiate), que o ambiente já suporta por ser AD. E enquanto <c>UseHttpsRedirection</c>
    /// seguir comentado, a chave trafega em claro — ligar isto sem TLS dá sensação de segurança,
    /// não segurança.</para>
    /// </remarks>
    public class ApiKeyGateFilter : IAuthorizationFilter
    {
        /// <summary>Header que carrega a chave compartilhada.</summary>
        public const string HeaderName = "X-Api-Key";

        /// <summary>Chave de configuração (env var equivalente: <c>Security__ApiKey</c>).</summary>
        public const string ConfigKey = "Security:ApiKey";

        /// <summary>Prefixos de rota liberados sem chave (env var: <c>Security__AnonymousPaths__0</c>, ...).</summary>
        public const string AnonymousPathsKey = "Security:AnonymousPaths";

        private readonly IConfiguration _configuration;
        private readonly ILogger<ApiKeyGateFilter> _logger;

        public ApiKeyGateFilter(IConfiguration configuration, ILogger<ApiKeyGateFilter> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        // IAuthorizationFilter (e não IActionFilter): roda ANTES do model binding, então um upload
        // não autorizado é recusado sem desserializar o corpo — que aqui pode ser um documento inteiro.
        public void OnAuthorization(AuthorizationFilterContext context)
        {
            var chaveEsperada = _configuration[ConfigKey];

            // ✅ Gate desligado: nenhuma chave provisionada. Não loga por request de propósito —
            // seria uma linha por chamada num log que é lido de volta pelo painel. O aviso único
            // sai no arranque (Program.cs).
            if (string.IsNullOrWhiteSpace(chaveEsperada))
                return;

            var rota = context.HttpContext.Request.Path.Value ?? string.Empty;

            if (RotaEhAnonima(rota))
                return;

            var chaveRecebida = context.HttpContext.Request.Headers[HeaderName].ToString();

            if (ChaveConfere(chaveRecebida, chaveEsperada))
                return;

            // Loga a SITUAÇÃO do header, nunca o valor recebido nem o esperado: este log é relido
            // como fonte de dados e exposto por GET api/logs.
            _logger.LogWarning(
                "Requisição recusada em {Rota}: header {Header} {Situacao}.",
                rota,
                HeaderName,
                string.IsNullOrEmpty(chaveRecebida) ? "ausente" : "inválido");

            context.Result = new ObjectResult(new
            {
                success = false,
                error = $"Header '{HeaderName}' ausente ou inválido."
            })
            { StatusCode = StatusCodes.Status401Unauthorized };
        }

        private bool RotaEhAnonima(string rota)
        {
            var anonimas = _configuration.GetSection(AnonymousPathsKey).Get<string[]>();
            if (anonimas == null || anonimas.Length == 0)
                return false;

            return ApiKeyGatePolicy.RotaBateComAlgumPrefixo(rota, anonimas);
        }

        // Comparação em tempo constante, mesmo padrão do AiMetricsIngestKeyFilter:
        // FixedTimeEquals devolve false para tamanhos diferentes sem lançar.
        private static bool ChaveConfere(string? recebida, string esperada)
        {
            if (string.IsNullOrEmpty(recebida))
                return false;

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(recebida),
                Encoding.UTF8.GetBytes(esperada));
        }
    }

    /// <summary>
    /// Regra de casamento de rota anônima, separada do filtro para rodar na suíte.
    ///
    /// <para>Mora aqui, e não inline no filtro, pelo mesmo motivo de
    /// <c>LowCodeCandidatesBudget</c>: o filtro depende de <c>AuthorizationFilterContext</c> e de
    /// <c>IConfiguration</c>; esta classe é pura. E é exatamente a parte que precisa de teste — um
    /// prefixo frouxo demais abre a API inteira sem ninguém notar.</para>
    /// </summary>
    public static class ApiKeyGatePolicy
    {
        public static bool RotaBateComAlgumPrefixo(string? rota, IEnumerable<string>? prefixos)
        {
            if (prefixos == null || string.IsNullOrWhiteSpace(rota))
                return false;

            foreach (var prefixo in prefixos)
            {
                if (string.IsNullOrWhiteSpace(prefixo))
                    continue;

                // ⚠️ Casamento por SEGMENTO, não por substring. Com StartsWith cru, o prefixo
                // "/api/document" liberaria também "/api/documentos-confidenciais" — um prefixo
                // inocente viraria buraco. Exige igualdade exata ou que o próximo caractere da
                // rota seja '/', que é o único jeito de "prefixo" significar "esta rota ou abaixo dela".
                var p = prefixo.TrimEnd('/');

                if (rota.Equals(p, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (rota.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
