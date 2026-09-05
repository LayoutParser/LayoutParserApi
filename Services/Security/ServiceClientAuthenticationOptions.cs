namespace LayoutParserApi.Services.Security
{
    /// <summary>
    /// Configuração pública (não-segredo) do esquema M2M "ServiceClient" — Parte 1 do ADR
    /// (<c>docs/architecture/adr-autenticacao-m2m-e2e-cypress-2026-09-03.md</c>).
    /// </summary>
    /// <remarks>
    /// <para>Este esquema é <b>paralelo</b> ao <see cref="TrustedIdentityMiddleware"/>/
    /// <see cref="TrustedHeaderAuthenticationHandler"/> — não o substitui nem estende. O
    /// <c>TrustedIdentityMiddleware</c> confia por <b>rede</b> (loopback = BFF); este esquema
    /// confia por <b>criptografia</b> (assinatura do token JWT verificada contra o tenant Entra).
    /// São dois modelos de confiança estruturalmente diferentes — ver seção "Como o middleware
    /// valida o token" do ADR.</para>
    ///
    /// <para><see cref="Authority"/> e <see cref="Audience"/> são metadados públicos de
    /// configuração OIDC (não segredo) — podem viver em <c>appsettings.json</c>, como o
    /// <c>Security:TrustedUserHeader</c> já faz. O <c>client_secret</c> do App Registration "de
    /// serviço" (usado pelo Cypress para logar) <b>nunca</b> passa pela API — é trocado por um
    /// token diretamente com o Entra, do lado do consumidor (Cypress/CI).</para>
    /// </remarks>
    public class ServiceClientAuthenticationOptions
    {
        /// <summary>Seção de configuração (env var: <c>Authentication__ServiceClient__...</c>).</summary>
        public const string SectionName = "Authentication:ServiceClient";

        /// <summary>
        /// Authority do tenant Entra (ex.: <c>https://login.microsoftonline.com/&lt;tenant-id&gt;/v2.0</c>).
        /// Vazio até o App Registration "de serviço"/"resource" ser criado no Entra da NDD
        /// (pré-requisito externo, fora do alcance de qualquer agente — ver ADR, "Plano de
        /// implementação", passo 1).
        /// </summary>
        public string Authority { get; set; } = string.Empty;

        /// <summary>
        /// Audience/Application ID URI do App Registration da própria API (ex.:
        /// <c>api://layoutparser-api</c>). Vazio até ser provisionado.
        /// </summary>
        public string Audience { get; set; } = string.Empty;

        /// <summary>
        /// O esquema só é registrado se ambos os valores estiverem preenchidos — enquanto o App
        /// Registration não existir, o esquema M2M fica ausente (degrada: endpoints
        /// <c>[Authorize]</c> continuam funcionando via <c>TrustedHeader</c>, só não aceitam
        /// Bearer token ainda). Nunca lança na ausência de config.
        /// </summary>
        public bool IsConfigured => !string.IsNullOrWhiteSpace(Authority) && !string.IsNullOrWhiteSpace(Audience);
    }
}
