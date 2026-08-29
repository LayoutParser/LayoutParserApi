# Plano — fine-tuning para especialização em mapeamentos Sysmiddle (2026-08-29)

## Decisões finais do dono (fecham o plano — sem bloqueios de infra pendentes)

1. **Host de treino QLoRA e de inferência em produção: a mesma VM Ubuntu que já roda o Ollama
   hoje** (`UBU220405RUN`, IP atual `172.25.32.5`). Treino roda numa janela de fim de semana,
   coordenada para não colidir com o cron do Job 1 de métricas (sábado 00:00, ver
   `metrics-job-topology-vm.md`).
2. **A VM NÃO tem GPU confirmada — nenhuma memória disponível registra GPU para esta VM
   VirtualBox, e o padrão de uso observado até hoje (Job 1/`metrics-batch`) sempre foi
   CPU-tolerante por design.** Isso foi levantado como bloqueio técnico nesta sessão. **Decisão
   consciente do dono: prosseguir mesmo assim, treinando em CPU.** Não é mais uma pendência em
   aberto — é uma escolha explícita, registrada aqui.
3. **Prioridade da Fase 1 não é performance/qualidade do treino — é ver o processo de
   desenvolvimento do mapeador acontecendo de ponta a ponta, com observabilidade em tempo real.**
   Otimizar velocidade/qualidade vem depois, só depois de o ciclo completo (dado → treino →
   modelo → geração observável → validação) funcionar de ponta a ponta pelo menos uma vez.
4. **Observabilidade: streaming ao vivo**, prioridade igual ou maior que o resultado do treino em
   si — é o mecanismo que permite ao dono "ver o modelo desenvolvendo" o mapeador em tempo real,
   não apenas auditar depois.

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
continua obrigatório mesmo com modelo especializado). O raciocínio evoluiu ainda mais dentro
desta mesma sessão — ver histórico de correções de rumo na seção 2.

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

**Avaliação honesta:** o volume real hoje é **dezenas de pares, não milhares**. Isso é pequeno
demais para fine-tuning completo (risco de overfitting/decoreba já sinalizado na Decisão 2
original) mas é **compatível com LoRA/QLoRA**. Para a Fase 1 (ver-o-processo-funcionando), usar
inicialmente **apenas o(s) mapeador(es) já melhor documentados** (o de referência com 237
`LinkMappings`), sem esperar completar todo o corpus — objetivo é destravar o ciclo ponta a
ponta, não maximizar dataset ainda. O `RuleInterpretor` continua disponível para gerar dado
sintético adicional quando a Fase 2 (generalização) exigir mais volume.

## 2. Abordagem técnica — QLoRA em CPU, na VM Ubuntu, modelo reduzido pela realidade do hardware

> **Histórico de correções de rumo nesta sessão (registrado para não se repetir):**
> 1ª versão dimensionava o modelo pelo hardware do host de produção `BRNDDAPPBLD01` (1-3B).
> O dono corrigiu: não subdimensionar pelo hardware, dimensionar pelo domínio (7B-14B).
> 2ª versão então apontou um bloqueio técnico real (VM sem GPU confirmada) antes de fechar o
> tamanho do modelo em produção.
> **Decisão final do dono: aceitar o hardware real (CPU-only na VM) e ajustar o modelo/adapter
> para o que é viável nele — não é mais "não subdimensionar por hardware", é "primeiro fazer
> funcionar de ponta a ponta, otimizar depois".** As duas orientações não se contradizem: a
> primeira valia enquanto o objetivo era "modelo tecnicamente ideal"; agora o objetivo explícito
> da Fase 1 é "ciclo completo observável", o que muda o cálculo.

- **Modelo base: reduzir para a faixa 1-3B (ex.: Qwen2.5-Coder 1.5B, `qwen2.5-coder:3b`,
  StarCoder2-3B)** — não a faixa 7B-14B da versão anterior. QLoRA em CPU sobre 7B-14B é
  tecnicamente possível mas o tempo de treino cresce de forma proibitiva mesmo para uma janela
  de fim de semana longa; 1-3B é o ponto onde "dias, não semanas" continua uma estimativa honesta
  (ver abaixo). **Trade-off explícito:** modelos 1-3B têm taxa de erro estrutural maior em geração
  de XML/XSLT bem-formado que 7B-14B — a Fase 1 vai gerar mapeadores de qualidade pior que o
  ideal, compensados pelo loop de correção existente (`RepairOrchestrator`). Isso é aceito
  conscientemente pelo dono como custo da Fase 1; revisitar o tamanho do modelo é o primeiro item
  da Fase 2 (seção 5).
- **LoRA com rank reduzido** (ex.: r=4 ou r=8, em vez de r=16+ que se usaria com GPU) — reduz
  ainda mais o custo computacional por época, à custa de menor capacidade de adaptação. Ajustar
  para cima assim que o ciclo funcionar e houver folga de tempo na janela de fim de semana.
- **Estimativa realista de tempo — não medida, extrapolação honesta:** treino QLoRA de um modelo
  1-3B em CPU, sobre um dataset de dezenas de exemplos, por poucas épocas, tende a levar **de
  algumas horas a 2-3 dias**, dependendo da CPU real da VM (não documentada em memória — nenhuma
  sessão anterior perfilou a CPU desta VM especificamente, só confirmou que ela hospeda o job de
  métricas via CPU sem medir throughput de treino). **Não prometer um número fixo sem medir** —
  o primeiro passo do plano de execução (seção 6) é justamente medir isso com um treino de
  smoke-test antes de comprometer a janela inteira de fim de semana a um treino completo que pode
  não terminar a tempo.
- **Onde roda:** treino e inferência na mesma VM Ubuntu (decisão 1 do dono) — elimina a etapa de
  "decidir onde publicar o modelo" que versões anteriores deste plano tratavam como pendência.
  Ciclo: (1) treino QLoRA na janela de fim de semana → (2) merge do adaptador no modelo base +
  quantização GGUF (`llama.cpp`/`Ollama Modelfile`) → (3) `ollama create` na própria VM, que já
  roda `ollama.service` em produção.
- **O verificador determinístico continua obrigatório**, mesmo com modelo reduzido — o loop
  gerar→validar(XSD/diff)→corrigir do `RepairOrchestrator`/`AiTransformationCandidateService`
  segue sendo o mecanismo de confiança; com um modelo 1-3B, esse loop vai rodar mais iterações de
  correção que rodaria com 7B-14B — esperado e aceito.

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
  dado/parâmetros ainda mais. Com treino em CPU (seção 2), esse argumento pesa ainda mais: dois
  treinos completos do zero dobraria a já apertada janela de fim de semana.
- **Fase 1 treina só `lora-txt-tcl-xsl` primeiro** (mais dados disponíveis, é o pathway citado
  primeiro pelo dono) — `lora-xml-xsl` fica para a Fase 2, depois que o ciclo ponta a ponta já
  tiver sido validado uma vez.
- Trade-off aceito: adaptadores LoRA compartilham o mesmo modelo base, então um bug de base afeta
  os dois pathways igualmente. Se a diferença entre os dois pathways se mostrar maior do que o
  esperado, reavaliar para modelos separados é uma correção de rota barata.

## 4. Observabilidade — streaming ao vivo (prioridade igual ao treino, não um extra)

Decisão do dono: streaming, não só log estruturado pós-fato. Plano de execução, reaproveitando
o que já existe:

- **Base já existe**: `RepairOrchestrator` (`ai/XslSynth.Core/Core/RepairOrchestrator.cs`) já
  estrutura o loop gerar→validar→corrigir em iterações discretas com resultado de cada validação
  (`CanonicalDiffer`, erros XSD por iteração). É o ponto de emissão de eventos.
- **Mecanismo recomendado: SignalR Hub** (já é o padrão ASP.NET Core nativo do ecossistema deste
  projeto, sem dependência nova) publicando um evento por iteração do loop — XSLT/TCL parcial
  gerado, lista de erros de validação da iteração, decisão tomada (aceitar/corrigir/desistir). O
  front-end React (que já tem aba de análise/transformação, ver
  `frontend-transformation-tab-built.md`) consome o Hub para exibir a geração acontecendo ao
  vivo. Alternativa mais simples (Server-Sent Events) é viável se SignalR se mostrar pesado demais
  para esse uso pontual — decisão de implementação de `@lp-backend-dev`/`@lp-parser-llm`, não
  travada aqui.
- **Esforço revisado para cima vs. a estimativa original deste plano:** a primeira versão desta
  seção tratava streaming como "Fase 2, só se log não bastar" — a decisão do dono inverte isso:
  streaming é parte da Fase 1, não uma evolução posterior. Isso adiciona trabalho de
  implementação real (Hub, contrato de evento, consumo no front) à primeira entrega, não é mais
  "log estruturado é suficiente por ora".
- Log estruturado via `ILogger`/Serilog continua sendo feito em paralelo (auditoria pós-fato,
  correlação por `CorrelationId`) — streaming é para acompanhamento ao vivo, log é para revisão
  depois; os dois são necessários, não um substituindo o outro.

## 5. Fases

**Fase 1 — ciclo completo, ponta a ponta, observável (o objetivo desta rodada).**
Escopo: um único mapeador já bem documentado (o de referência, 237 `LinkMappings`), pathway
TXT→TCL→XSL/XSLT→XML. Modelo 1-3B + LoRA rank baixo, treino CPU na VM Ubuntu, janela de fim de
semana. Sucesso = o ciclo inteiro roda sem intervenção manual: dado preparado → treino QLoRA →
merge/quantização → `ollama create` → geração de um mapeador via `RepairOrchestrator` observável
em streaming ao vivo → validação XSD/diff. Não exige generalização nem qualidade de produção —
exige que o pipeline exista e seja visível.

**Fase 2 — otimizar + segundo pathway + generalização controlada.**
Depois que a Fase 1 funcionar pelo menos uma vez: (a) revisitar tamanho do modelo/rank do LoRA
com dados reais de tempo de treino da Fase 1 na mão; (b) treinar `lora-xml-xsl`; (c) testar contra
mapeador held-out (não visto em treino) para medir generalização real vs. decoreba.

**Fase 3 — mapeamentos novos (fora do escopo desta sessão).**
Usar o modelo especializado como ponto de partida para mapeamentos que a Sysmiddle nunca viu,
sempre sob o loop gerar→validar→corrigir — objetivo de longo prazo ("eliminar Sysmiddle", já
registrado em `finetuning-small-model-poc.md`).

## 6. Plano de execução — passo a passo para `@lp-parser-llm` (Lia)

1. **Confirmar acesso e specs reais da VM** — SSH em `elson@172.25.32.5`, rodar `lscpu` (núcleos/
   threads/instruction set) e `nvidia-smi`/`lspci | grep -i vga` (confirmar de vez ausência de
   GPU, para registro, não para bloquear). Sem isso, a estimativa de tempo da seção 2 continua
   extrapolação.
2. **Preparar o dataset da Fase 1**: extrair o par TXT→XML-lowcode→XML-final do mapeador de
   referência (237 `LinkMappings`) em formato de treino (prompt/completion ou instrução/resposta,
   a definir pela ferramenta de treino escolhida — ex. `peft`/`transformers` da Hugging Face).
3. **Escolher e baixar o modelo base 1-3B** (ex. `Qwen2.5-Coder-1.5B` ou `-3B`, verificar licença)
   na VM.
4. **Rodar um smoke-test de treino** (1 época, poucos passos) para medir tempo real por época
   nessa CPU específica — usar esse número para decidir se o treino completo cabe na janela de
   fim de semana ou se é preciso reduzir mais (menos épocas, rank menor, ou dataset ainda mais
   enxuto).
5. **Agendar o treino completo** para a próxima janela de fim de semana disponível, coordenado
   para não colidir com o cron do Job 1 (sábado 00:00) — treino deve rodar em horário que não
   dispute CPU com o job de métricas.
6. **Merge + quantização GGUF + `ollama create`** do adaptador treinado.
7. **Implementar o Hub de streaming** (seção 4) no `RepairOrchestrator`/serviço que o invoca,
   emitindo evento por iteração do loop gerar→validar→corrigir.
8. **Rodar uma geração real do mapeador de referência** contra o modelo novo, observando via
   streaming, e validar o resultado contra o XML final real (diff canônico) — este é o critério
   de sucesso da Fase 1.

## Pontos que ainda exigem autorização explícita do dono (reduzidos — a maioria já foi decidida)

1. **Uso de serviço de nuvem** — não se aplica mais como pendência principal (treino é na VM,
   decisão 1), mas continua valendo se, durante a execução, a Fase 2 decidir migrar para GPU
   externa: nesse caso, confirmar que os dados de treino usados não contêm dado fiscal real de
   cliente, ou usar exclusivamente dado sintético via `RuleInterpretor`.
2. **Escolha final do modelo base específico** (qual 1-3B, exatamente) — depende de licença e do
   resultado do smoke-test do passo 4 acima; não travar aqui.

Nenhum código foi implementado. Trabalho de implementação (dataset prep, scripts de treino QLoRA,
Hub de streaming, wiring do modelo no Ollama) fica com `@lp-parser-llm`/`@lp-backend-dev`, sob
esta especificação.
