# Plano de segurança e arquitetura — LayoutParser API (2026-08-10)

> `@lp-architect` (Aria). Base: auditoria em 4 lentes com refutação adversarial + verificação
> própria contra a instância viva. Priorizado por **risco real**, não por gosto. Cada item tem
> arquivo:linha e, onde deu, prova empírica.

---

## P0 — CRÍTICO: leitura de arquivo arbitrário, sem autenticação (provado)

**Onde:** `Controllers/DocumentController.cs:123` (`GetLayout`), `:157` (`GetDocument`), `:174`
(`GetExcelFile`). Padrão: `Path.Combine(_documentsPath, "Layout", fileName)` com `fileName` cru do
cliente.

**Prova (instância local `:5100`, hoje):**

```
GET /api/document/layout/C:%5CWindows%5Cwin.ini  →  200
{"success":true,"fileName":"C:\\Windows\\win.ini","content":"; for 16-bit app support..."}
```

**Por que funciona:** `Path.Combine(base, x)` **descarta `base`** quando `x` é um caminho enraizado
(`C:\...`). É comportamento documentado do .NET, e é a armadilha exata aqui. `../` foi barrado por
normalização de URL, mas o caminho absoluto passa direto.

**Impacto:** qualquer um na rede lê qualquer arquivo que a conta do serviço enxergue —
`appsettings.json` (que já teve segredos), configs, chaves, arquivos de outros clientes. A API **não
tem autenticação** (`Program.cs:608`, `UseAuthorization` comentado), então não há segunda barreira.
É o pior tipo de falha: **crítica, trivial de explorar, e em produção agora**.

**Correção (baixo esforço, alto impacto):**
1. Validar `fileName` por **lista branca de caractere** (`^[A-Za-z0-9._-]+$`) e rejeitar qualquer
   coisa com separador de caminho, `:` ou `..` — **validar, não sanear**.
2. Após `Path.Combine`, `Path.GetFullPath` e conferir que o resultado **começa** com o diretório base
   canonicalizado. Padrão de referência: a validação por regex dos **endpoints de ticket de
   transformação** (adicionados na leva anterior). ⚠️ **CORREÇÃO (Aria, após implementação):** eu
   escrevi que o `ParseController` inteiro seguia esse padrão — **errado**. Os endpoints de ticket
   têm; mas `SaveFileForLearningAsync` (`ParseController:483`) **não tinha nada** e era ele próprio um
   vetor.
3. Auditar **todos** os endpoints que recebem nome de arquivo: `MetricsController:261,319` (TCL/XSL)
   têm a mesma forma.

> **VETOR DE ESCRITA — achado do `@lp-backend-dev` durante a implementação, NÃO previsto neste plano.**
> `SaveFileForLearningAsync` (`ParseController:483`, chamado no upload) fazia
> `Path.Combine(basePath, layoutName)` e `Path.Combine(dir, fileName)` com **os dois vindos do
> cliente** — `layoutName` é `[FromForm]` cru (`:58`), `fileName` é o nome do upload. **Escrita** de
> arquivo arbitrário, estritamente pior que a leitura do P0: permite plantar arquivo fora da base.
> Confirmado por mim no diff contra `develop`. Corrigido sob o mandato "auditar todos, não deixar
> vetor gêmeo": `layoutName` pela lista branca (recusa → pula o aprendizado, não derruba o parse),
> nome do upload por `Path.GetFileName` + `IsInsideBase`. Lição: o plano apontou o bom exemplo e
> presumiu o resto do controller seguro — a auditoria ampla é que pegou o contra-exemplo no mesmo
> arquivo.

Isto sobe **antes** de qualquer melhoria de arquitetura. É o único item que eu classificaria como
"parar e corrigir agora".

---

## P1 — Falhas silenciosas: o sistema mente que está saudável

Três achados confirmados (sobreviveram à refutação) que compartilham uma raiz: **operação que falhou
reporta sucesso.** É o mesmo mecanismo que deixou o LowCode rodar sem runner por semanas sem ninguém
ver.

### 1. Descriptografia falha devolvendo o texto cifrado como se fosse válido

**Onde:** `Services/Database/DecryptionService.cs:73` — quando o `.exe` de descriptografia não existe,
o método **retorna a entrada cifrada** em vez de falhar.

**Impacto:** se o decryptor sumir do deploy ou o caminho estiver errado no appsettings do destino (que
o deploy preserva), o catálogo de layouts volta vazio/parcial **com resposta 200**. O operador conclui
"não há layouts". Pior: `test-decryption`/`test-decryption-raw` — o endpoint que se usaria para
diagnosticar isso — responde `success=true` com a cifra ecoada, exatamente no cenário em que a
descriptografia **não aconteceu**.

**Correção:** falhar explícito (resultado tipado `Success/Error` ou exceção — o catch em
`LayoutDatabaseService.cs:155` já trata). Separar no log "ignorado por não ser TextPositional" de
"falha de descriptografia". Warm-up com taxa de falha > 0 → ERROR, não INFO com ✅.

### 2. Timeout de 30s do decryptor é inalcançável (deadlock de thread)

**Onde:** `Services/Database/DecryptionService.cs:149` — `ReadToEnd()` **síncrono** antes do
`WaitForExit(30000)`.

**Impacto:** um decryptor travado ou verboso prende a thread **para sempre** — o `ReadToEnd` bloqueia
antes de o timeout ser sequer avaliado. A chamada roda dentro do loop do `SqlDataReader`, então prende
a conexão SQL junto. Repetido (warm-up, refresh de catálogo), esgota o pool e a API para de responder
— **enquanto o serviço segue "Running" e o health devolve 200**. Incidente sem um único log de erro.

**Correção:** ler `stdout`/`stderr` com `ReadToEndAsync` e correr `Task.WhenAll` contra
`Task.Delay(timeout)`, matando o processo se o delay vencer — **o padrão que o Dex já implementou** em
`LowCodeTransformationService.ExecuteRunnerProcessAsync`. Tornar `DecryptContentAsync`.

### 3. Nenhum health check testa dependência alguma

**Onde:** `Controllers/TestController.cs:92` — o único health do projeto retorna `200 {status:"API
está funcionando"}` sem tocar em SQL, Redis, decryptor ou runner. Sem `AddHealthChecks` em
`Program.cs`.

**Impacto:** com SQL/Redis/decryptor/runner fora, o processo sobe, o SCM reporta Running, o smoke test
do CI fica verde e o health devolve 200 — o defeito só aparece quando um usuário sobe um documento e
recebe catálogo vazio. **O deploy pode publicar uma versão inoperante e declarar sucesso.**

**Correção:** `AddHealthChecks` com sondas — SQL (`SELECT 1` com timeout curto), Redis (PING,
ausência = Degraded, não Unhealthy), existência do decryptor e do `RunnerPath`, e a contagem do
warm-up (catálogo vazio = readiness fail). Expor `/health` (liveness) e `/health/ready` (readiness,
503 quando dependência essencial fora). Apontar o smoke test do CI para `/health/ready` estrito.

---

## P2 — Autenticação: a rede é a única fronteira

`UseAuthorization` está comentado (`Program.cs:608`). **Toda** a API é anônima. O Gage já preparou o
terreno — há um `Security__ApiKey` provisionável e o mecanismo "nasce desligado sem a chave"
(`Program.cs:163`). O P0 acima é explorável **porque** não há esta segunda barreira.

**Recomendação:** provisionar `Security__ApiKey`, listar em `Security__AnonymousPaths` só o que
precisa ser público (health), e ligar. Não substitui o P0 — defense-in-depth, os dois. Decisão de
produto sobre onde a chave vive e quem a distribui (front, MCP, integrações).

---

## P3 — Dívida estrutural (não sangra, mas custa)

Estes vieram das lentes de acoplamento/dados mas **não passaram por verificação** (os agentes de
refutação caíram no limite de sessão). Trato como **hipótese a confirmar**, não achado — registro para
não perder, com o rótulo honesto:

- **[NÃO VERIFICADO]** Documento fiscal de cliente (CNPJ, e-mail, endereço, valores) gravado em disco
  em claro, sem retenção definida, no pipeline de aprendizado (`LowCodeAutoTransformationService`,
  `ML:*Path`). Precisa confirmar o que exatamente persiste e por quanto tempo. Se confirmado, é LGPD.
- **[NÃO VERIFICADO]** `Program.cs` com ~890 linhas de registro de DI; suspeita de serviços mortos no
  container e serviços usados fora dele. Precisa do diff DI-vs-uso.
- **[NÃO VERIFICADO]** Dois pathways de transformação paralelos (`TransformationController` vs
  `TransformationExecutionController`) — já documentado em memória como dívida; candidato a deprecação
  do Pathway 1.

**Ação:** re-rodar a auditoria destas lentes após o reset de sessão (19h10) antes de agir. Não
implementar nada de P3 com base em achado não refutado — foi assim que erros anteriores foram pegos.

---

## Ordem recomendada

1. **P0** — path traversal. Hoje. Bloqueia tudo.
2. **P1.2 e P1.1** — deadlock e echo do decryptor. Baixo esforço, alto risco operacional.
3. **P1.3** — health real + smoke test estrito. Fecha a classe "deploy inoperante declarado sucesso".
4. **P2** — ligar autenticação.
5. **P3** — re-auditar e então decidir.

**P0 a P1 são do `@lp-backend-dev`.** P2 e a config são do `@lp-devops`. P3 começa por nova auditoria
minha. Nada aqui é `git push` sem o `@lp-devops`.
