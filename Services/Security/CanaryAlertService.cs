using LayoutParserApi.Services.Logging;

using Microsoft.AspNetCore.Http;

namespace LayoutParserApi.Services.Security
{
    /// <summary>
    /// Mecanismo de alerta da camada de honeypot/canary — ADR M2M, Parte 2. Sempre que o
    /// endpoint-isca ou a credencial-isca (<see cref="CanaryConstants"/>) forem acionados, este
    /// serviço grava o alarme.
    /// </summary>
    /// <remarks>
    /// <para>🔴 <b>DETECÇÃO, NÃO PREVENÇÃO.</b> Este serviço nunca autentica, autoriza, bloqueia
    /// ou modifica a resposta HTTP — só registra que algo tocou numa isca. Quem decide o que fazer
    /// com o request (deixar seguir, negar por outro motivo real) é o resto do pipeline, sem
    /// nenhuma influência deste serviço.</para>
    ///
    /// <para><b>Nível <c>Critical</c> de propósito:</b> nenhum log operacional legítimo deste
    /// projeto usa esse nível hoje — torna o grep/alerta trivial de distinguir de ruído normal
    /// (ver ADR, "Mecanismo de alerta — nunca silencioso").</para>
    ///
    /// <para><b>Extensão futura — e-mail:</b> o projeto já tem um mecanismo de alerta por e-mail
    /// (<c>dawidd6/action-send-mail@v3</c> em <c>.github/workflows/deploy.yml</c>, secrets
    /// <c>SMTP_*</c>/<c>ALERT_EMAIL_TO</c> documentados em <c>.claude/rules/security.md</c>), mas
    /// ele só dispara dentro de um workflow de CI (contexto de deploy) — não serve diretamente a
    /// um evento de runtime da API. Para estender o alarme aqui a e-mail sem inventar um pipeline
    /// novo, a rota recomendada (ADR, "Mecanismo de alerta") é adicionar um sink condicional do
    /// Serilog restrito a <c>LogEventLevel.Fatal</c>/<c>Critical</c> — ex. pacote gratuito
    /// <c>Serilog.Sinks.Email</c> — reaproveitando os MESMOS secrets <c>SMTP_*</c>/
    /// <c>ALERT_EMAIL_TO</c> já documentados (nenhuma config nova). Não implementado nesta
    /// mudança porque (a) o provedor SMTP ainda não foi escolhido pelo dono (mesma pendência do
    /// alerta de deploy) e (b) adicionar uma dependência de rede (SMTP) num pacote NuGet novo é
    /// esforço desproporcional a uma pendência que já está com o dono; enquanto isso não acontece,
    /// o log <c>Critical</c> local já é uma melhoria real sobre "sem alarme nenhum" — nunca falha
    /// silenciosamente (dotnet-standards §Resiliência).</para>
    /// </remarks>
    public interface ICanaryAlertService
    {
        /// <summary>
        /// Dispara o alarme de canary. Nunca lança — uma falha ao logar não pode derrubar o
        /// request real que está sendo servido (a resposta ao honeypot/credencial continua
        /// plausível independente do resultado deste método).
        /// </summary>
        /// <param name="canaryType"><see cref="CanaryConstants.EndpointCanaryType"/> ou
        /// <see cref="CanaryConstants.CredentialCanaryType"/>.</param>
        /// <param name="httpContext">Contexto da requisição atual, para extrair IP de origem e path.</param>
        void Raise(string canaryType, HttpContext httpContext);
    }

    /// <inheritdoc cref="ICanaryAlertService"/>
    public sealed class CanaryAlertService : ICanaryAlertService
    {
        private readonly ILogger<CanaryAlertService> _logger;

        public CanaryAlertService(ILogger<CanaryAlertService> logger)
        {
            _logger = logger;
        }

        public void Raise(string canaryType, HttpContext httpContext)
        {
            try
            {
                var sourceIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "desconhecido";
                var correlationId = CorrelationContext.CurrentId ?? "sem-correlation-id";
                var path = httpContext.Request.Path.Value ?? string.Empty;
                var method = httpContext.Request.Method;

                // Marcador CANARY_TRIGGERED: grep/alerta trivial, distinto de qualquer log
                // operacional. CorrelationId reaproveitado (não um novo mecanismo de correlação)
                // para permitir cruzar com outros logs da mesma origem/sessão.
                _logger.LogCritical(
                    "CANARY_TRIGGERED {CanaryType} origem={SourceIp} correlationId={CorrelationId} metodo={Method} rota={Path}",
                    canaryType, sourceIp, correlationId, method, path);
            }
            catch
            {
                // Nunca deixa o alarme derrubar o request real — pior caso é perder ESTE alerta
                // específico, não a resposta ao cliente (nem denunciar a detecção via erro 500).
            }
        }
    }
}
