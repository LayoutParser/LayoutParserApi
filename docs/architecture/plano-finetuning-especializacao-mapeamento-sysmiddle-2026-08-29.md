# Plano — fine-tuning para especialização em mapeamentos Sysmiddle (2026-08-29)

## Reversão explícita de decisão

Em `.claude/agent-memory/lp-architect/gemini-openai-decommission-decision.md` (2026-07-21,
Decisão 2), esta arquitetura recomendou **não fazer fine-tuning** para diagnóstico
XSD/síntese de XSLT — motivos: dataset pequeno (~5 mapeadores, risco de decorar), fine-tuning
não elimina o verificador (dado fiscal exige XSD+diff sempre), custo de manutenção a cada NT da
SEFAZ, e ausência de gap de capacidade que só fine-tuning resolveria.

Em 2026-08-29, o dono do projeto pediu diretamente fine-tuning para um objetivo mais específico:
não "diagnosticar erro melhor", mas **aprender e recriar os mapeamentos Sysmiddle existentes**
(TXT→TCL→XSL/XSLT→XML e XML→XSL/XSLT→XML), com observabilidade do processo. **Revertendo a
Decisão 2 por pedido explícito do dono.** Os motivos originais continuam parcialmente válidos e
moldam o desenho abaixo (dataset pequeno → LoRA em vez de fine-tuning completo; verificador
continua obrigatório mesmo com modelo especializado; hardware CPU-only em produção → treino
offline, inferência local). Não é um "esqueça a decisão anterior" — é uma mudança de escopo do
objetivo que muda o cálculo de custo/benefício.

## 1. Dataset de treino — honestidade sobre volume

Fontes reais disponíveis hoje:

- **Trio TXT → XML low-code → XML final** (README §5) — pares rotulados por definição, mas
  concentrados em poucos mapeadores reais confirmados nesta sessão e sessões anteriores
  (`.claude/agent-memory/lp-parser-llm/` cita ~5 mapeadores com layout de input; o mapeador de
  referência tem 237 `LinkMappings` + 98 regras DSL). Amostras adicionais em `.claude/tmp/
  exemplos/` (gabarito FIAT) e no corpus multi-cliente da Trilha A (FIAT/CNHI/IVECCO/MARELLI).
- **`RuleInterpretor` decifrado** (`docs/architecture/decisao-dsl-mapper-sysmiddle-2026-08-21.md`)
  — não é dado de treino em si, é o **oráculo determinístico** de como a Sysmiddle interpreta a
  DSL. Vale mais como componente do verificador (gerar pares sintéticos DSL→resultado corretos
  por construção) do que como texto para o modelo memorizar.
- **Dataset de métricas Job1/Job2** (`.claude/agent-memory/lp-architect/ai-metrics-job1-job2-gaps.md`)
  — escopo real confirmado é **4 de 54 pares**, a maioria do pipeline nunca converteu em amostra
  utilizável.

**Avaliação honesta:** o volume real hoje é **dezenas de pares, não milhares** — é dataset de
poucos mapeadores multiplicado por poucas linhas/regras cada. Isso é pequeno demais para
fine-tuning completo de um LLM (risco de overfitting/decoreba já sinalizado na Decisão 2
original) mas é **compatível com LoRA/QLoRA**, que é desenhado justamente para adaptar um modelo
base com centenas a poucos milhares de exemplos sem re-treinar os pesos inteiros. Ainda assim,
a primeira fase deve tratar o volume como **insuficiente para generalização** — ver Fase 1 vs
Fase 2 abaixo — e o `RuleInterpretor` deve ser usado para **gerar dado sintético rotulado
adicional** (variações de DSL → resultado correto, gerado deterministicamente pelo próprio
motor real, não por um LLM) antes de comprometer com qualquer treino real. Isso amplia o dataset
sem o risco de "IA decorando dado fiscal real" já levantado na Decisão 3 de 21/07.

## 2. Abordagem técnica — LoRA/QLoRA offline, não fine-tuning completo

Fine-tuning completo de um modelo, mesmo pequeno, exige GPU com VRAM significativa e volume de
dados maior do que o disponível — inviável tanto em custo quanto em risco de overfitting.
Recomendação:

- **QLoRA sobre um modelo base 1-3B compatível com Ollama** (ex.: Qwen2.5-Coder 1.5B/3B,
  StarCoder2-3B — escolha final depende de licença e desempenho medido, não travar aqui).
  Modelo pequeno é coerente com a restrição de inferência em produção (ver abaixo) e QLoRA
  reduz VRAM de treino o suficiente para caber em GPU de consumidor/cloud modesta (8-12GB),
  não em datacenter.
- **Treino acontece OFFLINE, fora do host de produção.** `production-server-hardware.md`
  confirma CPU-only (i7-4790 Haswell 2014, sem GPU) — treinar aí é impraticável mesmo para um
  modelo pequeno (achado já registrado em `gemini-openai-decommission-decision.md`: "TREINAR é
  mais pesado que INFERIR o mesmo modelo... CPU-only é impraticável"). O ciclo é:
  1. Treino do adaptador LoRA numa máquina com GPU (workstation com GPU discreta se existir na
     empresa, ou serviço de nuvem temporário — ver autorização pendente abaixo).
  2. Merge do adaptador no modelo base + quantização (GGUF, via `llama.cpp`/`Ollama Modelfile`).
  3. Só o artefato final (modelo quantizado) é publicado no `Ollama` do host de produção via
     `ollama create` a partir de um `Modelfile` — nenhum dado de treino nem processo de treino
     toca o servidor Haswell.
- **O verificador determinístico continua obrigatório**, mesmo com modelo especializado — não é
  substituído pelo fine-tuning. O loop gerar→validar(XSD/diff)→corrigir do `RepairOrchestrator`
  e do `AiTransformationCandidateService` continua sendo o mecanismo de confiança; fine-tuning
  melhora a taxa de acerto na primeira geração (menos iterações de correção), não elimina a
  necessidade de validar.

## 3. Dois pathways — um modelo base, dois adaptadores LoRA (não dois modelos do zero)

Recomendo **um único modelo base + dois adaptadores LoRA separados** (`lora-txt-tcl-xsl` e
`lora-xml-xsl`), carregados sob demanda pelo Ollama conforme o tipo de entrada detectado —
em vez de dois modelos treinados independentemente do zero. Justificativa:

- O modelo base já entende sintaxe XML/XSLT genericamente; o que muda entre os dois pathways é
  o "sotaque" de entrada (TXT posicional com TCL de meio-termo vs. XML já estruturado) e os
  padrões específicos de `LinkMappings`/regras DSL da Sysmiddle — exatamente o tipo de
  diferença que LoRA captura bem sem duplicar o custo de treino de um modelo base inteiro.
- Dataset pequeno (seção 1) penaliza mais fortemente treinar dois modelos completos
  independentes — dividir o já-escasso dado rotulado em dois treinos do zero piora a proporção
  dado/parâmetros ainda mais.
- Trade-off aceito: adaptadores LoRA compartilham o mesmo modelo base, então um bug de base
  (ex.: modelo base ruim em geração de XML bem-formado) afeta os dois pathways igualmente — não
  há isolamento total. Se a diferença entre os dois pathways se mostrar maior do que o esperado
  (ex.: TCL exigir raciocínio estrutural muito distinto de XML→XML), reavaliar para modelos
  separados é uma correção de rota barata (retreinar só o adaptador, não a decisão de base).

## 4. Observabilidade do processo de geração

Objetivo do dono: acompanhar o **processo**, não só o resultado. Reaproveitar antes de propor
infra nova:

- **Já existe parcialmente**: `RepairOrchestrator` (`ai/XslSynth.Core/Core/RepairOrchestrator.cs`)
  já estrutura o loop gerar→validar→corrigir em iterações discretas com resultado de cada
  validação (`CanonicalDiffer`, erros XSD por iteração) — é o esqueleto certo para logging
  estruturado por etapa, só falta emitir esses eventos para fora do processo em vez de manter
  só o resultado final.
- **Proposta mínima, sem infra nova**: instrumentar cada iteração do loop com `ILogger`
  estruturado (`_logger.LogInformation("Iteracao {N} mapper {MapperId}: xsltGerado={Xslt}
  erros={Erros}", ...)`, seguindo o padrão já estabelecido em `dotnet-standards.md`), correlacionado
  por `CorrelationId` do mapeador/execução. Isso já dá rastreabilidade via o sink Serilog
  existente (unificado entre Lib/Decrypt/Api, ver `unified-logging-and-multi-transform.md`) sem
  construir nada novo.
- **Fase 2 (se log estruturado não bastar)**: endpoint read-only que expõe o estado da última
  execução em andamento/concluída por mapeador (XSLT parcial por iteração, lista de erros de
  validação, decisão tomada) — reaproveitando o mesmo padrão de controller fino sobre serviço já
  usado no resto da API, sem WebSocket/streaming novo até haver evidência de que polling não
  serve. Streaming real (SignalR) só se justifica se o dono quiser acompanhar uma geração *ao
  vivo*, não apenas auditar depois — não presumir isso sem confirmar.

## 5. Fases

**Fase 1 — replicar o conhecido (o pedido do dono, literal).**
Escopo: só os mapeadores TXT→TCL→XSL/XSLT→XML já existentes e documentados (o corpus pequeno da
seção 1). Objetivo de sucesso: o modelo com LoRA recria a transformação de um mapeador que ele
viu em treino, validado por diff canônico contra o XML final real — não generalização, replicação
fiel do que já existe. Serve como prova de conceito de que LoRA + dataset pequeno é viável antes
de investir em generalização. Inclui: gerar dado sintético adicional via `RuleInterpretor`
(seção 1), treinar `lora-txt-tcl-xsl` primeiro (mais dados disponíveis que XML→XML), medir taxa
de acerto/iterações de correção necessárias, instrumentar observabilidade mínima (seção 4).

**Fase 2 — segundo pathway + generalização controlada.**
Treinar `lora-xml-xsl` com o corpus XML→XML disponível. Testar o modelo especializado da Fase 1
contra um mapeador que ele **não** viu em treino (held-out) — é aqui que se descobre se LoRA
generalizou ou só decorou. Se overfitting for confirmado, ampliar dataset sintético antes de
prosseguir, não forçar mais épocas de treino.

**Fase 3 — mapeamentos novos (fora do escopo desta sessão).**
Só depois de Fase 1/2 validadas: usar o modelo especializado como ponto de partida para
mapeamentos que a Sysmiddle nunca viu, sempre sob o loop gerar→validar→corrigir — este é o
objetivo de longo prazo ("eliminar Sysmiddle", já registrado em `finetuning-small-model-poc.md`),
não o objetivo desta rodada.

## Pontos que exigem autorização explícita do dono antes de qualquer implementação

1. **Uso de serviço de nuvem para treino** (se não houver GPU disponível internamente) — mesmo
   que o treino em si não use dado fiscal real de cliente (o corpus de mapeadores é estrutura
   DSL/XSLT, não necessariamente PII), a regra de `.claude/rules/security.md` ("LLM em nuvem: não
   envie documentos/dados reais de cliente sem autorização explícita") exige confirmação explícita
   de que os pares de treino escolhidos não contêm dado fiscal sensível antes de subir a máquina
   de nuvem — ou usar exclusivamente dado sintético gerado pelo `RuleInterpretor`.
2. **Qual GPU/orçamento está disponível** para o treino offline — a proposta assume que existe
   alguma forma de acesso a GPU (workstation da empresa ou cloud temporária); isso não foi
   confirmado nesta sessão.
3. **Escolha final do modelo base** — depende de licença (evitar repetir o problema de licença já
   encontrado com SDV/BSL na Decisão 3 de 21/07) e de medição real de desempenho no hardware de
   produção antes de comprometer.
4. **Nível de observabilidade desejado** (log estruturado vs. endpoint de estado vs. streaming ao
   vivo) — a Fase 4 do item de observabilidade (streaming) só deve ser construída se o dono
   confirmar que quer acompanhar geração em tempo real, não apenas auditar depois.

Nenhum código foi implementado. Trabalho de implementação (LoRA training scripts, endpoint de
observabilidade, wiring do modelo no Ollama) fica com `@lp-parser-llm`/`@lp-backend-dev`, sob
esta especificação.
