# Plano de segurança e arquitetura — LayoutParser API

> `@lp-architect` (Aria), 2026-08-10. Escrito após o fechamento dos gaps de configuração do deploy
> (commits `f48b5a9` e `a632d6c`). Tudo abaixo foi verificado no código desta árvore, não herdado de
> documento anterior — onde um doc existente diverge do código, o código venceu e está anotado.
>
> **Atualização (mesma data), após autorização do dono para decidir e executar os gates.** O
> diagnóstico do §2.1 foi rodado na máquina de dev e mudou o quadro: a instância do Sysmiddle
> **existe**, só não com o nome que o step procurava. §2.1, §3.2 e o passo 1 do §2.2 foram
> executados; §3.1 foi instrumentado mas **não** invertido. Ver §6 para o estado por item.

---

## 1. Onde a ferramenta está hoje

O que os commits de hoje fecharam, e o que **não** fecharam, importa para ler as prioridades abaixo.

**Fechado:** a configuração low-code agora chega ao destino (`LowCode__Package`,
`LowCode__RunnerTimeoutSeconds`, `ML__LowCodeTransformationsPath`, `AiMetrics__IngestApiKey`); o
timeout deixou de matar toda transformação no meio; o endpoint de multi-candidato deixou de segurar
o cliente por 6 minutos e de vazar slots do runner; o arranque agora denuncia config low-code
incompleta em uma linha de log; e o quality gate parou de mentir (rodava com zero testes).

**Não fechado — e é o que decide se a ferramenta funciona:** o `LayoutParserLowCodeRunner.exe` **não
é publicado por nenhum workflow**. `LowCode:RunnerPath` aponta para
`C:\inetpub\wwwroot\layoutparser\api\LayoutParserLowCodeRunner.exe`, um arquivo que ninguém coloca
lá. Sem ele, tudo que foi configurado hoje está correto e inerte.

> **A causa não é esquecimento, é uma restrição real.** O `.csproj` do runner referencia
> `SysMiddle.Base.dll` por `$(InstanceBin)` — a `Bin` da instância FiatMQ, que vive em `.claude/tmp/`
> e é ignorada pelo git. O CI não tem como compilá-lo. E o `.exe` versionado em
> `tools/LowCodeRunner/Functions/` (verificado: **está** sincronizado com o fonte, contém o exit 9 e
> a flag `--nfePostProcessing`) **não executa de onde está** — é `net481/x86` e resolve dependências
> pelo diretório do próprio `.exe`, então só roda de dentro da `AppConnector.DIR\Bin`. De
> `Functions/` estoura em `InstanceFactory.Initialize()` por divergência de assembly (log4net
> 1.2.13.0 vs 2.0.17.0).

O step de diagnóstico não-fatal adicionado aos dois workflows existe exatamente para responder isso
de dentro do host — o `.42` não aceita SSH/WinRM/SMB, então não há outra forma. **A próxima execução
do deploy é o instrumento de decisão.** Ver §2.1.

---

## 2. P0 — bloqueia produção ou já está exposto

### 2.1 Publicação do runner low-code *(bloqueia toda transformação)*

Sem isso, nada do pathway low-code funciona em produção. Três opções, com custos honestos:

| Opção | Como | Custo / risco |
|---|---|---|
| **A. Publicar dentro da `Bin` da instância** *(recomendada)* | Step no `deploy.yml` copia o `.exe` versionado para `<instância>\AppConnector.DIR\Bin\` e `LowCode:RunnerPath` passa a apontar para lá | Depende de existir instância no host — **o diagnóstico responde**. Escrever na `Bin` de um produto de terceiros exige combinar com quem opera o FiatMQ |
| **B. Vendorizar a `Bin` no repo** | Versionar as DLLs do Sysmiddle para o CI compilar | Licenciamento de binário de terceiros + repo pesado. **Não recomendo** sem aval jurídico |
| **C. Runner como serviço na máquina que já tem a instância** | API chama por HTTP em vez de processo | Melhor desenho de longo prazo, maior esforço; resolve também §3.3 |

**Ação imediata:** rodar o deploy e ler o diagnóstico. Se houver instância, é a opção A e é barata.
Se não houver, a decisão sobe para o dono do projeto — e nesse caso *nenhuma* das configurações de
hoje entrega transformação, o que muda a prioridade de tudo mais.

### 2.2 A API não tem autenticação nenhuma *(exposição ativa)*

Verificado em `Program.cs`: `app.UseAuthorization()` está **comentado** (linha 476),
`UseAuthentication` não existe, e `app.UseHttpsRedirection()` também está comentado (linha 473). O
Kestrel escuta `0.0.0.0:5000` e o CORS libera origens de rede interna (`172.25.32.42`).

São **18 controllers**. Apenas **2 endpoints** (ingestão de métricas de IA) exigem credencial, via
`X-AiMetrics-Key`. Todo o resto — upload e parse de documento, catálogo de layouts, execução de
transformação, leitura de logs — está aberto a qualquer host da rede, **em HTTP puro**.

O agravante é o dado: são NF-e reais de cliente, com CNPJ, valores e itens. Isso não é só postura
frouxa de API interna; é dado fiscal de terceiro trafegando em claro e acessível sem credencial.

> ⚠️ **Onde a documentação diverge do código:** `rules/security.md` trata isso como uma nota de
> rodapé ("a app não usa autenticação no pipeline atual — sinalize se um endpoint novo expuser dado
> sensível"). Isso subestima: o problema não é o endpoint *novo*, são os 18 controllers que já
> existem. Recomendo reclassificar como item de primeira ordem.

**Proposta (incremental, sem parar o produto):**

1. **Agora, barato:** estender o padrão do `AiMetricsIngestKeyFilter` — que já existe, é fail-closed
   e é conhecido do time — a um filtro global de API key, com allowlist explícita para o que o front
   consome anonimamente. Não é autenticação de verdade, mas tira a API de "aberta".
2. **Depois, correto:** autenticação integrada (Windows/Negotiate) — o ambiente já é AD e o front é
   interno. Casa com a recomendação de gMSA do §3.1 e não introduz novo gerenciamento de segredo.
3. **Junto:** ligar HTTPS. Hoje `UseHttpsRedirection` está comentado; sem TLS, qualquer credencial
   que se introduza no passo 1 trafega em claro e o ganho é ilusório.

### 2.3 Segredos já expostos — duas ações que não são minhas nem do CI

Ambas continuam pendentes e **nenhuma é executável por agente**:

- **Senha do SQL** — comprometida duas vezes, e desde a PR #7 está no histórico da `master`. A
  rotação está **bloqueada no DBA**. Enquanto isso, o secret `DB_PASSWORD_DEV` carrega a senha
  comprometida por necessidade operacional. Runbook em `rules/security.md`.
- **API key do Gemini** — Gemini foi decomissionado (2026-07-21), então a ação é **revogar, não
  rotacionar**. Exige console interativo do Google com a conta que gerou a chave. Passos em
  `rules/security.md`.

**Limpeza do histórico** (`git filter-repo`/BFG) segue pendente e **não substitui** a rotação: todo
clone anterior à limpeza continua com os segredos. Rotacionar primeiro, limpar depois.

> Nota factual sobre o registro do Windows: injetar segredo no `Environment` do serviço
> (`REG_MULTI_SZ` em `HKLM`) é melhor que texto plano em arquivo versionado, mas **não é cofre** —
> qualquer administrador local lê. É um degrau intermediário aceitável, não o destino. O destino é
> §3.1.

---

## 3. P1 — dívida que já está cobrando juros

### 3.1 Config drift entre repositório e destino *(a causa-raiz do trabalho de hoje)*

O deploy copia com `-Exclude appsettings.json`, preservando o do servidor. A intenção é boa
(preservar ajuste local), mas o efeito é que **toda chave nova adicionada ao repositório é
silenciosamente ignorada em produção** — o código cai no default sem avisar ninguém.

Isso não é um bug: é uma fábrica de bugs. O gap do `LowCode:Package` foi uma instância. O do
timeout, outra. O do `ML:LowCodeTransformationsPath`, outra. Cada chave nova é uma chance nova.

Os commits de hoje mitigaram por três caminhos (env var no CI, defaults seguros sozinhos,
diagnóstico no arranque), mas a estrutura continua de pé. Opções:

| Opção | Efeito | Custo |
|---|---|---|
| **A. Inverter o default: repo é a fonte, destino sobrepõe** | `appsettings.json` sempre copiado; overrides ficam em `appsettings.Production.json` (não versionado) e env vars | Migração única do que hoje é ajuste local. **Recomendada** — devolve ao repo a autoridade sobre config |
| **B. Manter e blindar** | Continuar com env var + defaults seguros + diagnóstico | Zero, já está feito. Mas exige disciplina eterna e falha em silêncio quando alguém esquece |
| **C. Configuração centralizada** | Consul/App Configuration | Desproporcional para uma instância |

Recomendo **A**, com a migração feita junto do primeiro deploy que já vá mexer no destino.

### 3.2 Código morto de IA com DI quebrado

`GeminiAIService`, `SemanticAIGenerator` e o `DataGenerationController` inteiro **não estão
registrados no DI**. Não é remediação deliberada — é bug: os endpoints que dependem deles lançam
exceção em runtime. Hoje isso funciona como uma proteção acidental (a chave do Gemini não vaza
porque o código não roda), e é justamente por isso que é perigoso: parece seguro, mas é uma mina.

Com Gemini/OpenAI decomissionados e Ollama assumindo 100% do diagnóstico, esse código não tem
futuro. **Remover** (escopo já mapeado em `docs/architecture/ai-roadmap-dispatch.md`, Grupo 1) é
mais barato e mais seguro que consertar o DI.

### 3.3 O semáforo do runner é um gargalo global

`LowCodeTransformationService` é Singleton e o `SemaphoreSlim` vale para o **processo inteiro da
API** — não por request. Com `MaxConcurrentRunners = 2` e cada execução levando 48–137s, **dois
uploads simultâneos saturam a API inteira** para transformação. O terceiro usuário espera em fila
sem saber.

O vazamento de slot que corrigi hoje agravava isso (slots ficavam presos após o 504), mas o gargalo
estrutural permanece. Não recomendo simplesmente aumentar o número: o host FiatMQ é sensível a
execução concorrente e a licença é disputada. O caminho é a opção C do §2.1 — runner como serviço
próprio, com fila explícita e observável, em vez de um semáforo invisível dentro do processo web.

### 3.4 Três pathways de transformação, um deles sem leitor

Já mapeado (`transformation-pathway-duplication.md`): Pathway 1 sem caller no front, Pathway 2
canônico, e um terceiro caminho — o campo `transformations` do `ParseController` — que o front nunca
lê. Três implementações do mesmo conceito significam três lugares para corrigir cada bug e três
comportamentos possíveis para a mesma pergunta do usuário.

A decisão de que o Pathway 2 é canônico já foi tomada. Falta **executar a depreciação** — enquanto
não executar, o custo continua sendo pago em cada mudança.

### 3.5 IDOC parseia "com sucesso" e erra 100% dos campos

`TextPositional` é sobrecarregado: serve MQSeries e IDOC, e o discriminador real (`WithBreakLines`)
**nunca é lido pelo parser**. O resultado é a pior classe de defeito possível em dado fiscal —
sucesso reportado com conteúdo errado, sem nenhum sinal.

Isso não é gap de configuração, é correção de domínio, e merece prioridade acima do resto desta
seção se IDOC estiver em uso real.

---

## 4. P2 — estrutural, quando houver espaço

- **Autenticação integrada / gMSA para o SQL** (§2.2 e §2.3 convergem aqui): elimina a senha da
  configuração por completo e o AD rotaciona sozinho. Exige alinhamento com infra/AD.
- **CORS**: hoje `AllowAnyMethod().AllowAnyHeader().WithExposedHeaders("*")` com origens fixas.
  Depois da autenticação, apertar métodos e headers para o que o front realmente usa.
- **`AllowedHosts: "*"`**: restringir ao host real, barato depois que houver TLS.
- **Warnings de nullable**: ~10 `CS8618`/`CS8604` pré-existentes. Não são urgentes, mas cada um é um
  `NullReferenceException` esperando entrada específica; vale limpar por arquivo quando tocá-lo.

---

## 5. Sequência recomendada

A ordem importa mais que a lista, porque alguns itens mudam o valor dos outros:

1. **Rodar o deploy e ler o diagnóstico** (§2.1). Barato, e a resposta redefine as prioridades: sem
   runner no destino, todo o pathway low-code é teórico.
2. **Publicar o runner** conforme a resposta acima. Até aqui, a ferramenta não transforma nada em
   produção.
3. **TLS + filtro de API key global** (§2.2). Na ordem: TLS primeiro, senão a credencial trafega em
   claro e o ganho é aparente.
4. **Destravar a rotação do SQL com o DBA** (§2.3) — escalar, é bloqueio externo, não técnico. Em
   paralelo com o item 3.
5. **Inverter o default de config** (§3.1), aproveitando um deploy que já vá mexer no destino.
6. **Remover o código morto de IA** (§3.2). Barato, e tira a mina do caminho.
7. **IDOC** (§3.5), se estiver em uso real — nesse caso sobe para junto do item 3.
8. **Depreciar os pathways redundantes** (§3.4) e **repensar o runner como serviço** (§3.3).

**O que não está no meu alcance e precisa de decisão ou acesso do dono do projeto:** revogar a chave
do Gemini (console interativo do Google), destravar a rotação do SQL (DBA), autorizar escrita na
`Bin` da instância FiatMQ, e criar os secrets `AI_METRICS_INGEST_KEY` / `AI_METRICS_INGEST_KEY_DEV`
no GitHub.

---

## 6. Estado da execução (2026-08-10, após autorização do dono)

### 6.1 O diagnóstico do §2.1 — o que a máquina respondeu

Rodado na máquina de dev. **Corrige uma premissa do §1 deste plano:**

| Pergunta | Resposta |
|---|---|
| Instância do Sysmiddle neste host? | **SIM** — `C:\appconnector\App\Bin`, 580 arquivos, **log4net 2.0.17.0** |
| Runner em `<deploy>\api\`? | **AUSENTE** — confirma o bloqueador |
| `<deploy>\api\Functions`? | log4net **1.2.13.0** — a versão que estoura em `InstanceFactory.Initialize()` |
| Redis em `localhost:6379`? | **Aceita conexão** |
| Serviço `LayoutParserApi`? | **Running**, com `LowCode__Package`, `LowCode__RunnerTimeoutSeconds` e `ML__LowCodeTransformationsPath` já no `Environment` |

> ⚠️ **A varredura por `AppConnector.DIR` não achou nada** — e havia instância o tempo todo. O step
> de diagnóstico que eu mesma commitei em `f48b5a9` procurava o *nome da pasta* usado no ambiente de
> referência; aqui a instância se chama `C:\appconnector\App`. Era um **falso negativo** que teria
> mandado a decisão para o caminho errado ("não há instância, escale ao dono"). Corrigido em
> `27a5ca0`: a detecção passou a ser por **conteúdo** — `SysMiddle.Base.dll` com log4net 2.x ao lado
> — e o relatório imprime a versão de cada candidata, que é o dado que decide.

O `AiMetrics__IngestApiKey` **não** está no `Environment`: o secret `AI_METRICS_INGEST_KEY_DEV`
segue por criar, e a ingestão de métricas continua recusada.

### 6.2 O que foi executado

| Gate | Decisão | Commit |
|---|---|---|
| **§2.1** Publicar o runner | **Opção A**, confirmada pelo diagnóstico. Step publica o `.exe` na Bin apta (detecção por conteúdo) e injeta `LowCode__RunnerPath`. Não-fatal: sem Bin apta, o deploy segue. | `27a5ca0` |
| **§3.2** Código morto de IA | Remoção **cirúrgica**, não expurgo do cluster. Saíram `GeminiAIService`, `SemanticAIGenerator` e os 4 models de resposta; ficaram a geração por regras e o `DataGenerationController`, porque dado sintético é workstream ativo. −1390 linhas. | `aa54dc3` |
| **§2.2** Autenticação (passo 1) | `ApiKeyGateFilter` **global**, nascendo **desligado**. CI ganhou o canal para ligá-lo sem quebrar o smoke test. 13 testes, validados por mutação. | `b2179c2` |
| **§3.1** Config drift | **Instrumentado, não invertido** — ver abaixo. | `491debd` |

### 6.3 Por que o §3.1 não foi invertido

O plano recomenda inverter o default (repo como fonte). **Não executei**, e a razão é factual, não
cautela genérica: ninguém sabe o que diverge no `appsettings.json` do `.42`, porque o host nega
SSH/WinRM/SMB e o arquivo nunca foi comparado com o do repo. Inverter às cegas pode apagar ajuste
local que sustenta a produção, e não há como testar isso daqui.

O step novo roda em **dry-run** e produz exatamente o dado que falta, ao custo de um deploy: chaves
só no repo (que nunca chegaram), divergentes, e só no destino. Valor de chave sensível nunca é
ecoado. Lido o relatório, a migração liga por `MIGRATE_CONFIG_TO_REPO=true`.

**Trocar um silêncio por outro não seria executar o recomendado — seria fingir que executou.**

### 6.4 O que continua exigindo ação humana

| Item | Por quê | Quem |
|---|---|---|
| Validar o runner de ponta a ponta na Bin | Precisa copiar o `.exe` para `C:\appconnector\App\Bin` e rodar o gate de equivalência (4246 bytes). Bloqueado por permissão nesta sessão | Dono / `@lp-devops` |
| Rotação da senha SQL | Bloqueio externo | DBA |
| Revogar a chave do Gemini | Console interativo do Google | Dono |
| Secrets `AI_METRICS_INGEST_KEY(_DEV)` | Ingestão de métricas segue recusada | Operador |
| Secret `API_KEY_DEV` + header no front | É o que liga a autenticação de fato | Operador + front |
| TLS | `UseHttpsRedirection` comentado; sem ele a chave trafega em claro | Infra |
| Combinar escrita na Bin do FiatMQ em produção | O step é aditivo e reversível, mas mexe em produto de terceiros | Dono + operação FiatMQ |
| `MIGRATE_CONFIG_TO_REPO=true` | Só depois de ler o relatório de dry-run | Dono |
