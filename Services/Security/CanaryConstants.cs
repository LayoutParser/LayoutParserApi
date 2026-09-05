namespace LayoutParserApi.Services.Security
{
    /// <summary>
    /// Valores da camada de DETECÇÃO (honeypot/canary) do ADR M2M — Parte 2
    /// (<c>docs/architecture/adr-autenticacao-m2m-e2e-cypress-2026-09-03.md</c>, seção
    /// "Honeypots / Canary Tokens").
    /// </summary>
    /// <remarks>
    /// <para>🔴 <b>ISTO É DETECÇÃO, NÃO PREVENÇÃO.</b> Nenhum valor aqui autentica ou autoriza
    /// nada — não substitui <see cref="TrustedIdentityMiddleware"/> nem o esquema
    /// <c>ServiceClient</c> (Parte 1 do ADR). O único papel destes valores é servir de isca: se
    /// alguém os usar, é porque não deveria estar testando esta API (enumeração de rotas, replay
    /// de segredo antigo vazado no histórico do git — ver <c>.claude/rules/security.md</c>).</para>
    ///
    /// <para>A credencial abaixo é deliberadamente "descoberta" fora deste arquivo também — ela
    /// aparece documentada em <c>appsettings.Legacy.json.example</c> (na raiz do repo), simulando
    /// um arquivo de config de ambiente descontinuado que um atacante que já tenha acesso ao
    /// histórico do repositório poderia plausivelmente encontrar. Não é um valor secreto de
    /// verdade — reconhecê-lo NUNCA concede acesso, só dispara o alarme
    /// (<see cref="ICanaryAlertService"/>).</para>
    /// </remarks>
    public static class CanaryConstants
    {
        /// <summary>
        /// Header da credencial-isca. Nome reaproveitado da Opção B do ADR M2M (descartada como
        /// mecanismo de autenticação real) — nenhum consumidor legítimo (React, MCP, Cypress real)
        /// jamais deveria enviar este header, porque esse mecanismo nunca foi implementado como
        /// via de autenticação de verdade (a Parte 1 do ADR escolheu client credentials via Entra,
        /// com <c>Authorization: Bearer</c>, não headers próprios).
        /// </summary>
        public const string LegacyCredentialHeader = "X-Service-Credential";

        /// <summary>
        /// Valor "aposentado" da credencial-isca — plausível o suficiente para parecer uma chave
        /// de API legada, mas nunca gerado por nenhum sistema real. Reconhecê-lo dispara o alarme;
        /// nunca autentica nada.
        /// </summary>
        public const string LegacyCredentialValue = "svc_e2e_legacy_2026-06-30_9f3a2c81d4e07b6c";

        /// <summary>Marcador do tipo de isca acionada, para filtrar/alarmar sobre o log <c>Critical</c>.</summary>
        public const string CredentialCanaryType = "credential";

        /// <summary>Marcador do tipo de isca acionada pelo endpoint-isca.</summary>
        public const string EndpointCanaryType = "endpoint";
    }
}
