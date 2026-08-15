# Diagnóstico — 403 em `execute-candidates` + `LowCodeRunner` ausente (2026-08-14)

> `@lp-architect` (Aria). Missão `analyze-impact`/`review-arch`, correlação com o que foi mergeado
> hoje (`develop` → PR #91, incluindo PR #89 "auditoria de gates" e Issue #32 "enforcement por
> papel"). **Não implementa** — entrega causa raiz confirmada por evidência de arquivo:linha e
> opções de correção para o dono decidir.

## Resumo executivo

Os dois sintomas são **causas distintas, não relacionadas entre si**:

| Sintoma | Causa raiz confirmada | Introduzida hoje? |
|---|---|---|
| `403 Forbidden` em `execute-candidates` | `[Authorize(Roles = "admin")]` (Issue #32) rejeitando o usuário — falta do papel `admin` propagado pelo BFF via `x-iis-roles`, **ou** a origem da requisição não passa no guard de loopback do `TrustedIdentityMiddleware` | Sim — mergeado há poucas horas (PR #43/#91), é o comportamento **pretendido**, não um bug |
| `LowCodeRunner` ausente no servidor | Pendência **antiga e já documentada** (`LowCode:RunnerPath` inerte / binário não publicado) — hoje **mitigada, não resolvida**, por um step de deploy que copia o `.exe` para a Bin do Sysmiddle **se** encontrar uma "Bin apta" no host | Não — o mecanismo de publicação existe desde antes; pode falhar silenciosamente (`continue-on-error: true`) se nenhuma Bin apta for encontrada |

Hipótese 1 do dono (trava de rede/HTTPS reabrindo acesso direto) está **refutada** pela config.
Hipótese 3 (PR #89 quebrou a ordem de middleware) está **refutada** — a ordem está correta.
Hipótese 4 (as duas causas são a mesma) está **refutada** — 403 é decisão de `[Authorize]`, ausência
de runner produziria outro sintoma (warning no payload 200, nunca 403).

---

## Sintoma 1 — 403 em `execute-candidates`

### Causa raiz confirmada: `[Authorize(Roles = "admin")]`

`Controllers/TransformationExecutionController.cs:161-165`:

```csharp
// Issue #32: dispara processos externos (runner x86) e é operação privilegiada —
// restrita ao papel "admin".
[Authorize(Roles = "admin")]
[HttpPost("execute-candidates")]
public async Task<IActionResult> ExecuteTransformationCandidates(...)
```

Mecanismo completo, ponta a ponta:

1. **Identidade vem de headers, não de login na API.** `Services/Security/TrustedIdentityMiddleware.cs`
   lê `x-iis-user` / `x-iis-roles` (CSV) e monta um `ClaimsPrincipal` — **mas só se a origem da
   requisição for loopback** (`TrustIdentityFromLoopbackOnly=true`, default fora do
   `appsettings.json`). Fora de loopback ou sem header → identidade **anônima**, nunca exceção.
2. **`TrustedHeaderAuthenticationHandler`** (`Services/Security/TrustedHeaderAuthenticationHandler.cs`)
   existe só para dar ao ASP.NET um `AuthenticationScheme` — ele não autentica nada por conta
   própria, apenas embrulha o `ClaimsPrincipal` que o middleware acima já populou.
3. **Pipeline** (`Program.cs:634-678`): `TrustedIdentityMiddleware` roda **antes** de
   `UseAuthentication`/`UseAuthorization` — ordem correta, confirmada por leitura direta. O
   comentário na linha 674 registra exatamente essa dependência.
4. **`Authorize(Roles="admin")`** então checa se `x-iis-roles` continha `admin`. Resultado:
   - sem identidade (fora de loopback/sem header) → 401/anônimo tratado como Forbid pelo pipeline
     de auth padrão quando não há challenge configurado → **403**;
   - identidade presente mas papel ≠ `admin` (ex.: `operador`) → **403** (`Forbid`, não `401`) —
     comportamento coberto por teste (`tests/.../Security/RoleAuthorizationTests.cs:45-55`,
     `Usuario_autenticado_sem_o_papel_exigido_e_negado`).

Ambos os caminhos batem exatamente com o sintoma relatado: `403`, não `401`/`500`/`504`.

### Hipóteses do dono checadas contra o código

- **Hipótese 1 (trava de rede/HTTPS reabriu acesso direto)** — **refutada**. `appsettings.json:145-154`
  já vincula os dois endpoints Kestrel a loopback:
  ```json
  "Kestrel": { "Endpoints": {
    "Http":  { "Url": "http://127.0.0.1:5000" },
    "Https": { "Url": "https://127.0.0.1:5001" }
  }}
  ```
  O PR #54 (`feat(seguranca): habilita endpoint HTTPS no Kestrel com certificado auto-assinado`)
  **não** rebindou para `0.0.0.0` — o endpoint HTTPS novo também está em `127.0.0.1:5001`. O
  `deploy.yml:709` reforça o mesmo bind via env var (`Kestrel__Endpoints__Http__Url=http://127.0.0.1:5000`).
  Logo, o front batendo em `https://172.25.32.42/...` (porta 443 implícita) **não pode estar
  chegando direto no Kestrel** — algo na frente (IIS/reverse proxy fazendo TLS termination,
  possivelmente o próprio BFF Fastify exposto via IIS ARR) está recebendo em 443 e repassando.
  Isso é **consistente com a topologia correta** (browser → porta pública → BFF → loopback :5000),
  não com um furo de rede. Não dá para confirmar 100% sem acesso ao host (sem SSH/WinRM/SMB, nota
  já registrada em `deploy.yml:455-459`), mas nada no código aponta para exposição direta do Kestrel.

- **Hipótese 3 (PR #89 quebrou `UseAuthentication`/`UseAuthorization`)** — **refutada**. A ordem em
  `Program.cs:600-680` está correta: cabeçalhos de segurança → `UseCors` → CorrelationId →
  `TrustedIdentityMiddleware` → (dev: Swagger / prod: exception handler) → `UseAuthentication` →
  `UseAuthorization` → `MapControllers`. `git log` mostra que o commit de auditoria (`0250441`,
  `a0e67dc`) não tocou nessa região — são arquivos de doc/memória, não `Program.cs`.

### Causa mais provável (ordem de probabilidade)

1. **O usuário logado no front não carrega o papel `admin`** no token/sessão Entra, e o BFF não está
   mapeando esse papel para `x-iis-roles: admin`. Este é o candidato mais provável — é exatamente o
   tipo de lacuna que aparece no primeiro uso real de um enforcement novo (Issue #32 mergeou há
   poucas horas). **Fora do escopo deste repo** — o mapeamento de roles Entra → `x-iis-roles` vive no
   BFF (`LayoutParserReact/server/`), não auditado nesta sessão.
2. **A requisição não está passando pelo guard de loopback** — se o BFF e a API não estiverem
   realmente co-hospedados no mesmo host (ou se algum proxy intermediário mudar o IP de origem
   percebido pela API), `TrustedIdentityMiddleware` descarta os headers e a identidade vira anônima
   → 403. Menos provável que (1) dado que o pré-requisito de co-hospedagem já foi confirmado pelo
   dono (`rollout-p2-autenticacao.md:706-707`), mas não impossível se algo mudou na topologia.

Não dá para diferenciar (1) de (2) sem log do lado da API no momento do 403 (`ICurrentUser.Name`/
`Roles` fica em claro no pipeline — nunca logar o header cru, mas o nome do usuário resolvido e a
lista de papéis são seguros de logar e já é o dado que falta). Recomendo pedir esse log antes de
qualquer correção.

---

## Sintoma 2 — `LowCodeRunner` ausente no servidor

### Causa raiz: pendência antiga, **parcialmente mitigada** por um step novo

Confirmado como a **mesma pendência já documentada** em memória de projeto
(`lp-architect/lowcode-nunca-rodou-em-producao.md`: "`LowCode:RunnerPath` vazio no servidor... runner
`.exe` não é copiado por nenhum workflow"). **Não é a mesma causa do 403** — motivo:

- Quando o runner está ausente, o pathway sysmiddle degrada como **warning no payload 200**, nunca
  como 403. Ver `Controllers/TransformationExecutionController.cs:342-349`:
  ```csharp
  catch (Exception ex)
  {
      _logger.LogWarning(ex, "Falha estrutural no pathway sysmiddle ao gerar candidatos ...");
      warnings.Add($"Pathway sysmiddle falhou: {LowCodeErrorSanitizer.ForWire(ex)}");
  }
  ```
  A resposta segue com `success: true` e os candidatos do pathway `tcl-xsl`. A ausência do runner é
  **isolada por design** (comentário na linha 279-286) — nunca deveria produzir `403`/`500`/`502`.
- **O que mudou hoje**: `.github/workflows/deploy.yml:383-441`, step `Publicar runner low-code na Bin
  do Sysmiddle`. Ele **procura** uma "Bin apta" do Sysmiddle no host (`SysMiddle.Base.dll` +
  `log4net` 2.x lado a lado) e copia `LayoutParserLowCodeRunner.exe` para lá — não mais para
  `<deploy>\api`, que nunca funcionaria (o runner é `net481`/x86 e resolve dependências pelo próprio
  diretório). É `continue-on-error: true` e **não falha o deploy** se nenhuma Bin apta for
  encontrada — nesse caso o pathway low-code segue indisponível, silenciosamente, exatamente como
  antes.

### Por que o dono ainda vê o runner ausente

Duas possibilidades, não custa checar ambas no próximo deploy:

1. **O run mais recente não passou por esse step com sucesso** — checar o log do job `Publicar
   runner low-code na Bin do Sysmiddle` no Actions: se ele reportou "Nenhuma Bin do Sysmiddle APTA
   neste host", o runner segue ausente por design (`exit 0`, sem falha).
2. **O runner foi publicado numa Bin do Sysmiddle** (ex.: `C:\appconnector\App\Bin`), não na pasta
   `<deploy>\api` — se o dono está checando `<deploy>\api` esperando encontrá-lo lá, ele não vai
   aparecer ali de propósito (comentário `deploy.yml:364-368` explica por quê).

---

## Opções de correção (decisão do dono)

### Para o Sintoma 1 (403)

| Opção | Trade-off |
|---|---|
| **A. Conceder o papel `admin` ao(s) usuário(s) certo(s)** via Entra (grupo/role assignment) + confirmar que o BFF mapeia esse claim para `x-iis-roles: admin` | Não muda código da API; resolve a causa mais provável. Exige acesso ao BFF/Entra (fora deste repo) para confirmar o mapeamento existe e está correto |
| **B. Rebaixar a exigência de papel** desse endpoint específico (ex.: `operador` em vez de `admin`, ou remover `[Authorize]`) | É decisão de produto, não técnica — o endpoint dispara processo externo (`.exe`) e foi classificado como `admin` deliberadamente na tabela de risco (`rollout-p2-autenticacao.md:216-222`). Rebaixar aumenta a superfície de quem pode disparar runners x86 |
| **C. Instrumentar log temporário** de `ICurrentUser.Name`/`Roles` no momento da 403 (nunca o header cru) | Não corrige nada sozinho, mas resolve a ambiguidade entre "sem papel" vs. "identidade anônima" antes de escolher A ou B. Baixo custo, recomendo como primeiro passo |

### Para o Sintoma 2 (runner ausente)

| Opção | Trade-off |
|---|---|
| **A. Checar o log do step de publicação no último deploy** | Zero custo, resolve a ambiguidade "não rodou" vs. "rodou e não achou Bin apta" |
| **B. Se nenhuma Bin apta for encontrada**: provisionar manualmente uma Bin do Sysmiddle completa no host (fora do escopo de CI — depende de quem opera o FiatMQ/AppConnector, nota já registrada no próprio workflow) | Trabalho operacional, não muda o repo |
| **C. Fazer o step falhar o deploy** (remover `continue-on-error: true`) se nenhuma Bin apta for encontrada | Torna o gap visível a cada deploy em vez de silencioso, mas transforma uma dependência de infra de terceiros (AppConnector) em bloqueador de release da API — trade-off de acoplar release da API à disponibilidade de um produto externo |

---

## Não fiz (fora do escopo desta missão)

- Não testei localmente subindo a API com headers simulados — a evidência de código + o teste
  existente (`RoleAuthorizationTests.cs`) já cobrem exatamente esse comportamento de forma
  determinística; reproduzir localmente não mudaria a conclusão sem acesso aos logs reais de
  produção do momento do 403 (dado que falta é *qual* papel o BFF está de fato enviando).
- Não investiguei o BFF (`LayoutParserReact/server/`) — fora deste repositório; é onde vive o
  mapeamento de roles Entra → `x-iis-roles` que decide a Hipótese mais provável do Sintoma 1.
