# Plano de Execução Multi-Sessão — Trilha A (CLI) × Trilha B (Windows UI)

> **Autor:** @lp-architect (Aria) · **Status:** Trilha B COMPLETA (merged) · Trilha A: A1 pronto (branch `feat/lowcode-batch-mode`), A2–A5 especificadas (ver §8), ainda não implementadas · **Data:** 2026-07-15 (criação) / 2026-07-16 (auditoria de reconciliação §7 + especificação A2–A5 §8)
> **Contexto:** o usuário opera **duas sessões Claude Code na mesma máquina** (mesmo disco C:, mesma pasta do
> repo): esta sessão CLI (WSL) e uma sessão Windows Claude Code UI nativa. Objetivo: dividir o trabalho
> pendente (ver [`poc-excel-generator.md`](poc-excel-generator.md) §11 e
> [`multi-client-layout-generalization.md`](multi-client-layout-generalization.md) §5) em duas trilhas
> paralelas, sem as sessões pisarem uma na outra.
> **Este documento é o ponto de entrada para a Sessão B** — leia-o primeiro antes de tocar em qualquer arquivo.

---

## 0. ⚠️ CORREÇÃO CRÍTICA (2026-07-15) — "branch por trilha" num só diretório NÃO isola; usar `git worktree`

O plano original mandava cada sessão fazer `git checkout <sua-branch>` na **mesma pasta**. **Isso estava errado
e causou intermingling real:** as duas apps apontam para o mesmo diretório = **um único working tree**. A branch
é propriedade do working tree, compartilhada pelas duas sessões — quando uma faz `checkout`, muda a branch para
ambas, e as alterações não-commitadas das duas se acumulam juntas. Observado na prática: trabalho da Trilha A
(fix do runner) e da Trilha B (LineLengthResolver, Services/*) apareceram misturados no mesmo `git status`.

**Correção aplicada (isolamento real via `git worktree`):**
```
/mnt/c/.../LayoutParserApi          → feat/lowcode-runner-bootstrap  (Trilha A · sessão CLI)
/mnt/c/.../LayoutParserApi-trackB   → feat/multi-client-track-b      (Trilha B · sessão Windows)
```
- A sessão CLI (Trilha A) **não pode se realocar** no meio da execução → fica na pasta principal.
- A **sessão Windows (Trilha B) deve abrir a pasta `../LayoutParserApi-trackB`** (o worktree isolado). A partir
  daí, cada sessão tem pasta + branch próprias DE VERDADE; `git status`/commits de uma não vazam para a outra.
- Compartilhado entre os worktrees (mesmo `.git`): histórico de commits e, por serem gitignored e fora do
  versionamento, `.claude/tmp/` (inclui o `Instance_FiatMQ` e os gabaritos). Ou seja: a regra de §1 (só a
  Trilha A roda o runner) **continua valendo** — o worktree isola git, não o `.claude/tmp/` nem o processo do runner.
- Merge no final: `git merge feat/multi-client-track-b` (ou PR) a partir da branch A, quando as fases fecharem.

---

## 1. Por que a divisão é assim (restrição real, não escolha arbitrária)

Mesma máquina/disco ⇒ **`.claude/tmp/` e `tools/LowCodeRunner/{globalfolder,bin}` já são visíveis para as duas
sessões automaticamente** (não são rastreados pelo git — `tools/LowCodeRunner/.gitignore` ignora
`globalfolder/` e `bin/`; `.claude/tmp/` inteiro está fora do git). Isso inclui os **408 MB** do
`Instance_FiatMQ` já com o fix de licença aplicado (ver `poc-excel-generator.md` §9.4).

**Mas isso não significa que as duas sessões podem rodar o runner ao mesmo tempo.** O `LayoutParserLowCodeRunner.exe`
lê/escreve `global.config`, `logger.xml` e o diretório `AppConnector.DIR/Log` dentro do MESMO `Instance_FiatMQ`
compartilhado — **duas execuções concorrentes correm risco real de lock de arquivo ou corrupção de log/config.**
Por isso: **todo trabalho que executa o runner fica só na Trilha A.** A Trilha B trabalha em código puro
(compilação/análise, sem executar o `.exe` do runner).

---

## 2. Trilha A — esta sessão (CLI/WSL), branch `feat/lowcode-runner-bootstrap`

Continua na branch atual (já tem todo o histórico relevante do runner). Executa o `.exe`, gera gabaritos,
usa a rede (porta 1433 já confirmada alcançável para o Connect Us).

| # | Fase | Arquivos tocados | Depende de |
|---|---|---|---|
| A1 | **Modo lote do runner** — varrer `Examples/LAY_*`, gravar pares input→XML em `.claude/tmp/gabaritos/` | `tools/LowCodeRunner/{Program.cs,SysmiddleMapperExecutor.cs}` | nada — próximo passo desta sessão |
| A2 | **Generalização além do par único** (ICMS10-90/CSOSN, grupos especiais) usando os gabaritos novos de A1 | `ai/XslSynth/Excel/{XslGenerator,NfeLeiauteCatalog}.cs` | A1 |
| A3 | **P0 — Catálogo GUID→XPath** (237 LinkMappings), rodando o parser no layout NF-e alvo via runner | novo(s) arquivo(s) em `ai/XslSynth/Core/` (ex.: `GuidXPathCatalog.cs`) | runner funcional (já destravado) — pode começar em paralelo a A1/A2 |
| A4 | **G3 — `LayoutSpecExtractor`** (2º adaptador de ingestão via Connect Us, ver `multi-client-layout-generalization.md` §4.3) | novo arquivo em `ai/XslSynth/Excel/` | A1 (modo lote provê os dados de teste) |
| A5 | **G4 — Generalizar `MapperEmissionGuide`** de 8 campos conhecidos para motor genérico de condicionalidade | `ai/XslSynth/Excel/MapperEmissionGuide.cs` | A3/A4 (precisa de mais mapeadores reais) |

---

## 3. Trilha B — sessão Windows Claude Code UI, branch `feat/multi-client-track-b`

**Já criada localmente** (aponta para o mesmo commit desta sessão — `git branch feat/multi-client-track-b`,
sem push). Ao abrir a sessão B: `git checkout feat/multi-client-track-b`. **Não precisa do runner** — todo item
abaixo é código/análise pura.

| # | Fase | Arquivos tocados | Depende de |
|---|---|---|---|
| B1 | **G1 — Resolver único de tamanho de linha** (`LimitOfCaracters` > 0 → override da allowlist → null) e substituição dos ~20 literais `600` no núcleo | `Services/Implementations/LayoutParserService .cs`, `Services/Parsing/Implementations/{LayoutValidator,LayoutDetector}.cs`, `Services/Transformation/MapperTransformationService.cs`, `Services/Validation/{DocumentValidationService,DocumentMLValidationService}.cs`, `Models/Configuration/LayoutLineSizeConfiguration.cs` | G0 ✅ (já concluído, ver `multi-client-layout-generalization.md` §4.2) |
| B2 | **G2 — Parametrizar tamanho de linha** em vez de `const int LineLength = 600` | `ai/XslSynth/Excel/{RootTreeBuilder,NfeGabaritoMiner}.cs` | nada — independente |
| B3 | **Diffs cosméticos residuais** (declaração XML, ordem de atributos `Id`/`versao`) | `ai/XslSynth/Excel/XslGenerator.cs` (ou onde o `enviNFe` é serializado) | nada — trivial |
| B4 | **P1 — RAG few-shot** sobre corpus G2KA (9 regras difíceis do `DslBlockInterpreter`) | novo índice, provável `ai/XslSynth/Synthesis/` | nada — usa `RAG.ExamplesPath` já configurado |
| B5 | **P1 — Pipeline "NT nova"** (protótipo do diff XSD + extração de PDF) | novo, área de exploração — não mexe em código de produção ainda | nada — é design/prototipagem |
| B6 | **P2 — Detector de anomalia** sobre `MLData/DocumentPatterns` | `Services/Learning/*`, `MLData/*` | nada — independente |

**Risco de conflito de arquivo entre trilhas:** B1/B2 tocam `ai/XslSynth/Excel/RootTreeBuilder.cs` e
`NfeGabaritoMiner.cs` (B2) enquanto A2 toca `XslGenerator.cs`/`NfeLeiauteCatalog.cs` — **arquivos diferentes,
sem sobreposição.** Confirmado: nenhuma fase de A e nenhuma de B tocam o mesmo arquivo nesta divisão.

---

## 4. Fora das duas trilhas — dependem do resultado de ambas

| # | Fase | Depende de |
|---|---|---|
| C1 | Integração no runtime de produção (`Services/LowCode/LowCodeTransformationService`) | A2 (provar generalização) + B1 (núcleo generalizado) |
| C2 | Gate formal do `@lp-qa` (Quinn) | A2, B1, B2, B3 |
| C3 | Documentação de produto (`@lp-doc`, Duda) | C1 |
| C4 | 🔴 Rotação de segredos (`@lp-devops`, Gage) | **independente — não é código, roda em paralelo a qualquer momento**, ver `poc-excel-generator.md` §11 item 11 |

---

## 5. Protocolo de handoff entre sessões

1. **Sessão B, ao abrir:** `git checkout feat/multi-client-track-b`. Ler, nesta ordem: este documento →
   `multi-client-layout-generalization.md` (o design completo de B1) → `poc-excel-generator.md` §11 (contexto
   geral). **Não precisa ler o histórico de conversa desta sessão A** — os docs são o contrato.
2. **Sincronização:** cada sessão comita na sua própria branch (local, sem push — `push só por @lp-devops`).
   Quando uma fase fecha, quem estiver na sessão avisa o usuário; o merge das duas branches (Trilha A ⟶ Trilha B
   ou ambas ⟶ `feat/lowcode-runner-bootstrap`) é decisão do usuário/`@lp-devops`, não automático.
3. **Se A e B precisarem trocar informação no meio do caminho** (ex.: B1 descobre algo que muda o
   comportamento que A2 espera): registrar em `multi-client-layout-generalization.md` (é o documento
   compartilhado das duas trilhas) e avisar o usuário — ele está nas duas sessões e pode repassar.
4. **Nunca rodar o `.exe` do runner a partir da Trilha B** (§1) — se uma fase de B precisar de um gabarito
   novo, ela pede para a Trilha A gerar (via A1) em vez de executar por conta própria.

---

## 6. Estado em 2026-07-15 (histórico — ver §7 para o estado real corrigido em 2026-07-16)

- Branch `feat/multi-client-track-b` criada localmente, sincronizada com o HEAD atual desta sessão.
- Trilha A (esta sessão) segue para **A1 — modo lote do runner**.
- Trilha B fica livre para a sessão Windows começar por **B1** (não depende de nada, G0 já fechado) assim que
  o usuário abrir aquela sessão e fizer o checkout.

---

## 7. Auditoria de reconciliação (2026-07-16, Aria) — Trilha B fechada, Trilha A NÃO

### 7.1 Trilha B — CONFIRMADA completa e mergeada

`feat/multi-client-track-b` foi mergeada em `master` em duas PRs: **#3** (commit `d096364` — B1
resolver de tamanho de linha + B2 parametrização) e **#5** (commits `346c8d5`/`c60fd74`/`1d583fa` —
B3 diffs cosméticos byte-idênticos, B4 índice few-shot, B5/B6 detector de anomalias + CLI XSD diff).
Todas as 6 fases (B1-B6) estão em `master`. Nada pendente aqui.

### 7.2 Trilha A — NÃO fechada. O que de fato existe

A branch `feat/lowcode-runner-bootstrap` **não tem nenhum commit além do que já está em `master`**
(mergeada via PR #4, commit `05ebb3c` — só o texto deste plano, zero código de A1-A5).

O trabalho de A1 que de fato aconteceu ficou em **outra branch, não mergeada**:
`docs/track-a-a1-status` (commits `8daae8a`, `9ae36d6`). Auditoria do diff completo contra
`master` (`git diff master...docs/track-a-a1-status`) confirma que essa branch contém
**exclusivamente atualizações de documentação/memória** — `poc-excel-generator.md` §11.1/11.2/11.3
e um novo memory file do Lia (`multi-client-mappers.md`). **Nenhum arquivo de código-fonte foi
alterado nela** (não `tools/LowCodeRunner/*`, não `ai/XslSynth/*`).

O que essa documentação registra, e que é o achado real de valor da sessão:
- **62 pares input→XML reais do cliente FIAT** foram gerados em `.claude/tmp/gabaritos/fiat-sweep/`
  (+ `_manifest.tsv`), rodando o `.exe` do runner em lote **via harness bash externo** (não código
  commitável) — `.claude/tmp/` é gitignored, os dados não estão no controle de versão do repo de
  produção.
- **Descoberta multi-cliente**: o package único `938f9978-…` contém os 170 mapeadores de TODOS os
  clientes (FIAT/CNHI/IVECCO/MARELLI/COMAU/PSCA/…) — generalizar é escolher o maperador certo, não
  trocar de package. GUIDs dos mapeadores SEND_ENV de 4 clientes já identificados (FIAT
  `MAP_f31a6758`, CNHI `MAP_f1a6453f`, IVECCO `MAP_166b4df6`, MARELLI `MAP_204a020e`/`MAP_1cfab556`)
  e gabaritos adicionais gerados para eles (`.claude/tmp/gabaritos/{cnhi,ivecco,marelli}/` +
  `multi-client-manifest.tsv`), trazendo variantes fiscais reais (ICMS10/ICMS40, IPITrib,
  PISNT/Outr) que o FIAT sozinho não exercita — ver
  `.claude/agent-memory/lp-parser-llm/multi-client-mappers.md` (nessa branch, não em `master`).

**Conclusão:** A1 está **PARCIAL** — o dado existe (fábrica de gabaritos provada, à mão), o
**código do modo lote não existe** (item #2 de `poc-excel-generator.md` §11.2, correção pendente:
o texto atual em `master` ainda mostra #2 como pendência plena, não reflete o "PARCIAL" registrado
em `docs/track-a-a1-status` — corrigido nesta rodada, ver §7.4). A2 (generalização de variantes),
A3 (catálogo GUID→XPath), A4 (`LayoutSpecExtractor`/G3) e A5 (generalizar `MapperEmissionGuide`/G4)
**não foram iniciadas** — confirmado por inspeção de `ai/XslSynth/Core/` e `ai/XslSynth/Excel/`:
nenhum arquivo novo (`GuidXPathCatalog.cs`, `LayoutSpecExtractor.cs`) existe, e
`tools/LowCodeRunner/Program.cs` continua single-shot (`Uso: LayoutParserLowCodeRunner
<globalFolder> <package> <mapperGuid|LIST> <input> <output>` — uma execução por chamada, sem laço
de varredura).

### 7.3 Decisão de reconciliação de branches (executar via `@lp-devops`, NÃO por mim)

Recomendação: **descartar `feat/lowcode-runner-bootstrap`** (não tem nada que `master` não tenha —
mergeá-la de novo é no-op) e **trazer `docs/track-a-a1-status` para dentro de uma branch de trabalho
nova para o retomar de A1-A5** (ex.: `feat/lowcode-batch-mode`, criada a partir de `master` +
cherry-pick/merge dos 2 commits de doc dessa branch). Não decido isso sozinha porque envolve
`git branch -D` / merge — escalo a operação para `@lp-devops`:

```
1. git checkout master && git pull
2. git checkout -b feat/lowcode-batch-mode
3. git merge docs/track-a-a1-status   # traz só doc/memory, sem conflito esperado (arquivos não tocados por B1-B6)
4. git branch -d feat/lowcode-runner-bootstrap        # local, sem código próprio
5. git push origin --delete feat/lowcode-runner-bootstrap docs/track-a-a1-status   # após confirmação do usuário
```

### 7.4 Plano de finalização da Trilha A (o que falta, por fase)

| Fase | Falta | Arquivos | Dono | Depende de |
|---|---|---|---|---|
| **A1** | Codificar o **modo lote** no runner (hoje é bash externo repetindo `EXEC` um a um): novo modo `SWEEP <globalFolder> <package> <examplesDir> <outDir>` que itera `Examples/LAY_*`, chama a mesma lógica de `EXEC` para cada input, grava manifest. Reaproveitar os 62 pares já gerados como fixture de regressão em vez de regerar. | `tools/LowCodeRunner/Program.cs`, `SysmiddleMapperExecutor.cs` | `@lp-backend-dev` (Dex) | nada (runner já funcional) |
| **A1(b)** | Estender a varredura a CNHI/IVECCO/MARELLI usando os GUIDs de mapeador já identificados (ver §7.2) — precisa decidir se o modo `SWEEP` aceita GUID de mapeador por cliente ou se isso fica em script separado | mesmo `Program.cs` + os dados já em `.claude/tmp/gabaritos/{cnhi,ivecco,marelli}` | `@lp-parser-llm` (Lia) | A1 |
| **A2** | Generalizar além do par único: variantes ICMS10/20/30/40/51/60/70/90/CSOSN, grupos especiais (veículo/ANVISA/ANP/combustível/DI), 2ª aba do Excel (Layout-Receb), outras versões de NT — usar os gabaritos multi-cliente de A1(b) como fixtures novas | `ai/XslSynth/Excel/{XslGenerator.cs,NfeLeiauteCatalog.cs}` | Lia (domínio) + Dex (código) | A1(b) — sem gabaritos de outros clientes, A2 fica testando só o caso FIAT de novo |
| **A3** | P0 — Catálogo GUID→XPath (destrava os 237 `LinkMappings` do XslSynth) | novo arquivo `ai/XslSynth/Core/GuidXPathCatalog.cs` | Lia | nada — pode rodar em paralelo a A1/A2 (já destravado desde que o runner funciona) |
| **A4** | G3 — `LayoutSpecExtractor` (2º adaptador de ingestão via Connect Us, `multi-client-layout-generalization.md` §4.3) | novo arquivo em `ai/XslSynth/Excel/` | Dex (wiring) + Lia (domínio) | A1 (modo lote fornece dados de teste) |
| **A5** | G4 — Generalizar `MapperEmissionGuide` de 8 campos hardcoded para motor genérico de condicionalidade | `ai/XslSynth/Excel/MapperEmissionGuide.cs` | Dex + Lia | A3/A4 — precisa de mapeadores reais adicionais para não generalizar em cima de 1 exemplo só |

**Sequenciamento recomendado:** A1 (código do modo lote, baixo esforço/alta alavancagem porque
formaliza o que já foi provado à mão) → A1(b) em paralelo com A3 (não competem por arquivo) → A2
depois de A1(b) (precisa de diversidade fiscal real, não só FIAT) → A4/A5 por último (dependem de
volume de mapeadores reais que só A1(b)/A2 produzem).

**Item de doc pendente:** `master`'s `poc-excel-generator.md` §11.2 item #2 ainda mostra "Modo lote
do runner" como pendência plena (Dono: Dex, sem menção ao PARCIAL); a versão em
`docs/track-a-a1-status` já tem a correção (PARCIAL, com o detalhamento (a)/(b)). Ao aplicar a
reconciliação de branches do §7.3, essa correção de doc vem junto automaticamente — não precisa
reescrever à mão.

---

## 8. Especificação de A2–A5 (2026-07-16, Aria) — contrato para retomada pós-A1

**Ponto de partida confirmado:** A1 (`SWEEP` real em `tools/LowCodeRunner/Program.cs`) está
implementado na branch `feat/lowcode-batch-mode` (`dotnet build` verde, commit local). Isso muda o
estado de dependência de A2–A5: elas deixam de depender de "dado gerado à mão via bash" e passam a
depender de "o modo lote existe como comando repetível" — a diferença importa porque A2/A4
precisam rodar o SWEEP de novo (para CNHI/IVECCO/MARELLI e para o adaptador Connect Us) sem
reescrever scripts ad-hoc a cada vez.

Cada especificação abaixo segue o mesmo formato: **o que deve existir ao final**, **critérios de
aceite**, **dependências reais**, **domínio (Lia) vs infra .NET pura (Dex)**.

### 8.1 A2 — Generalização além do par único (ICMS10-90/CSOSN, grupos especiais)

- **O que deve existir:** `ai/XslSynth/Excel/XslGenerator.cs` e `NfeLeiauteCatalog.cs` deixam de
  assumir o único gabarito FIAT como verdade de referência e passam a resolver, por documento, qual
  variante de grupo fiscal (ICMS 00/10/20/30/40/51/60/70/90, CSOSN, grupos especiais — veículo/
  ANVISA/ANP/combustível/DI) aplicar, usando como fixtures os gabaritos multi-cliente já minerados em
  `.claude/tmp/gabaritos/{fiat-sweep,cnhi,ivecco,marelli}/` (ver §7.2). Não é reescrever os
  geradores — é parametrizar a seleção de variante em vez de hardcode do caso único.
- **Critério de aceite:** rodar `XslGenerator` contra pelo menos 2 clientes com regime fiscal
  diferente do FIAT (ex.: CNHI/IVECCO, que já têm ICMS10/ICMS40/IPITrib/PISNT-Outr conforme §7.2)
  produz XML que bate (`CanonicalDiffer`, diff==0) contra o gabarito real daquele cliente — não
  apenas contra FIAT.
- **Depende de:** A1(b) — a extensão do SWEEP para CNHI/IVECCO/MARELLI usando os GUIDs de mapeador
  já identificados. Sem essa diversidade fiscal real, A2 fica testando o caso FIAT de novo com
  nomes diferentes.
- **Dono:** domínio primário — Lia decide as regras de seleção de variante (conhecimento fiscal +
  leitura do mapeador real via `MapperEmissionGuide`/DSL); Dex entra só se a mudança exigir refactor
  estrutural do `XslGenerator` que fuja de lógica de domínio pura (ex.: assinatura de método, DI).

### 8.2 A3 — Catálogo GUID→XPath (237 LinkMappings)

- **O que deve existir:** novo arquivo `ai/XslSynth/Core/GuidXPathCatalog.cs`, expondo um contrato
  do tipo `IReadOnlyDictionary<string TargetGuid, string XPathNfe>` (ou serviço equivalente,
  `TryResolve(string targetGuid, out string xpath)`), construído rodando o parser da API no layout
  NF-e alvo (mesmo mecanismo que já prova `FLD→Nome→pos` no `jsonGerado`) para extrair a árvore
  `TAG_/GRT_ → path`. Este catálogo é uma das duas fontes que convergem para o mesmo XPath NF-e —
  a outra é o catálogo de leiaute do Excel (P0 original) — e devem se validar mutuamente.
  Ver `poc-excel-generator.md` §5 "P0 — Fechar o catálogo GUID→XPath".
- **Critério de aceite:** o catálogo resolve os 237 `LinkMappings` hoje travados no `XslSynth` (ou
  documenta objetivamente quantos restam sem correspondência e por quê); cruzamento com o catálogo
  Excel não apresenta divergência não-explicada para o layout NF-e usado como referência.
- **Depende de:** apenas o runner funcional (já destravado desde A1/A1 original) — **pode rodar em
  paralelo a A1(b)/A2**, não compete por arquivo com elas (`ai/XslSynth/Core/` vs
  `ai/XslSynth/Excel/`).
- **Dono:** domínio primário — Lia (extração de árvore GUID→XPath é parsing de domínio, não
  infraestrutura); Dex só se for preciso novo endpoint/serviço de suporte na API para rodar o
  parser sobre o layout alvo fora do fluxo normal de request.

### 8.3 A4 — G3: `LayoutSpecExtractor` (2º adaptador de ingestão via Connect Us)

- **O que deve existir:** novo arquivo em `ai/XslSynth/Excel/LayoutSpecExtractor.cs`, implementando
  o mesmo contrato de saída que `ExcelSpecParser.cs` já produz (`SpecModel`), mas com fonte de dados
  o layout/mapeador do Connect Us via `LowCodeRunner` em vez da planilha do cliente. Arquitetura de
  dois adaptadores convergindo para o mesmo `SpecModel` (ver `multi-client-layout-generalization.md`
  §4.3): `ExcelSpecParser` (Fonte A, client-provided) e `LayoutSpecExtractor` (Fonte B,
  client-agnostic real — não depende de o cliente fornecer planilha nenhuma).
- **Critério de aceite:** para o layout FIAT (que tem as duas fontes disponíveis), `LayoutSpecExtractor`
  e `ExcelSpecParser` produzem `SpecModel`s que convergem (mesma validação cruzada de §4.3) — prova
  que o adaptador novo está correto antes de confiar nele para um cliente sem planilha.
- **Depende de:** A1 (modo lote fornece os dados de teste repetíveis — sem `SWEEP`, cada iteração de
  `LayoutSpecExtractor` exigiria rodar o `.exe` manualmente por gabarito). A1 já está pronto; A4 pode
  começar assim que o build de A1 for validado por Lia/Quinn.
- **Dono:** trabalho misto — Lia desenha e implementa a extração de domínio (leitura do
  mapeador/DSL, mapeamento para `SpecModel`); Dex entra para o *wiring* de infraestrutura puro (I/O
  do `LowCodeRunner`, invocação do processo externo, tratamento de falha/timeout do `.exe` seguindo
  o padrão de resiliência do projeto — nunca deixar o `.exe` travar o pipeline). Lia decide via sua
  própria tool Task se delega a parte de wiring a Dex.

### 8.4 A5 — G4: Generalizar `MapperEmissionGuide`

- **O que deve existir:** `ai/XslSynth/Excel/MapperEmissionGuide.cs` deixa de resolver apenas os 8
  campos hardcoded (os que apareceram como SOBRA no único gabarito FIAT disponível quando foi
  escrito) e passa a ser o motor genérico de condicionalidade: para qualquer campo, qualquer
  mapeador, a pergunta "o mapeador real emitiria isto para este documento?" é respondida lendo as
  regras DSL reais (não uma lista estática de 8 nomes). Ver `multi-client-layout-generalization.md`
  §4.4.
- **Critério de aceite:** rodado contra mapeadores de pelo menos 2 clientes adicionais (não apenas
  FIAT), o motor generalizado decide presença/ausência de campo condicional sem precisar de código
  novo por campo — e mantém retrocompatibilidade nos 8 campos originais (regressão contra o gabarito
  FIAT já provado). Nota de gate (@lp-qa/Quinn, já registrada em §4.4): para documentos sem gabarito
  prévio, o gate correto não é `diff==0` posicional, é rastreabilidade — todo campo emitido/omitido
  tem uma regra do mapeador que o justifica.
- **Depende de:** A3 e A4 — generalizar em cima de 1 exemplo é o erro que motivou este item; precisa
  de volume de mapeadores reais (que só A1(b)/A2/A4 produzem) antes de abstrair a regra.
- **Dono:** trabalho misto — Lia lidera (é a mesma linha de raciocínio de domínio fiscal de A2/A4);
  Dex entra apenas se a generalização exigir mudança de assinatura/DI que toque `Program.cs` ou
  outros consumidores fora de `ai/XslSynth/Excel/`.

### 8.5 Sequenciamento consolidado (dado A1 pronto)

```
A1 ✅ (código do modo lote, pronto nesta branch)
  → A1(b) [Lia: estender SWEEP a CNHI/IVECCO/MARELLI]  ⟂ A3 [Lia: GuidXPathCatalog — não competem por arquivo]
      → A2 [Lia+Dex: variantes fiscais, usa gabaritos de A1(b)]
      → A4 [Lia+Dex: LayoutSpecExtractor, usa modo lote de A1]
          → A5 [Lia+Dex: generalizar MapperEmissionGuide, usa volume de A2/A3/A4]
```

Nenhuma fase de A2–A5 toca os mesmos arquivos que B1–B6 (já mergeadas em `master`) — sem risco de
conflito residual. Risco real a monitorar: A2 e A5 tocam `ai/XslSynth/Excel/XslGenerator.cs`/
`MapperEmissionGuide.cs` em sequência, não em paralelo — se Lia decidir rodar A2 e A5 ao mesmo tempo
em sessões distintas, isso precisa de coordenação (não é o desenho recomendado aqui).
