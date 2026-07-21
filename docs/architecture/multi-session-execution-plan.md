# Plano de Execução Multi-Sessão — Trilha A (CLI) × Trilha B (Windows UI)

> **Autor:** @lp-architect (Aria) · **Status:** Trilha B COMPLETA (merged) · Trilha A: A1 revisado/confirmado, A3 quase completa (235/237), A2 parcial (1/2 clientes), A5 já satisfeita na sintaxe (ressalva de validação), A4 bloqueada por dado externo — ver §8 (revisado) e §9 · **Data:** 2026-07-15 (criação) / 2026-07-16 (auditoria de reconciliação §7 + especificação A2–A5 §8) / 2026-07-17 (revisão pós-execução real por Lia em `feat/lowcode-batch-mode`, §8 corrigida + §9 veredito)
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

### 8.1 A2 — Generalização além do par único (ICMS10-90/CSOSN, grupos especiais) · **PARCIAL (1/2)**

> **Atualização 2026-07-17 (Aria, pós-execução real por Lia em `feat/lowcode-batch-mode`):** a
> premissa "único gabarito FIAT como verdade" já não procede — `XslGenerator.cs` **já generaliza por
> CST** (ICMS 00-90, IPITrib/IPINT, PIS/COFINS Aliq/Qtde/NT/Outr) antes desta rodada. O que faltava
> era prova empírica contra clientes com regime diferente. Resultado real: **CNHI fecha limpo**
> (FALTA=0/SOBRA=0/TEXTO=0, critério de aceite atingido). **IVECCO não fecha** — gaps diagnosticados,
> não são "falta de generalização de CST", são bugs/lacunas específicos:
> - `normalize-space()` colapsa espaço duplo que o gabarito real preserva;
> - homônimos `xPed`/`nItemPed` mal-selecionados (colisão de nome de campo entre grupos);
> - `dest/enderDest/fone` ausente na saída;
> - grupo `IBSCBSTot` (Reforma Tributária, layout novo) sem suporte ainda;
> - convenção de separador do `infCpl` varia por cliente e não está parametrizada.
>
> **Critério de aceite original ("≥2 clientes, diff==0") fica 1 de 2 — NÃO fechado.** Não é uma nova
> rodada de "generalização de CST" (isso já existia); é uma lista curta e concreta de 5 correções
> pontuais em IVECCO. Reclassifico A2 como fase de bugfix dirigido a gap, não mais de arquitetura de
> generalização.
- **Critério de aceite revisado:** os 5 itens acima corrigidos e IVECCO fechando diff==0 nos 2
  documentos testados, sem regressão em CNHI/FIAT.
- **Depende de:** nada além do que já está disponível (gabaritos de IVECCO já existem). Pronto para
  retomada imediata.
- **Dono:** Lia (leitura de mapeador/DSL para resolver os homônimos e o separador; decisão fiscal
  sobre `IBSCBSTot`); Dex só se `normalize-space()` exigir mudança estrutural na serialização XML.

### 8.2 A3 — Catálogo GUID→XPath (237 LinkMappings) · **PRATICAMENTE COMPLETA (235/237)**

> **Atualização 2026-07-17:** implementada e integrada. `ai/XslSynth/Core/GuidXPathCatalog.cs`
> existe, plugado em `LinkMappingTranspiler.cs` e `Program.cs` com degradação graciosa (funciona sem
> o catálogo, melhora com ele — alinhado ao princípio de resiliência do projeto). Achado-chave:
> `Documentos/Layout/layout-nfe.xml` é o LayoutVO real do Connect Us cujo `LayoutGuid` bate com o
> `TargetLayoutGuid` do mapeador SEND_ENV — a peça que faltava para resolver a árvore
> `TAG_/GRT_ → path` contra o layout certo. **235/237 LinkMappings resolvidos**; os 2 restantes são
> destino `ATT_`/atributo, exclusão por design documentada (não é gap, é fora de escopo do catálogo).
> Confirmado sem regressão em A2/CNHI após a integração. Dois bugs reais corrigidos no próprio código
> antes de fechar: encoding declarado UTF-16 vs real UTF-8; pretty-print quebrando
> `<ElementGuid>`/`<Name>` por falta de `.Trim()` — **ver §9 sobre necessidade de revisão desses
> fixes** antes de considerar A3 100% encerrada.
- **Critério de aceite:** atingido (235/237 + justificativa documentada dos 2 restantes). Falta
  apenas: (a) confirmar os 2 fixes de encoding/trim sob revisão de `@lp-qa`; (b) validar o catálogo
  contra o layout de ao menos 1 cliente não-FIAT quando A4 destravar (mesmo mecanismo serve de
  segunda prova).
- **Depende de:** nada — já rodou.
- **Dono:** Lia (concluído); revisão pontual de Quinn recomendada (ver §9).

### 8.3 A4 — G3: `LayoutSpecExtractor` (2º adaptador de ingestão via Connect Us) · **BLOQUEADA — dado externo**

> **Atualização 2026-07-17:** bloqueio real, não falta de esforço. Lia precisa do LayoutVO de
> **ENTRADA** real (`InputLayoutGuid = LAY_ad4fb6f4-…`) exportado do Connect Us. O único arquivo local
> parecido, `layout-mqseries.xml`, tem `LayoutGuid` diferente — não é o mesmo layout, usá-lo produziria
> um `SpecModel` que valida contra o layout errado (falso-positivo silencioso). Lia recusou
> implementar com dado fictício, corretamente — é a mesma classe de erro que motivou registrar A5
> como "não generalizar em cima de 1 exemplo só" (§8.4). Decisão correta, mantenho.
- **Ação concreta que precisa do usuário:** exportar do Connect Us o LayoutVO cujo `LayoutGuid` seja
  `LAY_ad4fb6f4-…` (o mesmo GUID hoje referenciado como `InputLayoutGuid` pelo mapeador em uso) e
  colocá-lo em `Documentos/Layout/` (mesmo padrão de `layout-nfe.xml`, que destravou A3). Sem esse
  arquivo, A4 não tem como progredir — não é um problema de código.
- **Critério de aceite:** inalterado (convergência `LayoutSpecExtractor` × `ExcelSpecParser` para o
  layout FIAT).
- **Depende de:** exportação do LayoutVO de entrada pelo usuário/Connect Us. Escalo esse pedido
  explicitamente — ver §9.
- **Dono:** Lia (domínio) + Dex (wiring), assim que o dado existir.

### 8.4 A5 — G4: Generalizar `MapperEmissionGuide` · **JÁ SATISFEITA NA SINTAXE — premissa original desatualizada**

> **Atualização 2026-07-17 — correção da minha própria especificação anterior.** A premissa "resolve
> apenas os 8 campos hardcoded" estava **desatualizada**, não errada quando escrita: no estado atual
> da branch, `MapperEmissionGuide.Load(mapperVoPath)` já é parametrizado por **qualquer** `MapperVO`
> (não FIAT fixo) e extrai, via regex sobre a DSL real (`if(#.x != 0) begin … end`), **todos** os
> `T.path` sob essa guarda — não uma lista estática de 8 nomes. Isso já foi exercitado contra CNHI em
> A2 (parte do 0/0/0). Ou seja: **a generalização de SINTAXE DSL está feita e em uso real
> multi-cliente.**
>
> **Distinção que Lia apontou corretamente e que precisa ficar registrada:** o motor cobre um único
> padrão sintático — guarda `!= 0`, bloco `begin...end` não aninhado (a regex é `Singleline` +
> lazy `.*?`, documentada como assumindo "os corpos reais não aninham outro begin/end"). Isso é
> **genérico na sintaxe DSL para esse padrão**, mas **não é** um motor genérico de condicionalidade
> fiscal arbitrária — não cobre, por exemplo, comparações diferentes de `!= 0` (`> 0`, `IsNullOrEmpty`
> como guarda principal), múltiplas condições combinadas, ou blocos aninhados. A visão original da
> §8.4 (motor genérico de condicionalidade *qualquer que seja a forma da regra*) segue sendo mais
> ampla do que o que existe — mas não há evidência ainda de que essa amplitude seja *necessária*: os
> mapeadores reais analisados até aqui (FIAT, CNHI) usam exatamente esse padrão.
- **Critério de aceite revisado (substitui o original):** A5 está **concluída para o padrão de guarda
  `!= 0` não-aninhado**, validado em 2 clientes (FIAT retrocompat + CNHI real). Falta, para fechar
  com confiança total: (a) rodar contra IVECCO/MARELLI decriptografados e confirmar que nenhum deles
  usa um padrão de guarda fora do coberto (`> 0`, aninhamento, `IsNullOrEmpty` como guarda de
  emissão) — se algum usar, essa é uma extensão pontual da regex/parser, não uma reescrita; (b) se
  nenhum padrão novo aparecer, fechar A5 como está e retirar a nota de "motor genérico de
  condicionalidade" do vocabulário do plano — o nome correto do que existe é "extrator de guardas
  `!=0` por mapeador", que é suficiente para o problema real observado até agora.
- **Depende de:** nada para a validação com CNHI/IVECCO/MARELLI (dados já existem uma vez A2/A1(b)
  fecharem). Não depende mais de A3/A4 como a especificação original supunha — essa dependência foi
  outra premissa que não se confirmou (A5 não precisava do catálogo GUID→XPath nem do
  `LayoutSpecExtractor` para o que faz hoje).
- **Dono:** Lia — validação de padrão contra os 3 mapeadores decriptografados restantes; sem
  necessidade de Dex a menos que apareça um padrão de guarda que exija nova estrutura de regex/AST.

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

---

## 9. Veredito da Trilha A (2026-07-17, Aria) — pós-execução real por Lia em `feat/lowcode-batch-mode`

**Estado objetivo, por fase:**

| Fase | Veredito | Nota |
|---|---|---|
| A1 | ✅ **Confirmado com dado real** | SWEEP rodou contra os 4 clientes de `Examples/` (FIAT 280/280, CNHI 113/113, IVECCO 68/68, MARELLI 8/8 — todos bem-formados). Achado colateral: bug intermitente de interop no runner (1ª chamada pós-cópia do `.exe` não distingue `SWEEP`), sem causa-raiz — ver nota de risco abaixo. |
| A2 | 🟡 **Parcial (1/2)** | CNHI fecha diff==0; IVECCO tem 5 gaps concretos e diagnosticados (não é falha de arquitetura — são bugfixes pontuais). Reclassificada em §8.1. |
| A3 | ✅ **Praticamente completa (235/237)** | Os 2 restantes são exclusão por design. Dois bugs de código corrigidos pela própria Lia durante a implementação — recomendo revisão por Quinn antes de considerar 100% fechada (ver abaixo). |
| A4 | 🔴 **Bloqueada — dado externo, não código** | Falta o LayoutVO de entrada real (`LAY_ad4fb6f4-…`) do Connect Us. Lia recusou corretamente implementar com dado fictício. **Ação do usuário necessária** (ver abaixo). |
| A5 | ✅ **Satisfeita para o padrão observado, com ressalva de cobertura** | A premissa "8 campos hardcoded" da minha spec original estava desatualizada — o motor já é genérico por mapeador/path para o padrão de guarda `!= 0`. Falta só validar contra IVECCO/MARELLI para descartar padrões de guarda não cobertos. Reclassificada em §8.4. |

**Leitura geral:** a Trilha A avançou mais do que a especificação original previa (A3 e A5 estavam
listadas como dependentes de A1(b)/A3/A4, mas ambas progrediram independentemente e sem esperar
essas dependências — a spec de §8 estava conservadora demais no sequenciamento). O único bloqueio
real e não-negociável é A4, que depende de um artefato externo que ninguém no repo pode produzir.
A2 e A5 têm escopo residual claro e pequeno (bugfixes/validação dirigida), não redesenho.

**O que merece revisão mais profunda antes de prosseguir (não é bloqueio, é recomendação de gate):**

1. **Os 2 bugs corrigidos por Lia em A3** (encoding UTF-16 declarado vs UTF-8 real; `.Trim()` faltando
   em `<ElementGuid>`/`<Name>` no pretty-print) — foram corrigidos como efeito colateral de fechar
   A3, sob pressão de "fazer o catálogo funcionar", não como item de trabalho isolado com teste
   dedicado. Risco: podem ter efeito em outros consumidores do mesmo parser/serializer fora do
   escopo de A3 que não foram exercitados pelo SWEEP. **Recomendo passagem por `@lp-qa` (Quinn)**
   focada nesses dois pontos antes do merge para `master`, não um re-review geral de A1-A5.
2. **O comportamento intermitente do runner em A1** (1ª invocação pós-cópia do `.exe` falha a
   distinguir `SWEEP`) — Lia reproduziu mas não achou causa-raiz, atribuindo a interop WSL/PE. Isso
   é aceitável para uso manual (basta descartar a 1ª chamada), mas é um risco real se A4 ou qualquer
   automação futura invocar o runner de forma não-interativa (ex.: CI, script sem retry). Não bloqueia
   nada hoje — **registro para não esquecer se alguém tentar automatizar o SWEEP sem look para esse
   caso**.

**O que precisa do usuário agora (único item de ação concreta, para desbloquear A4):**

Exportar do Connect Us o **LayoutVO de entrada** cujo `LayoutGuid` é `LAY_ad4fb6f4-…` (o mesmo GUID
referenciado como `InputLayoutGuid` pelo mapeador em uso) e disponibilizá-lo em `Documentos/Layout/`
— mesmo padrão de onde `layout-nfe.xml` já vive (que destravou A3). Sem esse arquivo, A4 fica parada;
nenhuma outra ação de código resolve isso.

**Nada aqui foi enviado (`git push`) nem tocado em branch/CI** — a atualização de §8/§9 é local, na
branch atual do working tree, via commit deste documento.

---

## 10. Nova fase — A6: Proveniência (sidecar) em `DslRuleTranslator` (2026-07-21, Aria)

> **Contexto:** pedido do dono do projeto, fora desta trilha (sessão nova, não relacionada a A1-A5), para
> instrumentar `DslRuleTranslator.TranslateAsync` (`ai/XslSynth/Synthesis/`) emitindo um sidecar JSON
> (`generated-provenance.json`, versionado, gerado offline junto do XSLT) mapeando XPath de saída →
> {regra, fonte, campos de input}. Decisão de não embutir isso no XML de saída (risco de conformidade de
> schema SEFAZ) já está fechada. Registro aqui só para não colidir com o trabalho ativo de Lia no mesmo
> subsistema — ver auditoria de reconciliação §7 e spec §8.

| # | Fase | Arquivos tocados | Depende de | Dono |
|---|---|---|---|---|
| A6 | Emitir sidecar de proveniência no loop de tradução DSL→XSLT **+ no transpilador de LinkMappings (escopo corrigido, ver ⚠️ abaixo)** | `ai/XslSynth/Synthesis/DslRuleTranslator.cs`, `ai/XslSynth/Core/LinkMappingTranspiler.cs` (+ novo arquivo de emissão do sidecar) | nada — pode rodar em paralelo ao restante de A2 (arquivo diferente) | Lia (mesmo subsistema/dono das fases A1-A5) |

**Sequenciamento recomendado:** A6 pode rodar em paralelo ao restante de A2 (arquivos diferentes, sem
conflito previsto — A2 toca `Excel/XslGenerator.cs`/bugs de serialização IVECCO, A6 toca
`Synthesis/DslRuleTranslator.cs`+`Core/LinkMappingTranspiler.cs`), mas é a mesma dona (Lia) — ela decide a
ordem real de execução dentro da própria fila. Não depende de A3/A4/A5.

**Motivação de negócio (fora desta trilha):** o sidecar alimenta um loop de diagnóstico de erro XSD via
Ollama (cruza XPath do erro × proveniência → prompt cirúrgico) que substitui a chamada Gemini hoje em
`XmlAnalysisController.AnalyzeXsdErrorWithAi` — decisão de decomissionar Gemini/OpenAI tomada na mesma
sessão (ver memória de `@lp-architect`: `gemini-openai-decommission-decision`). O consumidor do sidecar (o
novo serviço Ollama em `Services/`) é trabalho separado, fora de `ai/XslSynth/`, fora desta trilha. Detalhe
completo da visão de negócio (diagnóstico semântico fiscal, filtro de assinatura, substituição do
AppConnector) em [`ia-fiscal-diagnosis-vision.md`](ia-fiscal-diagnosis-vision.md).

**⚠️ Correção de escopo (2026-07-21, mesmo dia, revisão da Aria):** o escopo original desta fase (só
`DslRuleTranslator`) cobre apenas os ~98 campos via regra DSL — **não** os ~237 campos via `LinkMapping`
direto (a maioria), que passam por `LinkMappingTranspiler.cs` e hoje **não emitem sidecar nenhum** — em vez
disso, embutem um `XComment` (`target=... input=... xpath=...`) como filho do próprio elemento de saída,
dentro do XSLT. Isso sobrevive à transformação (não é instrução `xsl:`, é filho literal do resultado) —
**exatamente o anti-padrão que a decisão original de proveniência queria evitar** (nunca embutir rastro no
XML de produção). A6 precisa cobrir os dois mecanismos, e precisa de um passo de "publicação" (ainda
inexistente) que remova esse comentário de debug antes de qualquer XSLT ir pra `Mapper.XslContent`. Detalhe
completo em `ia-fiscal-diagnosis-vision.md` §3.1.

*Adicionado por `@lp-architect` (Aria), 2026-07-21 — fora do ciclo normal de auditoria de A1-A5 (§7-9), a
pedido do dono do projeto numa sessão paralela. Local apenas, não enviado (`git push`).*
