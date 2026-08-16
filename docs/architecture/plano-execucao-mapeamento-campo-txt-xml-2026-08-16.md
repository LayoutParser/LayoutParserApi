# Plano de execução — mapeamento campo TXT ↔ tag XML (PBI #128 / Epic #126)

> Continuação de `resposta-mapeamento-campo-txt-xml-2026-08-16.md` (develop, `408db78`).
> Aqui: qual opção seguir e as fases pra executá-la. Não implementa nada — plano pra
> outros agentes. Formalização em PBI (`@lp-pm`) só depois que o dono confirmar.

## 1. Recomendação

**Opção 3 imediata + Opção 1 como trabalho de fundo, com validação cruzada
comportamental do `.exe` como marco embutido na própria Opção 1.**

> **Atualização 2026-08-16:** a Opção 2 original ("perguntar ao fornecedor
> Sysmiddle/AppConnector se existe modo de saída anotada") foi **descartada pelo dono** —
> não há canal de contato disponível com o fornecedor. Não é substituída por uma "Opção 2"
> nova; ver §1.1 para a razão e o mecanismo que assume esse papel.

Não são alternativas — são sequenciais com propósitos diferentes:

- **Opção 3** desbloqueia o front *agora* com o dado que já existe (granularidade de
  seção/linha via `SegmentMappings`, hoje só no pathway MQSeries — precisa generalizar
  pros outros dois pathways). Não resolve o pedido original, mas é entregável em dias,
  não meses, e o front já pediu explicitamente algo pra não ficar bloqueado.
- **Opção 1** é o único caminho que resolve o pedido original (`fieldMappings` campo a
  campo) sem depender de terceiro. É trabalho real de projeto (promover
  `RealMapperParser` pra runtime), não uma extensão de contrato — vira trilha própria,
  liderada pela Lia. A confiança nessa opção não vem mais de uma fonte externa anotada
  (Opção 2 antiga); vem de comparação comportamental contra o próprio `.exe`, formalizada
  como marco de validação da Fase 2 (ver §1.1 e §3).

**Por que não só Opção 1 (ignorar a 3):** o pedido do front já está em drift há tempo
(ver §1.3 da resposta anterior) e "campo a campo" é um projeto de meses. Entregar seção
primeiro é reduzir o tempo de bloqueio do time de UI sem comprometer a qualidade do
resultado final.

### 1.1 Por que não existe mais uma "Opção 2" separada

Sem canal com o fornecedor, qualquer estratégia de validação de terceiro exigiria
decompilação do `.exe` — fora de cogitação (risco de licenciamento, e o binário é
propriedade do fornecedor, não código que possamos inspecionar). Avaliei três
alternativas que **não** dependem de contato externo:

1. **Engenharia reversa comportamental (adotada).** O `.exe`
  (`LayoutParserLowCodeRunner.exe`) já roda localmente e aceita input controlado (mapper
  conhecido + TXT conhecido). Em vez de perguntar ao fornecedor "existe saída anotada?",
  rodamos os dois lados — nosso `RealMapperParser` promovido e o `.exe` real — sobre a
  mesma amostra de produção, e comparamos **por valor** (onde o valor de um campo aparece
  no XML de saída) contra o que nosso parser aponta como origem. Isso não é uma opção
  paralela e descartável: é o próprio marco de validação que a Fase 2 já precisava ter
  (divergência zero ou explicada antes de avançar pra Fase 3) — só que agora é o
  mecanismo *central* de confiança na Opção 1, não um complemento opcional. Não depende de
  ninguém fora do time: usa apenas artefatos que já temos (o `.exe`, mappers de produção,
  TXTs de amostra, XMLs de saída reais).
2. **Flags de verbosidade/debug do `.exe` já expostas.** Verificação rápida e de baixo
  custo: `LayoutParserLowCodeRunner.exe --help` (ou observação de variáveis de ambiente já
  lidas pelo runner) pode revelar um modo de log mais granular que já ajuda a depurar o
  parser sem esperar ninguém. Vale conferir no início da Fase 1 (minutos, não dias) — se
  não houver nada, descartar sem reabrir investigação.
3. **Documentação já presente localmente.** Antes de assumir que não existe nada
  escrito, checar rapidamente as pastas de exemplos/gabaritos SEFAZ e o "servidor de
  assets" já mapeado em sessões anteriores (`.claude/agent-memory/lp-architect/
  server-assets-inventory.md`) — se houver manual ou spec do Sysmiddle/Mapper ali, é
  ganho de graça. Também é checagem de minutos, não uma trilha de trabalho.

Os itens 2 e 3 são checagens pontuais de baixíssimo custo a rodar no início da Fase 1
(não geram PBI próprio); o item 1 é o que realmente substitui a Opção 2 antiga e por
isso vira parte explícita da Fase 2 (§3), não uma seção separada do plano.

## 2. Diagrama do fluxo proposto

```mermaid
flowchart TB
    subgraph hoje["Hoje (runtime real)"]
        TXT[TXT / MQSeries / IDOC] --> API[LayoutParserApi]
        API --> EXE["LayoutParserLowCodeRunner.exe\n(Sysmiddle, terceiro, fechado)"]
        EXE --> XML[XML final]
        API -->|execute-candidates| FRONT[React front-end]
        XML --> FRONT
    end

    subgraph fase0["Fase 0 — Opção 3 (imediata)"]
        API --> SEG["SegmentMappings generalizado\n(linha/seção, não só MQSeries)"]
        SEG -->|sectionMappings novo campo| FRONT
    end

    subgraph fase1["Fase 1-3 — Opção 1 (trabalho de fundo)"]
        MVO["MapperVO real\n(amostra produção)"] --> PARSER["RealMapperParser\npromovido a runtime\n(ai/XslSynth.Core → novo serviço)"]
        LAYOUT[(Catálogo TargetLayoutGuid → XPath)] --> PARSER
        PARSER --> FM[FieldMapping por request]
        FM -->|"validação cruzada comportamental\n(por valor, amostra >=20 docs reais)"| XML
        FM -->|fieldMappings novo campo| FRONT
    end
```

## 3. Fases de execução

### Fase 0 — Generalizar `SegmentMappings` pros 3 pathways (Opção 3)
- **Título de PBI:** "Expor `sectionMappings` (granularidade de seção/linha) em
  `execute-candidates` nos pathways sysmiddle e tcl-xsl"
- **Dono:** `@lp-backend-dev` (Dex)
- **Escopo:** hoje `SegmentMappings` só é populado por `MqSeriesToXmlTransformer`. Levar o
  mesmo tipo (linha origem → segmento) pros pathways sysmiddle (`LowCodeTransformationService`)
  e tcl-xsl, na medida do que cada um já sabe sem parsear `MapperVO`. Renomear o campo
  exposto ao contrato pra `sectionMappings` (evitar confundir com `fieldMappings` futuro,
  que é granularidade diferente).
- **Critério de aceite:** os 3 pathways retornam `sectionMappings` não-nulo quando
  aplicável; contrato documentado como granularidade de linha/seção, explicitamente NÃO
  campo; front consegue destacar bloco de origem no TXT a partir da resposta.
- **Marco de validação:** `@lp-qa` (Quinn) confere, numa amostra de cada pathway, que o
  número de linha reportado bate com a seção real do TXT de entrada.
- **Dono de doc:** `@lp-doc` (Duda) atualiza o contrato HTTP documentado.

### Fase 1 — Confirmar shape real do `MapperVO` + decidir dono do parser
- **Título de PBI:** "Investigação: shape completo do `MapperVO` de produção e decisão de
  arquitetura do parser de runtime"
- **Dono:** `@lp-parser-llm` (Lia)
- **Escopo:** ler amostra de produção completa do `MapperVO` (não a amostra de síntese
  offline já usada por `RealMapperParser`); confirmar campos citados pelo front
  (`IsStaticValue`, `StaticValue`, `IsPositionalGroupRepetition`, `MinimalOccurrence`/
  `MaximumOccurrence`); decidir se `RealMapperParser` é promovido ou se nasce um parser
  novo dedicado a esse propósito (evitar acoplar a trilha de síntese offline a um serviço
  de runtime com SLA de request).
- **Critério de aceite:** documento com shape confirmado campo a campo + decisão
  registrada (promover vs. novo parser) + confirmação se `tcl-xsl` usa ou não `MapperVO`
  (pergunta em aberto da resposta anterior).
- **Marco de validação:** revisão por `@lp-architect` antes de Fase 2 começar — não avança
  sem essa confirmação (é o ponto que "muda o shape depois de publicado quebra o front").

### Fase 2 — Catálogo `TargetLayoutGuid` → XPath + resolução N:1 e grupos repetidos
- **Título de PBI:** "Construir catálogo GUID→XPath do layout de destino e resolver
  granularidade N:1 / ocorrência de grupo no parser de runtime"
- **Dono:** `@lp-parser-llm` (Lia), com apoio de `@lp-backend-dev` pra persistência do
  catálogo (provavelmente cache Redis + fallback SQL, seguindo o padrão de resiliência
  já usado no projeto)
- **Escopo:** resolver `TargetElementGuid` contra `TargetLayoutGuid` real (não mais
  heurística textual de `T.<path>`); parsear DSL além do regex atual pra extrair todas as
  origens `I.<Linha>/<Campo>` de uma rule (não só o destino); usar a ocorrência física real
  do parse (já calculada pela API — reaproveitar o fix pendente de
  `line-repetition-position-bug`, não a versão quebrada) pro índice de grupo repetido.
- **Critério de aceite:** parser de runtime produz `FieldMapping[]` completo (origem N,
  destino 1, incluindo valor estático `null` explícito) para um `MapperVO` de amostra.
- **Marco de validação (é a substituição da Opção 2 antiga, ver §1.1):** engenharia
  reversa comportamental — rodar `RealMapperParser` promovido e o `LayoutParserLowCodeRunner.exe`
  real sobre a mesma amostra de produção (mapper conhecido + TXT conhecido) e comparar
  **por valor** (onde o valor de cada campo aparece no XML de saída real) contra a origem
  que nosso parser aponta. Amostra de pelo menos 20 documentos reais cobrindo os 3 tipos
  de origem (TXT/MQSeries/IDOC) — só avança pra Fase 3 se divergência for zero ou
  explicada. Não depende de nenhum contato externo com o fornecedor.

### Fase 3 — Expor `fieldMappings` no contrato HTTP
- **Título de PBI:** "Adicionar `fieldMappings` opcional em `execute-candidates`,
  alimentado pelo parser de runtime da Fase 2"
- **Dono:** `@lp-backend-dev` (Dex)
- **Escopo:** integrar o parser promovido no `TransformationExecutionController`, como
  chamada adicional (não substitui o `.exe`, que continua gerando o XML real) — nulo/opcional
  se o pathway não suportar (ex.: se Fase 1 confirmar que `tcl-xsl` não usa `MapperVO`).
- **Critério de aceite:** contrato aceito pelo front, `fieldMappings` presente quando
  aplicável, tempo de resposta de `execute-candidates` não degrada de forma perceptível
  (resolução roda em paralelo/cache, não bloqueia a resposta principal — seguir o padrão
  de resiliência do projeto).
- **Marco de validação:** `@lp-qa` valida contrato + regressão de performance;
  `@lp-doc` atualiza Swagger e o contrato documentado pro front.

## 4. Riscos explícitos

| Risco | Mitigação / plano B |
|---|---|
| Fase 2 (catálogo + DSL) é maior do que parece — DSL condicional pode ter ramificações não cobertas por regex simples | Escopar Fase 2 como spike com timebox antes de comprometer prazo; se DSL for arbitrariamente complexa, considerar interpretador real (parser de expressão) em vez de regex incremental |
| Divergência entre nosso parser e o `.exe` real (duas fontes da verdade) — agora o único juiz de confiança, já que não há fonte externa anotada (Opção 2 descartada) | Marco de validação da Fase 2 é bloqueante — não avança pra Fase 3 sem amostra validada; se divergência persistir, `fieldMappings` sai marcado como "best-effort", não fonte oficial |
| `tcl-xsl` não usa `MapperVO` (confirmação pendente da Fase 1) | Fase 3 já prevê `fieldMappings` opcional/nulo nesse pathway — não é bloqueio, é escopo reduzido |
| Amostra de validação comportamental (Fase 2) não cobre um caso de borda real de produção (ex.: grupo repetido raro, DSL condicional pouco comum) | Ampliar a amostra incrementalmente conforme casos de borda aparecerem em produção; não é bloqueio de lançamento, é item de acompanhamento pós-Fase 3 |
| Fase 0 (Opção 3) cria expectativa no front de que "seção" é suficiente e a pressão por `fieldMappings` esfria, mascarando a real necessidade | Comunicar explicitamente ao front, junto da entrega da Fase 0, que é uma etapa intermediária — a Fase 3 continua no roadmap |

## 5. Próximo passo

Confirmação do dono sobre a recomendação (Opção 3 imediata + Opção 1 de fundo, com
validação cruzada comportamental do `.exe` como marco da Fase 2) antes de `@lp-pm`
formalizar as fases acima como PBIs no board.
