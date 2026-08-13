# TLS na API com certificado auto-assinado

Status: implementado (2026-08-12) · Issue #34 · dono do projeto pediu TLS de verdade,
mesmo com a mitigação de rede já existente.

## Por que TLS, se o loopback já mitiga o risco

O risco original da issue #34 era headers de identidade (`x-iis-user`/`x-iis-roles`,
ver `Services/Security/TrustedIdentityMiddleware.cs`) trafegando em HTTP puro entre o
BFF (`LayoutParserReact/server/`) e a API. Hoje esse risco **já está mitigado** pela
trava de rede: a API só deve escutar `127.0.0.1` (loopback), então o tráfego nunca sai
da própria máquina — não há rede física/switch capaz de farejar o pacote.

Mesmo assim, o dono do projeto decidiu manter TLS como **camada adicional de defesa em
profundidade**, não como substituto da trava de loopback:

- Cobre o caso de a API vir a escutar em `0.0.0.0` no futuro (erro de config, mudança de
  topologia) — se isso acontecer, o tráfego passa a estar cifrado mesmo assim.
- Elimina qualquer dúvida de auditoria/compliance sobre "API sobe em HTTP puro".
- Custo de implementação é baixo (config-only, sem código novo) e o risco de regressão é
  baixo (endpoint HTTP continua ativo em paralelo).

**A trava de loopback continua sendo a defesa primária.** TLS não abre a API para a
internet nem é usado como argumento para relaxar `TrustIdentityFromLoopbackOnly`.

## O que foi configurado

`appsettings.json` — Kestrel já era 100% orientado por configuração (nenhum
`UseKestrel()`/`ConfigureKestrel()` manual em `Program.cs`), então a mudança é
config-only:

```json
"Kestrel": {
  "Endpoints": {
    "Http": {
      "Url": "http://127.0.0.1:5000"
    },
    "Https": {
      "Url": "https://127.0.0.1:5001"
    }
  }
}
```

- O endpoint **HTTP continua ativo** (porta 5000) — não quebra o fluxo atual do BFF nem
  exige migração imediata. A migração do BFF para HTTPS (porta 5001) fica registrada
  como próximo passo, não foi feita nesta issue (fora do escopo: mexeria em
  `LayoutParserReact/server/`, outro repositório).
- `UseHttpsRedirection()` permanece **comentado** em `Program.cs` (linha ~649) — decisão
  preexistente registrada no próprio código ("Only use HTTPS redirection if actually
  using HTTPS"). Manter os dois endpoints ativos em paralelo é o modo menos arriscado de
  introduzir TLS sem forçar redirecionamento antes de o BFF estar pronto para consumir
  HTTPS.
- **Nenhum certificado foi comitado no repo.** `*.pfx` já está no `.gitignore`.

## Certificado — geração e uso

Não há bloco `Kestrel:Certificates:Default` no `appsettings.json`. Isso é proposital:
quando esse bloco está ausente, o Kestrel cai automaticamente no **certificado de
desenvolvimento do ASP.NET Core** (`dotnet dev-certs https`), que já é um certificado
auto-assinado padrão do próprio SDK — não precisamos gerar/gerenciar um novo formato.

### Dev / máquina local

```bash
dotnet dev-certs https --trust
```

Gera (se ainda não existir) e confia no certificado auto-assinado padrão do SDK. O
Kestrel encontra automaticamente porque nenhuma configuração explícita de certificado
foi feita — é o comportamento default do template ASP.NET Core.

### Deploy (serviço Windows nativo, sem perfil de usuário interativo)

O serviço Windows roda como `LocalSystem` (ver `runner-dev-gh-actions` /
`Services Windows nativo` na memória do projeto), que não tem o mesmo cert store de
usuário usado pelo `dotnet dev-certs`. Para esse cenário, gere um `.pfx` auto-assinado
com `openssl` (mais previsível para importar no cert store da máquina/serviço) e aponte
o Kestrel para ele via variáveis de ambiente — **nunca** via `appsettings.json`
(mesmo padrão de segredo documentado em `.claude/rules/security.md`):

```bash
openssl req -x509 -newkey rsa:2048 -keyout layoutparserapi-key.pem \
  -out layoutparserapi-cert.pem -days 825 -nodes \
  -subj "/CN=layoutparserapi.local"
openssl pkcs12 -export -out layoutparserapi.pfx \
  -inkey layoutparserapi-key.pem -in layoutparserapi-cert.pem
```

No ambiente de deploy (registro do serviço Windows, mesmo mecanismo de
`HKLM\SYSTEM\...\Services\LayoutParserApi\Environment` já usado para `Database__Password`):

```
Kestrel__Certificates__Default__Path=C:\caminho\seguro\layoutparserapi.pfx
Kestrel__Certificates__Default__Password=<senha do pfx>
```

Com essas duas env vars presentes, o Kestrel passa a servir HTTPS com esse certificado
em vez do dev cert — sem qualquer mudança de código, porque a configuração do Kestrel é
hierárquica (`appsettings.json` → env vars) igual ao resto da app.

### Renovação

- **Dev cert (`dotnet dev-certs`):** válido por ~13 meses; se expirar, `dotnet dev-certs
  https --clean && dotnet dev-certs https --trust` gera um novo. Não requer ação em
  produção — é só para a máquina de desenvolvimento.
- **`.pfx` de deploy (`openssl`):** gerado com `-days 825` (~2 anos, teto recomendado
  para certificados TLS). Antes de expirar, repetir os dois comandos `openssl` acima,
  reimportar o `.pfx` novo e atualizar a env var `Kestrel__Certificates__Default__Path`
  se o caminho mudar. Não há automação de renovação nesta primeira versão — uso é
  interno/dev, sem CA pública, então não há ACME/Let's Encrypt aplicável.

## O que este trabalho NÃO faz

- Não migra o BFF (`LayoutParserReact/server/`) para consumir a API via HTTPS — o
  endpoint HTTP continua sendo o caminho ativo até essa migração ser decidida/agendada.
- Não reintroduz `UseHttpsRedirection()` — forçar redirecionamento antes do BFF estar
  pronto quebraria a comunicação atual.
- Não substitui a trava `TrustIdentityFromLoopbackOnly` nem a decisão de a API escutar
  só em `127.0.0.1` — essas continuam sendo a defesa primária contra forja de
  identidade; TLS é redundância, não substituição.
