# Plano de Execução Multi-Sessão — Trilha A (CLI) × Trilha B (Windows UI)

> **Autor:** @lp-architect (Aria) · **Status:** Ativo · **Data:** 2026-07-15
> **Contexto:** o usuário opera **duas sessões Claude Code na mesma máquina** (mesmo disco C:, mesma pasta do
> repo): esta sessão CLI (WSL) e uma sessão Windows Claude Code UI nativa. Objetivo: dividir o trabalho
> pendente (ver [`poc-excel-generator.md`](poc-excel-generator.md) §11 e
> [`multi-client-layout-generalization.md`](multi-client-layout-generalization.md) §5) em duas trilhas
> paralelas, sem as sessões pisarem uma na outra.
> **Este documento é o ponto de entrada para a Sessão B** — leia-o primeiro antes de tocar em qualquer arquivo.

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

## 6. Estado agora (2026-07-15)

- Branch `feat/multi-client-track-b` criada localmente, sincronizada com o HEAD atual desta sessão.
- Trilha A (esta sessão) segue para **A1 — modo lote do runner**.
- Trilha B fica livre para a sessão Windows começar por **B1** (não depende de nada, G0 já fechado) assim que
  o usuário abrir aquela sessão e fizer o checkout.
