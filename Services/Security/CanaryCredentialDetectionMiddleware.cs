using System.Security.Cryptography;
using System.Text;

namespace LayoutParserApi.Services.Security
{
    /// <summary>
    /// Detecta o uso da credencial-isca "aposentada" (<see cref="CanaryConstants.LegacyCredentialHeader"/>)
    /// — ADR M2M, Parte 2 ("Honeypots / Canary Tokens" → "Credencial-isca").
    /// </summary>
    /// <remarks>
    /// <para>🔴 <b>ISTO É DETECÇÃO, NÃO PREVENÇÃO — E O OPOSTO DE AUTENTICAÇÃO.</b> Se o header
    /// bater contra o valor conhecido, este middleware NUNCA autentica nada (não popula
    /// <c>HttpContext.User</c>, não define nenhuma claim) — só dispara
    /// <see cref="ICanaryAlertService"/> e deixa o pipeline seguir normalmente. Reconhecer a
    /// credencial é exatamente o inverso do propósito de um mecanismo de auth: aqui, reconhecer
    /// significa "isto nunca deveria ter sido usado", não "isto prova identidade". Não
    /// "consertar" isto para aceitar a credencial de verdade — ver ADR, tabela de riscos:
    /// "Confundir a credencial-isca com um mecanismo de auth real".</para>
    ///
    /// <para><b>Nunca denuncia a detecção pela resposta:</b> o request continua exatamente como
    /// seguiria sem este middleware — se a credencial não convencer nenhum controle de auth real
    /// mais adiante (e não deveria, porque nenhum deles reconhece este header), o cliente recebe o
    /// mesmo 401/403 que qualquer requisição sem credencial válida receberia. Isso evita ensinar um
    /// atacante a distinguir "credencial errada comum" de "credencial canary específica".</para>
    ///
    /// <para><b>Ordem no pipeline:</b> roda ANTES de qualquer middleware de autenticação real
    /// (<see cref="TrustedIdentityMiddleware"/>, <c>UseAuthentication</c>) — registrado logo após o
    /// middleware de <c>CorrelationId</c> em <c>Program.cs</c>, para garantir que o alarme dispara
    /// mesmo que o valor canary por acaso colida com alguma validação futura.</para>
    /// </remarks>
    public sealed class CanaryCredentialDetectionMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ICanaryAlertService _canaryAlert;

        public CanaryCredentialDetectionMiddleware(RequestDelegate next, ICanaryAlertService canaryAlert)
        {
            _next = next;
            _canaryAlert = canaryAlert;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var recebido = context.Request.Headers[CanaryConstants.LegacyCredentialHeader].ToString();

            if (!string.IsNullOrEmpty(recebido) && ValorConfere(recebido, CanaryConstants.LegacyCredentialValue))
            {
                _canaryAlert.Raise(CanaryConstants.CredentialCanaryType, context);
            }

            // Sempre segue — nunca autentica, nunca bloqueia por conta própria. Ver remarks acima.
            await _next(context);
        }

        // Comparação em tempo constante — mesmo padrão já usado em AiMetricsIngestKeyFilter
        // (FixedTimeEquals já devolve false para tamanhos diferentes, sem lançar).
        private static bool ValorConfere(string recebido, string esperado)
        {
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(recebido),
                Encoding.UTF8.GetBytes(esperado));
        }
    }
}
