# Dispatch — Roadmap de IA consolidado (sessão 2026-07-21)

> **PT-BR** · Ponto de entrada único para `@lp-backend-dev` (Dex) e `@lp-parser-llm` (Lia) executarem o que
> foi decidido nesta sessão, sem precisar reconstruir o histórico de conversa. Cada item tem: o quê, dono,
> depende de, e onde ler o racional completo (não repetido aqui).
>
> **Autor:** `@lp-architect` (Aria) · **Natureza:** plano de execução — nada aqui é código de produção.
> **Docs de referência (racional completo):** [`ia-xslt-synthesis.md`](ia-xslt-synthesis.md) (síntese de
> XSLT), [`multi-session-execution-plan.md`](multi-session-execution-plan.md) (Trilha A, A1-A6),
> [`ia-fiscal-diagnosis-vision.md`](ia-fiscal-diagnosis-vision.md) (diagnóstico fiscal, CFOP, AppConnector).
> **Memória de arquitetura relevante:** `.claude/agent-memory/lp-architect/` — `transformation-pathway-
> duplication.md`, `frontend-transformation-tab-built.md`, `xslsynth-trilha-a-overlap.md`, `gemini-cloud-
> xsd-diagnosis-gap.md`, `gemini-openai-decommission-decision.md`, `dev-machine-gpu-constraints.md`,
> `ia-fiscal-diagnosis-vision.md`.

---

## Como usar este documento

- Cada item é uma unidade de trabalho dispatchável, não uma decisão em aberto.
- "Bloqueado por" = não começar sem resolver isso primeiro (a maioria dos bloqueios é do usuário/infra, não
  de código).
- Onde eu já tenho opinião mas não decidi sozinha (ex.: remoção definitiva de código, rotação de segredo),
  está marcado explicitamente com o agente que tem autoridade real.

---

## Grupo 1 — Decommission Gemini/OpenAI

| # | Item | Dono | Depende de | Detalhe |
|---|------|------|------------|---------|
| 1.1 | Remover `Services/Generation/Implementations/AIService.cs` + `Services/Generation/Interfaces/IAIService.cs` (OpenAI) + seção `OpenAI` do `appsettings.json` | Dex | nada — confirmado zero consumidores em todo o repo | `gemini-cloud-xsd-diagnosis-gap.md` |
| 1.2 | Decidir substituto dos 3 consumidores de `GeminiAIService` ANTES de apagá-lo: `XmlAnalysisController.AnalyzeXsdErrorWithAi` (→ Grupo 3), `SyntheticDataGeneratorService.GenerateWithGeminiAsync` (→ Grupo 4), `SemanticAIGenerator` (→ Grupo 4) | Dex + Lia | Grupos 3 e 4 terem destino concreto (nem que seja "usar só `GenerateWithRulesAsync` por enquanto") | idem |
| 1.3 | Só depois de 1.2: apagar `GeminiAIService.cs` + seção `Gemini` do `appsettings.json` | Dex | 1.2 | idem |
| 1.4 | Achado incidental (bug pré-existente, NÃO relacionado à decisão de nuvem): `RAGController`/`RAGService` também não sobem hoje (DI não registrado) — `RAGService` não tem dependência de nuvem, é retrieval puro. Corrigir oportunisticamente, sem pressa | Dex | nada | idem |
| 1.5 | Atualizar documentação (README 8 pontos, `security.md` 5 pontos, `.claude/CLAUDE.md` 1 ponto) — considerar generalizar a regra de `security.md` de "Gemini/OpenAI" pra "qualquer LLM em nuvem" | `@lp-doc` (Duda) | 1.3 (código já removido) | idem |
| 1.6 | Revogar/desprovisionar a API key do Gemini (já marcada comprometida, rotação pendente) — com a decisão de decommission, vira "revogar de vez", não "gerar chave nova". Rotação da senha SQL é assunto **não relacionado**, continua pendente à parte | `@lp-devops` (Gage) | nada | `gemini-openai-decommission-decision.md` |

---

## Grupo 2 — Pathway canônico de transformação

| # | Item | Dono | Depende de | Detalhe |
|---|------|------|------------|---------|
| 2.1 | **Decidido:** `TransformationExecutionController`/`TransformationPipelineService` (Pathway 2) é canônico — é o que o front-end já chama. `TransformationController`/`MapperTransformationService` (Pathway 1) sem novo investimento; candidato a deprecação (decisão final de remover = usuário/`@lp-devops`) | Dex | nada | `transformation-pathway-duplication.md` |
| 2.2 | Validação XSD (pré-requisito do Grupo 3) entra na **camada de serviço** (`TransformationPipelineService`/`TransformationValidatorService`), não no controller — Pathway 1 fazia isso no controller, é o padrão a NÃO copiar (viola `dotnet-standards.md`) | Dex | 2.1 | idem |

---

## Grupo 3 — Diagnóstico de erro XSD via Ollama (near-term)

| # | Item | Dono | Depende de | Detalhe |
|---|------|------|------------|---------|
| 3.1 | Validação determinística de campo de input (tamanho/formato/checksum contra o `LengthField` já declarado no Layout XML) — pega casos tipo CHAVEACESSO **sem IA nenhuma** | Dex | nada — pode começar já | `ia-fiscal-diagnosis-vision.md` §3.2 |
| 3.2 | Filtro de erro conhecido-e-aceito (assinatura digital ausente) — classificador determinístico, roda ANTES de qualquer explicação, obrigatório pro loop não virar ruído repetido em 100% dos documentos | Dex | nada — pode começar já | idem §3.3 |
| 3.3 | Sidecar de proveniência — ver Grupo 5 (A6 da Trilha A); é pré-requisito pro prompt cirúrgico do item 3.5 | Lia | ver Grupo 5 | `xslsynth-trilha-a-overlap.md` |
| 3.4 | Novo serviço Ollama real em `Services/` (padrão de resiliência do projeto: dependência externa opcional, try/catch, degrada graciosamente), acionado só sob demanda quando 3.1/3.2 não explicam o erro | Dex (wiring) + Lia (prompt/domínio) | 3.1, 3.2, 3.3 prontos (ou ao menos 3.1/3.2 — 3.4 pode nascer sem sidecar ainda, com prompt menos cirúrgico, e melhorar depois) | `gemini-openai-decommission-decision.md` |
| 3.5 | Expor via `XmlAnalysisController` e/ou `TransformationExecutionController` (Pathway 2, Grupo 2) — conferir contrato existente antes de desenhar endpoint novo | Dex | 2.1, 3.4 | — |
| 3.6 | **Modelo/tamanho: FECHADO (2026-07-21).** Servidor de produção `BRNDDAPPBLD01` — Intel i7-4790 (Haswell 2014, 4c/8t, AVX2 sem AVX-512), 32GB RAM, sem GPU confirmada, sem upgrade previsto. Mirar **1-2B params** (não 2-4B) como ponto de partida; **medir tok/s real nesse servidor antes de comprometer** (não há benchmark desta CPU específica); compilar/configurar mirando AVX2. Timeout/degradação graciosa vira mais crítico (mostrar erro cru se a IA demorar demais); cache por assinatura de erro como mitigação de custo. Suspeita não confirmada: pode ser a mesma máquina do runner CI/espelho dev — perguntar antes de assumir capacidade dedicada | Dex | nada — pode implementar já com a recomendação conservadora | `production-server-hardware.md` |
| 3.7 | Front-end: a aba "XML Transformação Final" **já existe** (`AnalysisModeTabs`/`Tabs`/`XmlTransformationDisplay`, wired no `l-bottom-right`) — não escrever prompt pro `lp-front-dev` pra "criar a aba". Só falta estender `XmlTransformationDisplay` (ou componente irmão) pra acionar/mostrar o diagnóstico quando a validação falhar — e só depois que 3.4/3.5 tiverem contrato de endpoint estável | Dex → `lp-front-dev` depois | 3.4, 3.5, 3.6 | `frontend-transformation-tab-built.md` |

---

## Grupo 4 — Geração sintética de dado posicional/EDI (caso à parte do Grupo 3)

| # | Item | Dono | Depende de | Detalhe |
|---|------|------|------------|---------|
| 4.1 | **FECHADO (2026-07-21):** dado sintético serve pra "criar uma IA específica em documentos fiscais" — transcende fixture-de-teste vs corpus-RAG, é sobre alimentar a visão de `ia-fiscal-diagnosis-vision.md`. **Reconciliado com o hardware (item 3.6/4.5): perseguir isso via RAG-enrichment (indexar cenários fiscais rotulados pra recuperação em tempo de inferência), não via treino de modelo** — mesmo princípio RAG-não-fine-tuning do resto do projeto, e o único caminho viável dado que o servidor de produção não tem GPU | — | resolvido | `gemini-openai-decommission-decision.md` |
| 4.2 | Nível 1 (ponto de partida confirmado): Faker/Mimesis (MIT, provider `pt_BR` com CPF/CNPJ) pra valor por campo realista **+ variação deliberada de cenário fiscal** (ex.: gerar documento de devolução com CFOP de venda, rotulado como incorreto) — vira o corpus que alimenta o RAG do item 4.1, não só fixture descartável | Lia + Dex | nada — pode começar já | idem |
| 4.3 | Nível 2 (modelagem generativa pesada): **rebaixado de prioridade** — o hardware real torna treino impraticável, e a reconciliação do 4.1 mostra que RAG-enrichment (nível 1 ampliado) atende o objetivo sem precisar disso. Manter candidatos pesquisados como referência caso a ambição cresça além de CFOP: SDV (migrou pra Business Source License, não serve pro critério Apache/MIT pedido), CTGAN/SynthCity (licença não confirmada), Gretel (comprado pela NVIDIA em 2025, risco de abandono), GReaT/REaLTabFormer (mais próximo de "clonar e treinar nossa versão", não confirmado nesta sessão) | — | não iniciar sem nova justificativa — nível 1+RAG é o caminho atual | idem |
| 4.4 | Requisito de design obrigatório (vale pro corpus RAG do 4.1/4.2 também): checagem de near-duplicate entre saída "sintética" e o corpus real — este projeto é material de TCC, "sintético" tende a circular mais solto que dado real controlado | Dex + Lia | nada | idem |
| 4.5 | Treinar/fine-tunar continua precisando de GPU que o servidor de produção não tem (confirmado, não mais hipotético) — reforça por que 4.1 foi resolvido via RAG, não treino | — | resolvido (motivo do 4.1) | `production-server-hardware.md` |

---

## Grupo 5 — Trilha A: síntese de XSLT (já em andamento — ver `multi-session-execution-plan.md`)

Não repito status de A1-A5 aqui (rastreado lá, com veredito por fase em §9 daquele doc). Only o que mudou
nesta sessão:

| # | Item | Dono | Depende de | Detalhe |
|---|------|------|------------|---------|
| 5.1 | **A6 (proveniência), escopo corrigido:** cobrir `LinkMappingTranspiler.cs` **e** `DslRuleTranslator.cs` (não só o segundo — a maioria dos campos, incluindo o exemplo real da CHAVEACESSO, são LinkMapping, confirmado pelo usuário) | Lia | nada — pode rodar em paralelo ao A2 residual | `multi-session-execution-plan.md` §10, `ia-fiscal-diagnosis-vision.md` §3.1 |
| 5.2 | Passo de "publicação" (ainda inexistente): remover o `XComment` de debug (`target=... input=... xpath=...`) que `LinkMappingTranspiler` hoje embute no XSLT antes de qualquer promoção pra `Mapper.XslContent` — sobrevive à transformação, é o anti-padrão que a decisão de proveniência queria evitar | Lia + Dex | 5.1 | idem |
| 5.3 | Critério de aceite ampliado: qualquer XSLT candidato a produção precisa replicar os pós-processamentos que `tools/LowCodeRunner/SysmiddleMapperExecutor.cs` já documenta do AppConnector real (escape de `<`/`>` em `infCpl`/`infAdFisco`/`infAdProd`) | Lia | mid-term, antes do long-term (Grupo 6 do doc fiscal) | `ia-fiscal-diagnosis-vision.md` §5 |
| 5.4 | Lembrete já documentado, mais urgente agora: engine `XslCompiledTransform` (XSLT 1.0) vs Saxon (XSLT 2.0/3.0) — decisão de arquitetura pendente | Aria (decisão) quando chegar a hora | nada urgente ainda | `ia-xslt-synthesis.md` §8 |

---

## Grupo 6 — Diagnóstico fiscal semântico (CFOP) — workstream novo, greenfield confirmado

| # | Item | Dono | Depende de | Detalhe |
|---|------|------|------------|---------|
| 6.1 | Construir base CFOP × tipo de operação indexada, a partir das tabelas públicas SEFAZ/Receita Federal. **Confirmado: 100% do zero, nenhuma das 98 regras DSL toca isso hoje** | Lia | nada — pode começar já | `ia-fiscal-diagnosis-vision.md` §4 |
| 6.2 | **Confirmado: não precisa de classificador fuzzy** — "tipo de operação" é sempre declarado de forma confiável no documento. A peça inteira é lookup determinístico (CFOP declarado × operação declarada × tabela) + LLM só pra explicar em linguagem natural | Lia + Dex | 6.1 | idem |
| 6.3 | Risco compartilhado a considerar: bug conhecido do `DslBlockInterpreter` em regras aninhadas/`&&`/`else` — relevante se essa validação algum dia for expressa nesse formato | Lia | — | memória da Lia `rag-fewshot-b4.md` |
| 6.4 | **Decisão já fechada, não fine-tuning:** é problema de lookup/tabela, não de padrão aprendido — nem pra este caso mais "semântico" o cálculo muda pra fine-tuning | — | — | `ia-fiscal-diagnosis-vision.md` §4.1 |

---

## Grupo 7 — Pipeline "NT nova" — autorizado a sair de proposta pra trabalho ativo

| # | Item | Dono | Depende de | Detalhe |
|---|------|------|------------|---------|
| 7.1 | P-1 (`XsdDiff` CLI) e P-2 (smoke de extração do PDF) — **autorizados pelo usuário em 2026-07-21** a rodar | Lia (dono original do doc) | nada | `nt-pipeline-design.md` §5 |
| 7.2 | P-3 (diff real `PL_009×PL_010b`) — **FECHADO (2026-07-21), não precisa de scraping.** O pacote XSD antigo `PL_009_V4` já está espelhado no repositório open-source `nfephp-org/sped-nfe` (GitHub) — `leiauteNFe_v4.00.xsd`, `nfe_v4.00.xsd`, `enviNFe_v4.00.xsd` e outros em `schemes/PL_009_V4/`. Reusar esse mirror em vez de construir scraper contra a SEFAZ pra este caso específico | Lia | nada — dado já disponível | `sefaz-xsd-schema-source.md` |
| 7.3 | Nota de consistência: o doc original permitia nuvem pra S2 (extração semântica do PDF, documento público) — por consistência com a decisão de decommission total (Grupo 1), usar Ollama local por padrão aqui também; só reconsiderar nuvem se qualidade local se provar insuficiente pra prosa jurídica complexa | Lia | — | `ia-fiscal-diagnosis-vision.md` §7 |
| 7.4 | Proposta do usuário (scraping direto da SEFAZ pra acompanhar NT futuras continuamente) — **avaliar como melhoria de P-1/P-2 pro caso contínuo, não como pré-requisito de P-3** (já resolvido no 7.2). Antes de construir scraper: verificar se `nfephp-org/sped-nfe` (ou mirror equivalente) já cobre o caso de acompanhamento contínuo — mais barato/robusto que parsear HTML. Se scraper direto for adiante: `WebFetch` na URL da SEFAZ falhou nesta sessão com erro de certificado ("unable to get local issuer certificate") — não confirmado se é padrão ICP-Brasil ou limitação da ferramenta; testar isso cedo antes de investir na implementação | Lia | nada — não bloqueia nada hoje | `sefaz-xsd-schema-source.md` |

---

## Bloqueios (atualizado 2026-07-21 — 3 de 4 fechados nesta rodada)

1. ~~Specs de GPU/VRAM do servidor de produção~~ — **FECHADO.** `BRNDDAPPBLD01`, i7-4790 Haswell 2014, 32GB
   RAM, sem GPU, sem upgrade previsto. Ver item 3.6 e `production-server-hardware.md`.
2. ~~Consumo final do dado sintético~~ — **FECHADO.** Alimenta a IA fiscal especializada, via RAG-enrichment
   (não treino, dado o hardware). Ver item 4.1.
3. ~~Pacote XSD antigo (`PL_009`)~~ — **FECHADO.** Já disponível em `nfephp-org/sped-nfe` no GitHub, sem
   precisar de scraping. Ver item 7.2.
4. **Rotação/revogação de segredos** (Grupo 1.6) — em andamento diretamente com `@lp-devops` (Gage), em
   paralelo a este documento. Não aguardar isso pra prosseguir com os demais grupos.

---

## Sequenciamento recomendado (visão executiva)

```
AGORA, SEM BLOQUEIO (podem rodar em paralelo — todos os bloqueios de dado externo fechados):
  1.1  remover AIService/OpenAI ................................. Dex
  1.4  fix incidental do RAGController (sem pressa) .............. Dex
  2.1  formalizar Pathway 2 como canônico ........................ Dex
  3.1  validação determinística de input ......................... Dex
  3.2  filtro de erro de assinatura .............................. Dex
  3.6  medir tok/s real no servidor + configurar AVX2 ............ Dex
  4.1/4.2  prototipar Faker/Mimesis + cenários fiscais (RAG) ..... Lia + Dex
  4.4  checagem de near-duplicate (design, qualquer nível) ....... Lia + Dex
  5.1  A6 corrigida (sidecar LinkMapping+Rule) ................... Lia
  5.2  passo de publicação (remove XComment debug) ............... Lia + Dex
  6.1  base CFOP×operação (do zero) .............................. Lia
  7.1  NT-pipeline P-1/P-2 ....................................... Lia
  7.2  puxar PL_009_V4 do mirror nfephp-org/sped-nfe .............. Lia

EM PARALELO, FORA DESTE DISPATCH:
  1.6  revogação da chave Gemini ................................. Gage (direto com o usuário)

DEPOIS que o acima fechar:
  1.2 → 1.3  decidir substituto de GeminiAIService, depois apagar . Dex + Lia
  3.4 → 3.5  serviço Ollama + endpoint (usando o modelo medido em 3.6) . Dex + Lia
  3.7        extensão do front-end (aba já existe) ................ lp-front-dev
  1.5        atualizar documentação .............................. Duda
  7.4        avaliar scraping SEFAZ pro caso contínuo (não urgente) . Lia

MID-TERM (Trilha A em andamento — A1-A5 já rastreados, A6 corrigida acima):
  qualidade multi-cliente do TCL/XSLT, critério ampliado com 5.3

LONG-TERM, GATED no mid-term provar qualidade suficiente:
  substituição do AppConnector em produção — usuário confirmou visibilidade, sequenciamento mantido
```

---

*LayoutParser · Dispatch consolidado da sessão de arquitetura · v1 · `@lp-architect` · 2026-07-21 · local, não enviado (`git push`).*
