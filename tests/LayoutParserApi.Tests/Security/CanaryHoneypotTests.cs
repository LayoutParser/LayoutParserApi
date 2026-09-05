using System.Net;

using LayoutParserApi.Services.Security;
using LayoutParserApi.Tests.Infra;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace LayoutParserApi.Tests.Security
{
    /// <summary>
    /// ADR M2M (docs/architecture/adr-autenticacao-m2m-e2e-cypress-2026-09-03.md), Parte 2:
    /// honeypot/canary — camada de DETECÇÃO complementar aos controles de auth reais (Parte 1,
    /// <see cref="TrustedIdentityMiddleware"/>/esquema <c>ServiceClient</c>). Não é prevenção.
    ///
    /// <para>Mesma decisão de projeto de <c>RoleAuthorizationTests</c>/
    /// <c>ServiceClientAuthenticationTests</c>: sem <c>WebApplicationFactory</c>/TestHost neste
    /// projeto de testes — exercita <see cref="CanaryAlertService"/> e
    /// <see cref="CanaryCredentialDetectionMiddleware"/> diretamente, com um
    /// <see cref="DefaultHttpContext"/> e o <see cref="CapturingLogger{T}"/> já usado por outras
    /// suítes.</para>
    /// </summary>
    public class CanaryHoneypotTests
    {
        // --- CanaryAlertService: dispara exatamente 1 log Critical com o marcador esperado ---

        [Fact]
        public void Raise_endpoint_gera_um_unico_log_Critical_com_marcador_CANARY_TRIGGERED()
        {
            var logger = new CapturingLogger<CanaryAlertService>();
            var service = new CanaryAlertService(logger);
            var context = ConstruirContexto("203.0.113.10", "/api/TransformationExecution/execute-legacy");

            service.Raise(CanaryConstants.EndpointCanaryType, context);

            Assert.Single(logger.Messages);
            Assert.Equal(LogLevel.Critical, logger.Levels[0]);
            Assert.Contains("CANARY_TRIGGERED", logger.Messages[0]);
            Assert.Contains(CanaryConstants.EndpointCanaryType, logger.Messages[0]);
            Assert.Contains("203.0.113.10", logger.Messages[0]);
        }

        [Fact]
        public void Raise_credential_gera_log_Critical_com_o_tipo_credential()
        {
            var logger = new CapturingLogger<CanaryAlertService>();
            var service = new CanaryAlertService(logger);
            var context = ConstruirContexto("198.51.100.7", "/api/TransformationExecution/execute-lowcode");

            service.Raise(CanaryConstants.CredentialCanaryType, context);

            Assert.Single(logger.Messages);
            Assert.Equal(LogLevel.Critical, logger.Levels[0]);
            Assert.Contains(CanaryConstants.CredentialCanaryType, logger.Messages[0]);
        }

        [Fact]
        public void Raise_preserva_o_CorrelationId_da_requisicao()
        {
            var logger = new CapturingLogger<CanaryAlertService>();
            var service = new CanaryAlertService(logger);
            var context = ConstruirContexto("10.0.0.5", "/api/TransformationExecution/execute-legacy");

            LayoutParserApi.Services.Logging.CorrelationContext.CurrentId = "corr-teste-123";
            try
            {
                service.Raise(CanaryConstants.EndpointCanaryType, context);
            }
            finally
            {
                LayoutParserApi.Services.Logging.CorrelationContext.CurrentId = null;
            }

            Assert.Contains("corr-teste-123", logger.Messages[0]);
        }

        [Fact]
        public void Raise_nunca_lanca_mesmo_sem_RemoteIpAddress()
        {
            var logger = new CapturingLogger<CanaryAlertService>();
            var service = new CanaryAlertService(logger);
            var context = new DefaultHttpContext(); // RemoteIpAddress nulo por padrão

            var exception = Record.Exception(() => service.Raise(CanaryConstants.EndpointCanaryType, context));

            Assert.Null(exception);
            Assert.Single(logger.Messages);
        }

        // --- CanaryCredentialDetectionMiddleware: credencial-isca dispara o alarme, sem autenticar ---

        [Fact]
        public async Task Credencial_isca_correta_dispara_alarme_e_nao_autentica_nada()
        {
            var logger = new CapturingLogger<CanaryAlertService>();
            var canaryAlert = new CanaryAlertService(logger);
            var proximoChamado = false;
            var middleware = new CanaryCredentialDetectionMiddleware(
                next: _ => { proximoChamado = true; return Task.CompletedTask; },
                canaryAlert: canaryAlert);

            var context = new DefaultHttpContext();
            context.Request.Headers[CanaryConstants.LegacyCredentialHeader] = CanaryConstants.LegacyCredentialValue;

            await middleware.InvokeAsync(context);

            Assert.True(proximoChamado); // pipeline sempre segue — nunca bloqueia por conta própria
            Assert.Single(logger.Messages);
            Assert.Equal(LogLevel.Critical, logger.Levels[0]);
            Assert.Contains(CanaryConstants.CredentialCanaryType, logger.Messages[0]);
            Assert.False(context.User.Identity?.IsAuthenticated ?? false); // NUNCA autentica
        }

        [Fact]
        public async Task Credencial_incorreta_nao_dispara_alarme()
        {
            var logger = new CapturingLogger<CanaryAlertService>();
            var canaryAlert = new CanaryAlertService(logger);
            var middleware = new CanaryCredentialDetectionMiddleware(
                next: _ => Task.CompletedTask,
                canaryAlert: canaryAlert);

            var context = new DefaultHttpContext();
            context.Request.Headers[CanaryConstants.LegacyCredentialHeader] = "qualquer-outro-valor-invalido";

            await middleware.InvokeAsync(context);

            Assert.Empty(logger.Messages);
        }

        [Fact]
        public async Task Trafego_normal_sem_o_header_nao_dispara_falso_positivo()
        {
            var logger = new CapturingLogger<CanaryAlertService>();
            var canaryAlert = new CanaryAlertService(logger);
            var middleware = new CanaryCredentialDetectionMiddleware(
                next: _ => Task.CompletedTask,
                canaryAlert: canaryAlert);

            // Requisição comum: sem X-Service-Credential, com um Authorization Bearer normal
            // (esquema ServiceClient da Parte 1) — não deveria acionar o canary.
            var context = new DefaultHttpContext();
            context.Request.Headers["Authorization"] = "Bearer eyJhbGciOi...";

            await middleware.InvokeAsync(context);

            Assert.Empty(logger.Messages);
        }

        [Fact]
        public async Task Header_vazio_nao_dispara_falso_positivo()
        {
            var logger = new CapturingLogger<CanaryAlertService>();
            var canaryAlert = new CanaryAlertService(logger);
            var middleware = new CanaryCredentialDetectionMiddleware(
                next: _ => Task.CompletedTask,
                canaryAlert: canaryAlert);

            var context = new DefaultHttpContext();
            context.Request.Headers[CanaryConstants.LegacyCredentialHeader] = string.Empty;

            await middleware.InvokeAsync(context);

            Assert.Empty(logger.Messages);
        }

        // --- helpers ---

        private static DefaultHttpContext ConstruirContexto(string remoteIp, string path)
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
            context.Request.Path = path;
            context.Request.Method = "POST";
            return context;
        }
    }
}
