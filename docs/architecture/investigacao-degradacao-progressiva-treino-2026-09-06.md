# Investigação: degradação progressiva do tempo/passo no treino LoRA (2026-09-06)

## Contexto

Treino LoRA (`~/train_lora.py`, Qwen2.5-Coder-1.5B-Instruct, CPU-only) na VM
`elson@172.25.32.5`, retomado do `checkpoint-400` após reboot da VM
(`python3 train_lora.py --dataset ~/sysmiddle-dsl-dataset-2026-09-02.jsonl
--output-dir ~/lora-out-v2 --epochs 3 --batch-size 1 --grad-accum 16`, log em
`~/train_lora_v2.log`, PID 1542 no momento da investigação).

Sintoma reportado: tempo por passo crescendo de forma monótona desde a retomada
(passo 411: 47.6s → 415: 97.9s), sem indício óbvio de contenção externa (load
average estável ~3.5-3.7/4 núcleos, sem throttling, RSS estável).

**O processo de treino NÃO foi tocado durante esta investigação** — nenhum kill,
restart, nem alteração no `train_lora.py` em execução. Só leitura (`/proc`,
`py-spy dump` somente-leitura) e uma instalação de `py-spy` via `pip install`
na venv (não afeta o processo já carregado em memória).

## O que foi medido (evidência, não suposição)

Log completo desde a retomada (`~/train_lora_v2.log`), tempos por passo em
`s/it` do tqdm:

```
401: 0.43s (2.33it/s)   407: 11.01s   413: 74.42s
402: 1.30s              408: 15.29s   414: 86.64s
403: 2.40s              409: 22.81s   415: 97.88s
404: 3.49s              410: 33.25s   416: 128.39s
405: 5.08s              411: 47.62s   417: 134.64s
406: 7.73s              412: 63.67s   418: 151.42s
```

A degradação **começa já no 2º passo pós-retomada** (401 foi rápido, 402 em
diante cresce sem parar) — não é algo que vinha se arrastando de muito antes do
reboot.

### 1. Não é vazamento de memória do processo

```
VmRSS oscila 6.97GB–7.41GB em ~2min (não cresce monotonicamente)
VmData oscila 8.11GB–8.55GB no mesmo intervalo
VmSwap: 0 kB / Swap do sistema: 0B usado
Threads: estável em 15
/proc/<pid>/maps: estável em 962 linhas (sem crescimento de VMAs)
majflt (page faults maiores/swap-in): ~1900 total, desprezível
```

Isso descarta as hipóteses 1 e 2 do pedido original (histórico/lista
crescendo sem limite, ou tensores retidos inflando o grafo de autograd e
degradando o GC) — se houvesse retenção de grafo/tensores, RSS e VmData
teriam que crescer de forma monotônica junto com o tempo por passo, e não
oscilar/estabilizar como medido.

### 2. Não é I/O, swap, nem contenção de CPU externa

```
utime+stime delta / wall delta ≈ 3.78 núcleos ocupados de 4 (ps mostra %CPU=364%)
```

O processo está genuinamente ocupado computando durante o passo lento, não
bloqueado esperando disco/rede/outro processo. Ou seja: o código está fazendo
**mais trabalho de CPU de fato**, não "esperando mais".

### 3. Não há callback customizado nem `report_to` acumulando estado

Lido `~/train_lora.py` na íntegra (176 linhas). Não há `TrainerCallback`
customizado registrado, `report_to=[]` (nenhum logger externo), e o
`SFTTrainer` é instanciado sem `callbacks=[...]`. `logging_steps=10` só aciona
os callbacks padrão do `transformers` (`ProgressCallback`/`DefaultFlowCallback`),
que fazem trabalho O(1) por chamada (atualizar postfix do tqdm), não O(passos).
Isso descarta a hipótese 3 do pedido original.

### 4. `py-spy dump` (somente leitura) mostra um forward/backward normal

```
Thread 1542 (active): "MainThread"
    forward (torch/nn/modules/linear.py:134)
    ...
    forward (peft/tuners/lora/layer.py:1058)
    ...
    forward (transformers/models/qwen2/modeling_qwen2.py:182/234)
    checkpoint (torch/utils/checkpoint.py:512)
    ...
    _chunked_ce_forward (trl/trainer/sft_trainer.py:311)
    forward (peft/peft_model.py:2101)
    compute_loss (transformers/trainer.py:4110 / trl/trainer/sft_trainer.py:1789)
    training_step (transformers/trainer.py:4020 / trl/trainer/sft_trainer.py:1891)
    _inner_training_loop (transformers/trainer.py:2674)
```

Pilha limpa: um forward normal passando pela camada Qwen2 com LoRA e pelo
`_chunked_ce_forward` do TRL (cross-entropy em chunks, técnica de economia de
memória — não tem loop dependente do nº de passos já executados). Nada de
recursão anômala, nada de laço reprocessando histórico. `use_cache=False` é
setado explicitamente dentro do próprio `_chunked_ce_forward`
(`trl/trainer/sft_trainer.py:~309`), então a hipótese de KV-cache vazando e
crescendo entre passos também fica descartada — o código do TRL já neutraliza
isso.

### 5. A causa mais provável: `batch_size=1` expõe diretamente a variância de comprimento do dataset

Medição direta e barata do dataset (sem tocar no processo de treino, só um
`awk` no arquivo `.jsonl`), comprimento em caracteres por linha (proxy do
comprimento em tokens, já que o dataset é DSL/código):

```
n=6044 exemplos
min=339 caracteres
p50=529 caracteres   (~130 tokens)
p90=1579 caracteres  (~400 tokens)
p99=4327 caracteres  (~1000+ tokens, próximo do teto max-seq-length=1024)
max=6447 caracteres  (truncado em 1024 tokens pelo SFTConfig)
```

Isso é uma distribuição de cauda longa muito pronunciada: o exemplo mediano
tem ~130 tokens, mas o percentil 99 chega a ~4x o `max-seq-length` configurado
(1024), sendo truncado no teto. Com `--batch-size 1`, **não há padding
"amortizando" a variação** — cada passo de otimização agrega `grad-accum=16`
exemplos individuais, e o custo de cada um (atenção é O(n²) no comprimento de
sequência, MLP é O(n)) é proporcional ao comprimento daquele exemplo
específico, sem nenhum outro exemplo do mesmo tamanho de batch pra "diluir" a
média.

Combinado com o fato de que o `Trainer` embaralha o dataset por época com uma
seed fixa (`args.seed`), a ordem de exemplos dentro da época 2 é determinística
mas não uniforme — é plausível (e mais provável que qualquer bug de código)
que os passos 402–418 estejam atravessando um trecho da época com uma
concentração local de exemplos longos (por exemplo, DSL de mapeadores fiscais
mais verbosos, que tendem a vir de blocos correlacionados do dataset de
origem). Isso produziria exatamente o padrão observado: crescimento suave e
consistente por uma dúzia+ de passos seguidos, sem qualquer vazamento de
memória ou recurso.

**Isto NÃO foi provado de forma definitiva.** Para confirmar 100% seria
necessário reproduzir a ordem exata do `RandomSampler` da época 2 (mesma seed)
e correlacionar índice-a-índice com o comprimento tokenizado de cada exemplo
nos passos 401–418 — não fiz isso porque exigiria instanciar um segundo
carregamento completo do modelo/tokenizer na mesma VM (CPU-only, já sob carga
de ~3.8/4 núcleos pelo treino real), o que arriscaria roubar CPU do processo
em produção só para uma confirmação adicional. Dado que todas as outras
hipóteses (memória, callback, KV-cache, I/O/contenção externa) foram
ativamente descartadas com medição direta, e que esta é a única explicação
consistente com "CPU 100% ocupada fazendo trabalho real, sem crescimento de
memória", ela fica como a hipótese líder — não como certeza absoluta.

## Não é um bug conhecido de biblioteca (até onde a checagem de versão permite dizer)

Versões na VM (`~/ft_venv`):

```
transformers 4.57.6
trl          1.12.0
peft         0.20.0
torch        2.11.0+cpu
accelerate   1.14.0
```

Buscas por "Trainer CPU training step time increasing gradually
resume_from_checkpoint" e "gradient_checkpointing + LoRA slowdown" não trazem
nenhum issue aberto/fechado que descreva especificamente esse padrão de
degradação monotônica e reversível-por-reinício. Os resultados encontrados
tratam de LR/scheduler descontínuo ao retomar checkpoint (não afeta tempo por
passo) e do trade-off constante (não crescente) de `gradient_checkpointing`
com LoRA (~30-45% mais lento, mas fixo, não progressivo). Nenhuma correlação
direta com o sintoma relatado.

## Recomendação

**Não fazer nada no treino atual** — ele deve seguir rodando até o dono decidir
matar/reiniciar. O padrão observado, se a hipótese da variância de comprimento
estiver correta, é **auto-limitante**: uma vez que a época atravesse o trecho
de exemplos longos, o tempo por passo deve voltar a cair (não é um vazamento
que só piora até o processo morrer).

Para um **próximo treino** (não aplicar no processo em execução):

1. **`group_by_length=True`** no `SFTConfig`/`TrainingArguments` — agrupa
   exemplos de comprimento parecido no mesmo lote/janela de acumulação,
   reduzindo a variância de tempo por passo (ajuda mesmo com `batch_size=1`,
   porque melhora a previsibilidade dos `grad-accum` consecutivos que compõem
   um passo de otimização).
2. Adicionar um logging leve e independente (por exemplo, um
   `TrainerCallback.on_step_end` que grava `len(input_ids)` do lote atual em
   um arquivo à parte) para, num próximo treino, correlacionar diretamente
   tempo por passo × comprimento de sequência sem precisar de `py-spy` nem de
   reproduzir o sampler a posteriori.
3. Revisitar se `--max-seq-length 1024` é o valor certo dado que p99 do
   dataset já bate nesse teto — truncar demais o percentil 99 (que
   provavelmente corresponde às regras DSL fiscais mais complexas, exatamente
   o caso mais importante de aprender) pode estar prejudicando a qualidade do
   fine-tuning tanto quanto o tempo de treino incomoda.
4. Diminuir `--save-steps` (hoje 200) reduz a perda de progresso em caso de
   reboot/crash — mitigação independente do diagnóstico acima.

## Ferramentas deixadas na VM

`py-spy` foi instalado via `pip install py-spy` na venv `~/ft_venv` durante
esta investigação (pacote leve, não interfere com o processo de treino). Pode
ser reaproveitado em investigações futuras (`py-spy dump --pid <PID>`, sem
`--locals` — esse modo travou por >60s tentando pausar o processo pra ler
variáveis locais, e foi abortado antes de completar para não arriscar
atrapalhar o treino real).
