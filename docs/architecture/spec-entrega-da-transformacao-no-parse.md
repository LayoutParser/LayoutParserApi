# Spec — Entrega da transformação junto ao parse (cache, store consultável e split)

> `@lp-architect` (Aria), 2026-08-05. Origem: pedido do dono do projeto — *"falta entregar junto ao
> parse a transformação do XML, quem sabe no Redis, podemos fazer um split pra não ficar muito
> grande também a questão de consulta, ou essa consulta pode permanecer em algum banco, eu só
> queria que seja o mais rápido possível."*
>
> Esta spec é o **contrato**. Quem implementar não deve divergir dela sem me avisar.
>
> **Método:** varredura em 5 lentes independentes + refutação adversarial de cada achado (26
> levantados, 12 sobreviveram). Três afirmações da **primeira versão desta spec foram derrubadas** e
> estão corrigidas abaixo, com o erro registrado — ver §6.

---

## 1. Onde o tempo realmente vai embora

O runner Sysmiddle (`LayoutParserLowCodeRunner.exe`, processo externo x86) é a parte cara. Tudo o
mais é serialização. Dois defeitos verificados fazem esse custo se multiplicar.

### 1.1 🔴 O teto de 6s não cancela nada — o trabalho abandonado continua ocupando o runner

`LowCodeTransformationService.cs:114` adquire o semáforo assim:

```csharp
await _runnerSemaphore.WaitAsync();   // sem timeout, sem CancellationToken
```

A assinatura de `TransformAsync` (`:34-41`) não recebe token — não há como cancelar.

Agora junte com `ParseController.cs:215`: passado `SyncDeliveryTimeoutSeconds` (6s), o controller
**para de esperar**, mas a `Task` continua viva, o processo do runner continua rodando e **continua
segurando um dos 2 slots** (`MaxConcurrentRunners = 2`).

**Consequência prática:** um documento grande que estourou o teto deixa de entregar ao usuário *e*
ainda bloqueia o próximo upload. Com 4 candidatos (`MultiCandidateTopN`) disputando 2 slots, uma
rajada de uploads produz uma fila que ninguém observa e que não encolhe. É o oposto direto de *"o
mais rápido possível"* — e nenhuma quantidade de cache resolve, porque o gargalo é o slot, não a
computação repetida.

**Correção:** propagar `CancellationToken` por `TransformAsync` até o `WaitAsync(token)` e até o
`Process`; o `ParseController` passa um token que dispara no teto síncrono. Abandonar a espera tem
de **liberar o slot** e matar o processo.

> ⚠️ Isto muda a semântica documentada em `LowCodeAutoTransformationService.cs:65-68` ("o trabalho
> não se perde mesmo se o chamador parar de esperar"). É deliberado: hoje o trabalho não se perde,
> mas também não chega a lugar nenhum (§2) — enquanto sequestra o recurso escasso. Com o store
> consultável do §2, a entrega passa a existir e o cancelamento deixa de ser perda.

### 1.2 🔴 O mesmo documento roda o runner duas vezes

Fluxo real de um documento visto pelo usuário:

1. Upload → `ParseController` → `RunAsync` → **N execuções do runner** (N = candidatos).
2. O array `transformations` volta no payload — **e o front não o lê** (`ParseResponse` em
   `types/api.ts:55-90` sequer declara o campo).
3. Usuário clica em "Gerar Transformação XML" → `execute-candidates` →
   `ExecuteSysmiddleCandidatesAsync` (`TransformationExecutionController.cs:259`) → **`RunAsync` de
   novo**, do zero → **mais N execuções**.

Com `MultiCandidateTopN = 4`: **8 execuções do runner** para um documento, das quais 4 são
desperdício puro. Não há cache em lugar nenhum — `LowCodeTransformationService` (190 linhas) não tem
uma única leitura de cache; o `sha256` do input é calculado (`LowCodeAutoTransformationService.cs:193,299`)
mas serve **só como nome de arquivo**.

**Correção — é aqui que mora a velocidade.** Cache-first em `RunAsync`: antes de consultar mappers e
antes de tocar no runner, procurar por `(sha256(input), layoutGuid)`. Hit → retorna imediatamente,
sem runner, sem SQL. O clique do usuário passa a ser instantâneo.

---

## 2. O store existe, é escrito, e ninguém consegue ler

Verificado por grep: `_storePath` é tocado **apenas** em `LowCodeAutoTransformationService.cs:22,35,37,195,301`
— todas dentro da própria classe, todas escrita ou `CreateDirectory`. **Zero leituras em todo
`Services/Transformation/`.** Nenhum controller lê o store.

Por isso `transformationsStatus: 'processing'` **nunca resolve**: o resultado é gravado e fica
inalcançável por qualquer cliente HTTP. O rótulo *"(processando...)"* (`AnalysisModeTabs.tsx:112-114`)
fica preso para sempre.

### 2.1 O que **não** é o problema (correção de rumo)

A primeira versão desta spec tratou o nome do artefato — `{sha}_{HHmmss}`
(`LowCodeAutoTransformationService.cs:198,304`) — como um defeito. **Não é.**

O store é um **corpus de treino append-only** (comentários `// Persistir para aprendizado contínuo`
em `:192` e `:297`), e o sufixo `HHmmss` é justamente o que impede que reprocessar o mesmo documento
**destrua a amostra anterior**. Uma chave determinística que sobrescreve apagaria histórico de
treino. O `layoutGuid` e o `mapperGuid` já estão gravados no `meta.json` (`:210-216`, `:337-340`).

O que falta não é chave — é **leitor**. Todos os ingredientes de uma chave determinística já estão em
escopo no ponto de persistência (`layoutGuid` é parâmetro, `txtContent` é parâmetro, `ComputeSha256`
já existe). Nada precisa ser renomeado.

### 2.2 Onde guardar — e por que **não** um banco novo

O dono do projeto levantou três lugares. A resposta não é escolher um: dois já existem e têm papéis
distintos.

| Store | Papel | Decisão |
|---|---|---|
| **Disco** (`ML:LowCodeTransformationsPath`) | corpus append-only; **já é escrito hoje** | **Fonte da verdade.** Ganha um **índice** de leitura ao lado — os artefatos não mudam |
| **Redis** | cache quente, TTL curto | **Acelerador opcional.** ⚠️ Ver §4.3: hoje não há evidência de que exista no servidor |
| **SQL** | catálogo (layouts/mappers) | **Não** |

Justificando a recusa do SQL: o princípio do projeto é *"SQL é fonte da verdade; Redis é cache"* — e
vale para **catálogo**. Um artefato de execução não é catálogo: é derivável (input + mapper), tem TTL
natural e **já possui store durável**. Acrescentar SQL seria um terceiro lugar para os mesmos bytes,
com invalidação em três frentes, sem comprar durabilidade que já não exista.

### 2.3 O índice de leitura

Manter os artefatos exatamente como estão e acrescentar, ao lado:

```
{storePath}/index/{sha256}.{layoutGuid}.json
    → { baseName, dateFolder, candidates: [ {mapperGuid, mapperName, targetLayoutGuid,
                                             success, outputFile, outputLength, errorMessage} ] }
```

Sobrescrito a cada execução (aponta sempre para a mais recente). **Histórico preservado, leitura O(1).**

### 2.4 O split (o *"não ficar muito grande"*)

**Duas chaves, separando o consultado-sempre do consultado-às-vezes:**

| Chave | Conteúdo | Tamanho |
|---|---|---|
| `lowcode:xform:{sha}:{layoutGuid}` | manifesto: status + descritores, **sem XML** | centenas de bytes |
| `lowcode:xform:{sha}:{layoutGuid}:cand:{mapperGuid}` | o XML de um candidato | KB a MB |

**Entrega inline com teto.** Medido em material real (`.claude/tmp/exemplos/`): input 35 KB, XML de
saída **4,2 KB**, `TopN = 4`. O caso comum cabe com folga:

- `outputLength <= LowCode:InlineXmlMaxChars` (default **262144**) → `outputXml` vai no payload;
- acima → campo omitido, o front busca pelo endpoint de corpo.

No front é **uma** ramificação: `candidate.outputXml ?? fetchBody(ticket, mapperGuid)`.

### 2.5 Endpoints novos

```
GET /api/parse/transformations/{ticket}
    200 { status: "completed"|"processing", candidates: [ {mapperGuid, mapperName,
          targetLayoutGuid, success, outputLength, errorMessage} ] }
    400 ticket fora do formato · 404 inexistente

GET /api/parse/transformations/{ticket}/candidates/{mapperGuid}
    200 { mapperGuid, outputXml } · 400 · 404
```

`ticket` = `"{sha256}.{layoutGuid}"`.

**Segurança — não-negociável.** O ticket vem do cliente e vira nome de arquivo:

1. **Validar por regex de charset fixo, nunca sanitizar por remoção de caracteres.** Sanitizar aceita
   entrada hostil e tenta consertá-la; validar recusa. `^[a-f0-9]{64}\.[A-Za-z0-9_\-]{1,64}$`.
2. Canonicalizar (`Path.GetFullPath`) e conferir contra o prefixo de `_storePath` antes de abrir.
3. **Nunca** devolver caminho absoluto de disco no payload (ver §3.1 — hoje isso já vaza).
4. TTL do Redis: `LowCode:TransformationCacheTtlHours` (default **2**). O disco não expira.

### 2.6 O que muda no payload do parse (aditivo, nada removido)

| Campo | Mudança |
|---|---|
| `transformationsTicket` | **novo**, string. Emitido sempre que o pathway for elegível — **inclusive** em `"processing"`. É o que resolve o rótulo eterno |
| `transformations[].outputLength` | **novo**, int |
| `transformations[].outputXml` | **omitido** acima do teto (§2.4) |

`transformationsStatus` mantém os mesmos quatro valores. Front que não conhecer os campos novos
continua funcionando como hoje.

---

## 3. Achados adjacentes confirmados (menores, mas reais)

### 3.1 🟠 Caminho absoluto do servidor vaza num `200 OK`

`LowCodeTransformationService.cs:169` lança
`$"Runner nao gerou outputFile: {outputPath}. stdout={stdout}"`. Essa mensagem é capturada como
`ErrorMessage` do candidato (`LowCodeAutoTransformationService.cs:286`) e sai **no payload de sucesso
do parse**. Vaza estrutura de diretórios do servidor para o cliente. Sanear antes de serializar —
detalhe fica no log, não no wire.

### 3.2 🟠 O timeout do conjunto escala ao contrário e joga fora trabalho pronto

`TransformationExecutionController.cs:200`:

```csharp
var overallTimeoutSeconds = Math.Max(1, _lowCodeOpt.RunnerTimeoutSeconds)
                          * Math.Max(1, _lowCodeOpt.MaxConcurrentRunners);
```

Aumentar `MaxConcurrentRunners` (mais paralelismo ⇒ deveria terminar **antes**) **aumenta** o
timeout. A fórmula tem o fator invertido. E no 504 (`:204-208`) o retorno acontece **antes** de
coletar (`:211-212`): candidatos que já terminaram são descartados. Devolver o que existe com
`warnings` é estritamente melhor que 504 vazio.

### 3.3 🟡 "Candidato" tem dois formatos incompatíveis

O parse emite `LowCodeCandidateResult` (`mapperGuid`/`outputXml`/…) e o `execute-candidates` emite
`TransformationCandidate` (`candidateId`/`pathway`/`transformedXml`/…). Mesmo conceito, campos
disjuntos — o front precisaria de dois parsers para a mesma coisa. **O manifesto do §2.5 deve adotar
o vocabulário do `execute-candidates`** (é o que o front já tipa), não inventar um terceiro.

### 3.4 🟡 Drift entre ADR e código na proveniência do dataset

A ADR especifica `positionalFormatSource = "LayoutMetadata"` e exige `provenanceContract`; o código
grava `"layout"` (`LowCodePositionalMetadata.cs:43`) e **não grava** `provenanceContract`
(`:77-83`). Não bloqueia esta leva — **é tarefa do `@lp-parser-llm`**, registrada aqui para não se
perder.

---

## 4. Bloqueios de infraestrutura — `@lp-devops`, não `@lp-backend-dev`

Três achados confirmados que **invalidam suposições** minhas e de qualquer plano que dependa de
configuração nova.

### 4.1 🔴 Config nova no `appsettings.json` do repositório **nunca chega ao servidor**

`ci-dev.yml:236-250` (e `deploy.yml:394`, mesma política): se o destino já tem `appsettings.json`, o
deploy copia tudo **menos** ele.

```powershell
Copy-Item -Path (Join-Path $SRC '*') -Destination $API_DEST -Recurse -Force -Exclude 'appsettings.json'
```

**Consequência direta:** acrescentar `LowCode:InlineXmlMaxChars`, `LowCode:TransformationCacheTtlHours`
ou `ML:LowCodeTransformationsPath` ao `appsettings.json` do repo **não tem efeito nenhum em
produção**. O código cai no default silenciosamente e ninguém percebe.

> **Correção do meu próprio plano:** a primeira versão desta spec mandava editar o `appsettings.json`.
> Estava errado. **Todo default novo precisa ser seguro sem configuração**, e o override precisa ir
> por **variável de ambiente** `Section__Key` — o mesmo canal já usado para `Database__Password` no
> `Environment` do serviço Windows.

### 4.2 🔴 O runner `.exe` não é compilado nem copiado por nenhum workflow

`appsettings.json:109` aponta `LowCode:RunnerPath` para
`C:\inetpub\wwwroot\layoutparser\api\LayoutParserLowCodeRunner.exe`.

O projeto existe no repo (`tools/LowCodeRunner/`, net481, com binários versionados em `bin/`), mas
grep em `.github/workflows/*.yml` por `LowCodeRunner` retorna **vazio** — nenhum workflow o compila
ou copia. Compare com `LayoutParserDecrypt.exe`, que **tem** step dedicado (`ci-dev.yml:252-257`).

Ou seja: o binário do qual todo o pathway low-code depende chega ao servidor **por cópia manual**, se
chegar. Não há verificação no deploy nem no smoke test. Precisa de step próprio.

### 4.3 🟠 Redis: não confirmado no servidor

`Redis:ConnectionString = "localhost:6379"` (`appsettings.json:88-91`) e a conexão é tentada **uma
única vez no startup** (`Program.cs:196`); falhou, o cache fica desligado **até reiniciar o
processo** — subir o Redis depois não reativa nada.

Sondagem externa a `172.25.32.42:6379` **não respondeu**. ⚠️ **Isso não prova ausência**: sendo
`localhost`, um Redis ligado apenas à interface local serviria a API e recusaria minha conexão. Fica
como **verificação pendente do `@lp-devops`**, no host:

```bash
redis-cli ping
```

**Consequência para o desenho:** o ganho **não pode depender** de Redis. Disco é o caminho primário;
Redis é aceleração quando existir. É o que o §2.2 já estabelece — e a razão de não invertê-lo.

---

## 5. Ordem de execução

| # | Item | Dono | Prioridade |
|---|---|---|---|
| 1 | `CancellationToken` no runner + liberar semáforo no teto de 6s | `@lp-backend-dev` | **P0** |
| 2 | Cache-first em `RunAsync` por `(sha256, layoutGuid)` — índice em disco, Redis opcional | `@lp-backend-dev` | **P0** |
| 3 | Índice de leitura no store (§2.3) | `@lp-backend-dev` | P0 (habilita o 2) |
| 4 | Endpoints de manifesto e corpo + validação de ticket (§2.5) | `@lp-backend-dev` | P1 |
| 5 | `transformationsTicket` e `outputLength` no payload (§2.6) | `@lp-backend-dev` | P1 |
| 6 | Sanear caminho absoluto do `errorMessage` (§3.1) | `@lp-backend-dev` | P1 |
| 7 | Corrigir fórmula do timeout e devolver parciais no lugar do 504 (§3.2) | `@lp-backend-dev` | P2 |
| 8 | Step de build/cópia do runner `.exe` no deploy (§4.2) | `@lp-devops` | **P0** |
| 9 | Canal de env var para config nova; confirmar Redis no host (§4.1, §4.3) | `@lp-devops` | P1 |
| 10 | Consumir ticket/manifesto; matar o `'processing'` eterno | `@lp-front-dev` | P1 |

Os itens **1 e 2 são independentes entre si** e sozinhos já entregam a maior parte do ganho de
tempo pedido. O item 10 não bloqueia nada: os campos são aditivos e opcionais.

**Defaults obrigatoriamente seguros sem configuração** (§4.1): `InlineXmlMaxChars = 262144`,
`TransformationCacheTtlHours = 2`, store no caminho atual. Nada pode exigir edição de
`appsettings.json` para funcionar.

---

## 6. Erros da primeira versão desta spec (registro)

Três afirmações minhas caíram na verificação. Ficam registradas para que ninguém as ressuscite:

1. **"O botão Gerar Transformação XML responde 404 em produção."** **Falso.** Eu li a constante
   `api.ts:52` (`/api/transformation-execution/...`, hifenizada) e supus ser o ponto de chamada. O
   ponto de chamada real é `transformationService.ts:95`, que usa `/api/transformationexecution/execute-candidates`
   — **sem hífen, e funciona** (confirmado contra a API viva: 400 na rota sem hífen = rota casou;
   404 na hifenizada). A constante de `api.ts:52` é **código morto** (grep: nenhum uso fora da
   declaração de tipo). Sobra uma dívida menor: a constante morta guarda uma URL que 404, e os XML
   docs do back-end (`TransformationCandidate.cs:6`, `TransformationExecutionController.cs:148-153`)
   documentam uma rota que a API não expõe. Corrigir os dois — **baixa prioridade, não é gate**.
2. **"O nome `{sha}_{HHmmss}` é um defeito que impede a leitura."** **Falso** — é esquema
   append-only deliberado que protege histórico de treino. Corrigido no §2.1.
3. **"A segunda execução envenena o dataset com amostra mal rotulada."** **Superdimensionado.** Existe
   quarentena mecânica: `positionalFormatSource`/`suspect` (`LowCodePositionalMetadata.cs:43,79-83`)
   marcam a amostra do `execute-candidates` como `suspect=true`, e a política da ADR já a exclui do
   few-shot. É desperdício de CPU e higiene de corpus, **não** envenenamento — e some sozinho quando
   o item 2 do §5 eliminar a segunda execução.

---

## 7. Quality gates

- `dotnet build` sem erros; `dotnet test` verde.
- **Cache-first:** dois `RunAsync` com o mesmo `(input, layoutGuid)` ⇒ runner invocado **uma** vez.
- **Cancelamento:** estourado o teto síncrono, o slot do semáforo é **liberado** e o processo do
  runner termina — teste com `MaxConcurrentRunners = 1` provando que o upload seguinte não espera.
- **Path traversal:** ticket com `..`, separador de caminho ou fora do charset ⇒ 400, sem nenhuma
  leitura fora de `_storePath`.
- **Sem Redis:** com `IConnectionMultiplexer` nulo, tudo funciona pelo disco (é o cenário provável
  em produção — §4.3, não é hipótese remota).
- **Sem vazamento:** nenhum payload de resposta contém caminho absoluto do servidor.
