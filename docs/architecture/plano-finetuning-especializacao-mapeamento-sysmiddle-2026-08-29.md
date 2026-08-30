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

## Smoke-test executado em 2026-08-29

Executado de ponta a ponta na VM (`elson@172.25.32.48`, IP fixado depois para `172.25.32.5` no
meio da sessão — sem impacto no resultado, só na forma de acesso SSH). Resultados **medidos**,
não estimados.

### Specs reais confirmadas da VM

- CPU: Intel Core i7-4790 @ 3.60GHz, **4 núcleos físicos, 1 thread/núcleo** (sem HT), `avx2`
  disponível (importante para throughput de matmul em CPU).
- RAM: 15Gi total, ~14Gi livre no momento do teste.
- Disco: 59G total, 43G livres.
- GPU: **confirmada ausente** — `lspci | grep VGA` só lista `VMware SVGA II Adapter` (adaptador
  virtual de vídeo, não GPU de compute), `nvidia-smi` inexistente, `torch.cuda.is_available()`
  retorna `False`. Bate com a decisão 2 do dono (seção acima) — não era mais uma pendência de
  confirmação, mas ficou confirmado tecnicamente mesmo assim.

### Ambiente Python — mudança de abordagem necessária (bloqueio de `sudo` contornado)

- `python3-venv` **não está instalado** e criar venv exige `apt install python3.12-venv`, que por
  sua vez exige `sudo` **com senha** (não há NOPASSWD configurado para este usuário) — não tenho a
  senha e não é algo que o dono autorizou compartilhar aqui. Contornado **sem tocar em rede/apt**:
  `pip` de usuário via `get-pip.py --user --break-system-packages` (ambiente Debian 12 é
  "externally managed" por padrão, PEP 668) instalou `pip` isolado em
  `~/.local/lib/python3.12/site-packages`, sem exigir root. Todas as libs (`torch`, `transformers`,
  `peft`, `accelerate`, `datasets`, `bitsandbytes`) foram instaladas assim, com sucesso, sem
  precisar de `sudo` em nenhum momento.
- **Registrar para a Fase 2:** se o treino completo do fim de semana precisar de mais alguma
  dependência de sistema (não-Python) via `apt`, isso *vai* bloquear pela mesma razão (`sudo`
  pede senha) — sinalizar ao dono com antecedência, não descobrir isso já dentro da janela do
  fim de semana.

### QLoRA real em CPU — NÃO funcional, decisão técnica atualizada para LoRA fp32

`bitsandbytes` **instala e importa sem erro** em CPU-only (0.50.2), e até aceita construir um
`BitsAndBytesConfig(load_in_4bit=True)` e carregar um modelo com esse config sem lançar exceção.
Mas isso é enganoso: `bitsandbytes` não tem backend de quantização 4-bit funcional sem CUDA —
o carregamento "funciona" porque a lib não está de fato quantizando nada nesse ambiente (sem
kernel CUDA disponível para os kernels 4-bit reais). **Não é seguro assumir que QLoRA real
(quantização 4-bit efetiva) funciona nesta VM.** Atualização de decisão: a seção 2 deste plano
falava em "QLoRA em CPU" — na prática, o treino real usado no smoke-test (e recomendado para a
Fase 1) é **LoRA em fp32, sem quantização de peso** (`r=4`, `lora_alpha=8`, `target_modules=
["q_proj","v_proj"]`, via `peft.LoraConfig` puro). Isso não muda a viabilidade de rodar em CPU —
LoRA fp32 sobre um modelo 1.5B roda igual, só ocupa mais RAM/disco que a versão 4-bit
teoricamente quantizada (não um problema aqui, sobram 14G de RAM livre para um modelo de ~1.5B
em fp32, ~6GB).

### Resultado do smoke-test real (medido, não estimado)

- Modelo base testado: `Qwen/Qwen2.5-Coder-1.5B-Instruct` (Hugging Face, 1543.7M parâmetros) —
  download (~2.9GB) + carregamento em fp32: primeira vez ~259s (rede), segunda vez (já em cache
  local) **1.6s**.
- LoRA aplicado: `r=4`, `q_proj`/`v_proj` — **544.768 parâmetros treináveis (0,0353% do total)**.
- Dataset do smoke-test: **10 pares sintéticos** curtos (prompt/completion no estilo
  "campo TCL → template XSLT", não os 155 pares reais — ver limitação abaixo), `max_length=256`
  tokens, batch size 1, sem acumulação de gradiente.
- **Tempo de treino medido: 107,3 segundos para 1 época (10 passos, batch=1)** — ou seja,
  **~10,7 segundos por exemplo/passo** nessa CPU, nessas condições (seq_len 256, r=4).
- Ciclo completo (carregar modelo do cache + tokenizar + treinar 1 época) em **111 segundos**.
- `train_loss` caiu de ~5.19 para ~4.94 ao longo da época (esperado com 10 exemplos/1 época — não
  é sinal de qualidade, só confirma que o gradiente está fluindo e os pesos LoRA mudam).

### Extrapolação honesta para o dataset real (155 pares, Fase 1)

Usando o custo medido de ~10,7 s/exemplo/passo como base:

- **1 época sobre 155 exemplos reais, batch=1, seq_len=256 (como no smoke-test): ~1.660s
  (~28 minutos).** Para 3-5 épocas, **~1,4h a ~2,3h** — folga confortável dentro de uma janela de
  fim de semana (~48h), mesmo com margem para o passo de merge/quantização GGUF depois.
- **Risco real que o smoke-test não cobre:** os arquivos `.xsl` reais variam de 454 a 153.438
  caracteres (média ~16,6k caracteres, ver `finetuning-poc-fase1-dataset.md`) — muito além dos
  256 tokens usados no smoke-test. Sequências mais longas custam bem mais que proporcionalmente
  (atenção cresce ~O(n²), e mesmo componentes lineares escalam com n) — um exemplo de 16k
  caracteres (~4-6k tokens) pode custar **uma ordem de grandeza a mais por passo** que os 256
  tokens medidos aqui. **Não é seguro extrapolar linearmente o tempo total do dataset real sem
  medir com sequências do tamanho real.** Próximo passo necessário antes de comprometer a janela
  de fim de semana ao treino completo: rodar o mesmo smoke-test com 5-10 pares *reais* (não
  sintéticos) truncados/chunked no tamanho que a Fase 1 realmente vai usar, e medir de novo.
- Dataset real dos 155 pares **não estava disponível nesta sessão** — foi gerado numa sessão
  anterior em `.claude/tmp/dataset-finetuning/` (fora do controle de versão, ambiente reiniciado
  entre sessões, ver `session-environment-gotchas.md`). Reextrair via
  `Examples/tcl|xsl/<DocType>/<Versao>/*` (mesma lógica documentada em
  `finetuning-poc-fase1-dataset.md`) é pré-requisito antes do treino completo — script já existia,
  precisa ser reexecutado nesta VM ou os dados copiados para lá.

### Viabilidade do treino completo no fim de semana — condicional, não fechada

**O ciclo mecânico (dado → LoRA → treino → salvar adapter) está confirmado funcionando de ponta
a ponta em CPU nesta VM**, dentro de um orçamento de tempo pequeno. **Não está confirmado ainda**
que o tempo se mantém viável com sequências do tamanho real dos `.xsl` de produção — esse é o
próximo passo mais crítico antes de comprometer a janela inteira, não o tamanho do modelo (1.5B
já parece a escolha certa) nem o mecanismo de treino (LoRA fp32 funciona). Recomendação: rodar
smoke-test #2 com dados reais (mesmo que só 5-10 pares, tamanho real) antes do treino completo
de sábado à noite.

## Smoke-test #2 executado em 2026-08-29 (dado real, tamanho real)

Dataset real localizado (155 pares `tcl`/`xsl` batendo por nome em
`C:\inetpub\wwwroot\layoutparser\Examples\{tcl,xsl}\...`, 259 pares brutos considerando variantes
de versão). Confirmado: `xsl` real varia **454 a 153.438 caracteres** (média ~16,6k, bate com a
extrapolação anterior). Copiado para a VM (`~/finetuning-dataset/`) via `scp`; amostra de 8 pares
reais selecionada (`smoke_dataset_real.jsonl`).

**MAX_LEN=4096 (padding a 4096 tokens) → processo `Killed` (OOM) já no primeiro passo de
treino**, mesmo com só 1 exemplo de 38k tokens na amostra — o padding a 4096 tokens sozinho, em
fp32, já é grande demais para os ~15Gi de RAM livre desta VM.

**MAX_LEN=1024, 6 exemplos reais (os mais curtos, 647–5.689 tokens reais, 5 de 6 truncados) →
funcionou, mas no limite:** RAM subiu para **~12Gi de 15Gi usados** (`free -h` durante o treino),
e cada passo levou **~41–43s** (vs. ~10,7s/passo no smoke-test #1 sintético de 256 tokens) — ou
seja, **~4x mais lento para 4x mais tokens** (custo aproximadamente linear nessa faixa, não
quadrático ainda). 5 passos completos mediram consistentemente 41,06 / 41,38 / 42,25 / 41,85 /
43,70s; o 6º foi cortado pelo timeout de 300s do teste, não pelo treino em si.

### Veredito do smoke-test #2

**Não cabe como está.** Dois problemas distintos, não um só:

1. **RAM é o limite real, não tempo.** A 1024 tokens já usa 80% da RAM da VM; a 4096 tokens
   (ainda abaixo da média real de ~16,6k chars ≈ 4-6k tokens) o processo morre por OOM. Rodar o
   dataset real sem ajuste de memória não é uma questão de "esperar mais", é inviável nesta VM.
2. **Truncar para 1024 perde a maior parte do conteúdo real** (mediana dos `.xsl` reais é bem
   maior que 1024 tokens) — treinar truncado a esse tamanho ensinaria o modelo a gerar só o
   início dos XSLTs, não o documento completo. Não é uma solução de qualidade aceitável para a
   Fase 1, só um dado de calibração de custo.

**Ajuste necessário antes do treino completo de fim de semana** (em ordem de impacto):
- **Gradient checkpointing** (`model.gradient_checkpointing_enable()`) — troca RAM por tempo de
  CPU extra, é o único ajuste que ataca a causa raiz (RAM, não tempo) sem cortar conteúdo.
- Mesmo com checkpointing, considerar **chunking dos `.xsl` mais longos** (ex.: janelas de
  ~2-4k tokens com overlap) em vez de truncar cru — decisão de dataset prep, não só de treino.
- Se checkpointing sozinho não bastar, **reduzir ainda mais o batch/seq_len efetivo** ou aceitar
  treinar só no subconjunto de pares com `.xsl` mais curto (perde cobertura, mas mantém
  qualidade no que treina) — última opção, não a primeira.

Sem esse ajuste, o "cabe em 1,4h-2,3h" da extrapolação anterior (baseada em 256 tokens) não é
válido para o dataset real — mais tokens por passo custam mais RAM antes de custarem mais tempo,
e a VM esgota RAM antes de chegar ao tamanho real dos exemplos.

### Scripts usados (não commitados no repo — artefatos de sessão na VM)

`~/smoke_dataset.jsonl` (10 pares sintéticos) e `~/smoke_train.py` (script de treino LoRA com
`transformers.Trainer` + `peft.LoraConfig`, `use_cpu=True` — nota: a versão instalada de
`transformers` (5.16.1) **removeu o parâmetro `no_cuda`** de `TrainingArguments`, usar `use_cpu`
no lugar) ficaram na VM (`elson@172.25.32.5:~/`), não foram copiados para o repositório — são
artefatos de teste, não parte do pipeline de produção ainda.

## Smoke-test #3 executado em 2026-08-29 — gradient checkpointing + chunking

Continuação direta do smoke-test #2 (mesma VM, mesmo dataset real de 259 pares brutos em
`~/finetuning-dataset/`). Dois ajustes implementados e medidos separadamente.

### 1. Gradient checkpointing — resolve o OOM, custa ~2,7x mais tempo/passo

`model.config.use_cache = False` + `model.gradient_checkpointing_enable()` +
`gradient_checkpointing=True` em `TrainingArguments` (script `~/smoke_train_real3.py`, variante
de `smoke_train_real2.py` do smoke-test #2).

- **MAX_LEN=4096 (a config que morria por OOM no smoke-test #2):** não travou imediatamente como
  antes (RAM subiu a ~8,4GB e ficou estável passando o ponto onde antes morria), mas o processo
  **morreu silenciosamente durante o step 1** antes de completar (sem mensagem de erro capturada —
  sem acesso a `dmesg`/`journalctl` nesta VM para confirmar OOM-kill com certeza, mas o padrão
  memória-sobe-depois-processo-some é consistente com OOM). **Não confiável em produção.**
- **MAX_LEN=2048, 5 exemplos reais (647–5.689 tokens reais, 2 de 5 truncados):** funcionou de
  ponta a ponta, sem OOM. **RAM estável em ~11,56GB** (`PEAK_RSS_KB=11559932`) ao longo dos 5
  passos — folga de ~3,5GB dos 15GB da VM. **Tempo por passo: 110,85–119,73s** (5 passos:
  111,72 / 110,85 / 111,72 / 118,86 / 119,73s) — **quase constante independente do tamanho real
  do exemplo**, porque `padding="max_length"` força todo passo a processar os 2048 tokens cheios
  independente de quanto do exemplo é conteúdo real vs. padding. Treino completo (5 passos):
  **572,8s de treino puro, 627,5s wall-clock** (a maior parte do resto é carregar o modelo do
  cache, ~50-54s).
- **Comparação direta com smoke-test #2:** ~41-43s/passo em MAX_LEN=1024 sem checkpointing vs.
  ~111-120s/passo em MAX_LEN=2048 com checkpointing — dobrar o tamanho da sequência **e** ligar
  checkpointing custou ~2,7x o tempo por passo (não só ~2x pelo dobro de tokens; a recomputação de
  ativações no backward é o custo extra do checkpointing). **Veredito: MAX_LEN=2048 com gradient
  checkpointing é o teto realista de sequência nesta VM sem OOM** — 4096 continua não confiável
  mesmo com checkpointing.

### 2. Chunking dos exemplos longos — resolve a perda de conteúdo, mas explode o nº de exemplos

Script `~/build_chunks.py`: em vez de truncar o `.xsl` cru, cada par é tokenizado
(`AutoTokenizer` do próprio modelo) e o `completion` é dividido em janelas de até
`WINDOW=2048` tokens (descontado o espaço do prompt/instrução) com overlap de 256 tokens entre
janelas consecutivas — cada janela vira um exemplo de treino próprio, marcado com
`[trecho i/N do XSLT completo]` no prompt. **Confirmado antes de decidir a estratégia:** os XSLTs
reais não têm uma estrutura de `<xsl:template>` limpa e uniforme pra particionar por bloco
semântico (a maioria tem só 2 templates — um de match raiz + um principal monolítico; um caso
tinha 4, outro 0) — janela deslizante com overlap foi a opção viável, não particionamento por
template.

**Resultado medido no dataset real completo (259 pares brutos):**
- **123 de 259 pares (47%) precisam de chunking** (o `.xsl` sozinho já excede o orçamento de
  tokens disponível dentro da janela de 2048, mesmo descontando o prompt).
- **Total de exemplos de treino após chunking: 1.772** (era 259 um-exemplo-por-par).
- **Chunks por par: mínimo 1, média 6,84, máximo 65** (o par mais longo do dataset, 153k
  caracteres, tokeniza pra além do próprio limite de contexto do tokenizer do modelo — 34.487
  tokens vs. 32.768 do Qwen2.5-Coder, o tokenizer avisa mas ainda processa).

**Por que isso muda o veredito:** como o tempo por passo em MAX_LEN=2048 é ~quase-constante
(~115s) **independente do conteúdo real do exemplo** (por causa do padding fixo), o custo total de
treino escala com o **número de exemplos**, não com o volume de conteúdo coberto. Chunking
preserva conteúdo (resolve o problema de qualidade do smoke-test #2), mas ao custo de **multiplicar
o dataset por ~6,8x em média** — e o tempo de treino junto:

- **259 exemplos (sem chunking, truncado) × ~115s/passo × 1 época ≈ 8,3h.** Para 3 épocas,
  **~24,8h** — cabe numa janela de fim de semana (~48h) com folga real para merge/quantização.
  **Mas isso é o cenário que ainda trunca os 123 pares longos** (47% do dataset perde parte do
  conteúdo real).
- **1.772 exemplos (com chunking, cobertura completa) × ~115s/passo × 1 época ≈ 56,6h.** Já
  **1 única época não cabe numa janela de fim de semana de ~48h** — muito menos as 3-5 épocas
  planejadas originalmente. Chunking com cobertura completa do dataset real, nas condições desta
  VM, é **inviável no fim de semana**, não é uma questão de ajuste fino de configuração.

### Veredito final do smoke-test #3

**O ciclo mecânico com gradient checkpointing está confirmado funcionando e sem OOM em
MAX_LEN=2048** — esse ajuste funcionou exatamente como esperado (RAM estável, ~3,5GB de folga).
**Chunking funciona tecnicamente** (script gera o dataset expandido corretamente, medido em
dataset real) **mas não resolve o problema dentro da janela de fim de semana** — ele troca "perda
de conteúdo" por "tempo de treino inviável", não elimina o trade-off, só desloca ele. Não é seguro
prometer que os dois ajustes juntos destravam o treino completo de qualidade nesta janela.

**Opção realista recomendada para o treino do fim de semana (em ordem de preferência):**
1. **MAX_LEN=2048 + gradient checkpointing, SEM chunking (truncamento aceito para os 123/259
   pares longos), 3 épocas, ~24,8h medidas** — cabe com folga, é o que efetivamente roda ponta a
   ponta dentro da janela. Sacrifica cobertura completa nos mapeadores mais longos (aceitável para
   a Fase 1, cujo objetivo declarado é "ver o ciclo funcionando", não qualidade de produção).
2. **Chunking parcial com teto** (ex.: no máximo 3-4 chunks por par, cobrindo só o início de cada
   XSLT longo em vez de tudo) — não medido nesta rodada; reduziria o multiplicador de ~6,84x pra
   algo como ~2-3x nos pares que hoje geram muitos chunks, mas ainda não cabe com folga junto de
   3-5 épocas sem reduzir também o nº de pares de origem. Precisaria de novo smoke-test antes de
   comprometer a janela.
3. **Reduzir épocas para 1** com chunking completo (1.772 exemplos, ~56,6h) — **não cabe** mesmo
   assim numa janela de ~48h; descartado.
4. **Reduzir o dataset de origem** (treinar só nos pares mais curtos, ex.: os que não precisam de
   chunking) mantendo chunking pros poucos que sobrarem — mantém qualidade total nesses pares às
   custas de cobertura de mapeadores (fica coerente com a decisão já registrada no plano de "Fase 1
   usa só o mapeador de referência mais bem documentado", não o corpus inteiro).

**Recomendação concreta:** opção 1 para o treino de fim de semana desta rodada — é a única medida
com números reais que cabe com folga. Revisitar chunking com teto (opção 2) ou modelo maior/GPU só
na Fase 2, depois que o ciclo ponta a ponta (dado → treino → merge → `ollama create` → geração
observável → validação) tiver rodado pelo menos uma vez sob a opção 1.

### Scripts do smoke-test #3 (não commitados — artefatos de sessão na VM)

`~/smoke_train_real3.py` (gradient checkpointing) e `~/build_chunks.py` (chunking com overlap),
mais o dataset gerado `~/finetuning-dataset-chunked.jsonl` (1.772 linhas) — ficaram na VM
(`elson@172.25.32.5:~/`), mesmo tratamento dos scripts anteriores (artefatos de teste).

## Smoke-test #4 executado em 2026-08-29 — chunking de par único (Fase 1 real)

Mudança de escopo pedida pelo dono: em vez de medir chunking do dataset inteiro (inviável, ver
smoke-test #3), aplicar chunking a **um único par `.tcl`/`.xsl` completo, sem truncar**, e treinar
o modelo a recriar EXATAMENTE esse mapeamento — a "primeira fase" real de "ver funcionando" antes
de generalizar. Par escolhido: `NFe/4.00/NFe009_4.00_EnvioNFe_NeoGridToSefaz` (`.tcl`=10.101
tokens, `.xsl`=41.901 tokens — maior que o próprio limite de contexto do tokenizer do modelo,
32.768, avisado mas processado; tamanho médio-grande do dataset, não o menor).

### Chunking do par único

`~/build_chunks_single.py` (variante de `build_chunks.py` restrita a este par) gerou **57 chunks**
de até 2048 tokens de completion (janela deslizante, overlap 256) — cobertura completa do `.xsl`,
sem perder conteúdo.

### Bug real achado e corrigido antes do treino

`smoke_train_real3.py` (script de treino usado nos smoke-tests #2/#3) **não truncava o prompt**
antes de concatenar com a completion e truncar o texto final a `MAX_LEN`. Como o prompt (`.tcl`
bruto completo, 10.101 tokens) já excede sozinho o `MAX_LEN=2048`, a truncação final cortava a
**completion inteira** — os 57 exemplos treinavam com 0 tokens de XSLT real, só texto do `.tcl`.
Confirmado rodando 3 steps antes da correção: `tokens reais=[10700, 11121, ...]`, `truncados p/
MAX_LEN=2048: 57/57`. Esse é o mesmo tipo de risco que motivou o budget assumido em
`build_chunks.py` (`WINDOW - min(len(prompt_ids), WINDOW//2) - 30`) — o build script já assumia
truncamento do prompt a `WINDOW//2`, mas o script de treino não aplicava essa mesma premissa.
**Corrigido** em `~/smoke_train_single_pair.py`: trunca o prompt a `MAX_LEN//2` tokens antes de
montar o texto de treino, replicando a premissa do `build_chunks.py`. Reexecutado com 3 exemplos
de teste: `tokens reais pos-truncamento-de-prompt=[2029, 2029, 2029]`, `0/3` truncados — completion
íntegra presente no exemplo de treino.

**Why isso importa:** sem essa correção, qualquer treino sobre pares com prompt (`.tcl`) grande
(comum neste dataset — TCL costuma ser maior que o `.xsl` correspondente em vários casos) treinaria
o modelo a recriar o `.tcl` de entrada, não a gerar XSLT — silenciosamente, sem erro, com loss
caindo normalmente (o padrão perigoso: métricas de treino "normais" mascarando dado de treino
errado). **How to apply:** ao montar QUALQUER pipeline de fine-tuning prompt+completion com
truncamento por tamanho total, sempre truncar o prompt de forma isolada e explícita ANTES de
concatenar — nunca confiar em `truncation=True` sobre o texto já concatenado quando o prompt
sozinho pode exceder o orçamento.

### Treino — 57 chunks, 1 época, MAX_LEN=2048, gradient checkpointing

`~/smoke_train_single_pair.py 2048 57 single_pair_chunked.jsonl 1`, rodado em background via
`nohup` (independente da sessão SSH, sobreviveu a múltiplos monitores SSH encerrados pelo harness
local). **Concluído de ponta a ponta, sem OOM:**

- **Tempo de treino puro: 6.894,3s (1h54min54s).** Wall-clock total: 6.898,3s.
- **~120-122s/passo, quase constante** (mesmo padrão do smoke-test #3, `padding="max_length"`
  domina o custo) — variação de 115,6s a 137,7s (2 passos levemente mais lentos, 132/136s, sem
  causa identificada, possível contenção momentânea de CPU no host).
- **RSS estável em ~11,64GB** (`PEAK_RSS_KB=11639068`) — mesma margem de segurança do smoke-test
  #3 (~3,4GB de folga dos 15GB da VM).
- **Loss:** primeiro passo 0,7647 → último passo 0,5935 (`train_loss` médio da época: 0,7033,
  esperado com ruído alto — só 1 época, 57 exemplos de UM par só, sem validação hold-out).
- Adapter LoRA salvo em `~/lora_single_pair_adapter` (rank 4, `q_proj`/`v_proj`, ~544K parâmetros
  treináveis de 1,54B — 0,035%).

**Extrapolação honesta para 3 épocas:** ~3 × 1h55min ≈ **5h45min** para este único par — cabe
folgado numa sessão, bem diferente da escala do dataset inteiro (24,8h–56,6h no smoke-test #3).
Não foi executado (rodou só 1 época, por orçamento de tempo desta sessão) — mas a extrapolação é
direta porque o tempo/passo já mostrou ser quase-constante em 3 rodadas diferentes (smoke #2, #3,
#4).

### Validação — geração real com o adapter treinado

`~/infer_single_pair.py`: carrega o modelo base + adapter LoRA, gera com `model.generate()`
(greedy, `max_new_tokens=1024`) a partir do `.tcl` de entrada (mesmo truncamento de prompt do
treino, 1024 tokens), salva em `~/gerado_single_pair.xsl`. Geração em CPU levou também vários
minutos (autoregressive, sem KV-cache acelerado por hardware).

**Resultado, honesto:**
- Os **primeiros ~1024 tokens gerados não são XSLT** — o modelo ecoa conteúdo em estilo do `.tcl`
  de entrada (linhas `<FIELD name="..." length="..."/>`), não a estrutura de saída esperada.
  Hipótese mais provável: o prompt truncado a 1024 tokens cobre só a primeira fração do `.tcl`
  (10.101 tokens reais), e com só 1 época sobre 1 par o modelo ainda não aprendeu a transição
  clara "fim do prompt → início da resposta" — problema de treino insuficiente, não de arquitetura
  quebrada.
- **A partir daí, a geração converge para XSLT real e sintaticamente válido**: produz
  `xsl:choose`/`xsl:when`/`xsl:otherwise` corretamente aninhados, com **defaults idênticos ao
  `.xsl` real** para os mesmos campos — `tpAmb` (`otherwise=2`), `tpEmis` (`otherwise=1`),
  `procEmi` (`otherwise=0`) todos batem exatamente com o arquivo de referência, incluindo a
  estrutura `<xsl:if test="CAMPO!=''">` para campos opcionais (`dhSaiEnt`, `indIntermed`) que
  também bate com o padrão real.
- **Não é reprodução perfeita** (não comparado token-a-token / diff formal — comparação visual,
  como pedido) — é evidência real de que o modelo está aprendendo o mapeamento semântico correto
  (valores default específicos deste mapeador, não genéricos), não só sintaxe XSLT genérica.

### Veredito do smoke-test #4

**A Fase 1 reduzida ("aprender 1 mapeamento completo primeiro") é viável nesta VM e já mostra sinal
real de aprendizado específico do mapeador**, mesmo com só 1 época. Achado colateral valioso: o bug
de truncamento de prompt (não descoberto nos smoke-tests #2/#3, que usavam exemplos com prompt
menor) teria comprometido silenciosamente qualquer treino real sobre este dataset — correção
aplicada e confirmada antes de gastar as ~2h de treino real. Próximo passo natural (fora do escopo
desta sessão): rodar as 3 épocas completas (~5h45min extrapoladas) e comparar a geração final com
diff formal contra o `.xsl` real, não só inspeção visual.

### Scripts do smoke-test #4 (não commitados — artefatos de sessão na VM)

`~/build_chunks_single.py`, `~/smoke_train_single_pair.py` (corrige o bug de truncamento de
prompt), `~/infer_single_pair.py` (validação por geração), dataset `~/single_pair_chunked.jsonl`
(57 linhas), adapter `~/lora_single_pair_adapter/`, saída `~/gerado_single_pair.xsl` — todos na VM
(`elson@172.25.32.5:~/`), mesmo tratamento dos artefatos de teste anteriores.
