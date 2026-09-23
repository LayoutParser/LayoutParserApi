---
name: finetuning-branches-orfas-2026-09-02
description: Experimento anterior completo de fine-tuning (branches órfãs 2026-08-29 a 09-01) achado e consolidado antes de reiniciar treino LoRA em 2026-09-02; corrigiu bug de masking real no train_lora.py.
metadata:
  type: project
---

Em 2026-09-02, ao iniciar um novo treino LoRA (`ai/XslSynth/training-data/train_lora.py`,
dataset `sysmiddle-dsl-dataset-2026-09-02.jsonl`, 6044 exemplos, `Qwen2.5-Coder-1.5B-Instruct`,
3 épocas fixas, sem split), descobri que um experimento **idêntico já tinha sido rodado por
completo** entre 2026-08-29 e 2026-09-01, em branches órfãs nunca mergeadas em `develop`:
`worktree-agent-a2d770dd5f356804b`, `worktree-agent-a9c5e2f1d2f8a824b`,
`worktree-agent-a1201eb5f00d4438f`, `worktree-agent-a81d1b2e6585643e7`,
`worktree-agent-a10c96231bc44d391`, `worktree-agent-a2d0a9d02a126d470`,
`feat-import-checkpoints`, `feat-import-checkpoints-v2`. Doc principal só existe nessas
branches: `docs/architecture/plano-finetuning-especializacao-mapeamento-sysmiddle-2026-08-29.md`.

**Achados relevantes da sessão anterior (validados, não redescobrir):**
1. **3 épocas fixas sem validação degeneraram** — com 1 época o modelo gerava XSLT real com
   defaults semânticos corretos (smoke-test #4); com 3 épocas fixas virou eco puro do `.tcl` de
   entrada (`grep -c 'xsl:'` = 0). Recomendação explícita: nunca fixar nº de épocas sem
   split de validação/early stopping, medir geração a cada época.
2. **Bug de masking de labels era real e foi corrigido lá** (commit `8943a759`,
   `worktree-agent-a10c96231bc44d391`): `labels=input_ids.copy()` sem mascarar o prompt fazia a
   loss treinar sobre prompt+completion inteiros, não só a completion. Corrigido no script
   daquela sessão (`smoke_train_ckpt_masked.py`), loss ficou mais saudável (0.40-0.50 vs
   0.70-0.86), mas a degeneração de geração continuou — masking não era a causa dominante lá
   (suspeitos remanescentes: dataset de 57 chunks de 1 par só, prompt truncado a 1024 tokens de
   um `.tcl` real de 10101 tokens).
3. **Checkpoints intermediários nunca existiam** (`save_strategy="no"` no script daquela sessão,
   achado do commit `169a0de`) — impossível voltar pra época 1 depois do fato; e a VM tem teto
   duro de RAM (15GB): treino (~11.8GB pico) + `generate()` concorrentes (~6.6GB) excedem e o SO
   mata o treino silenciosamente. Nunca rodar treino e inferência juntos nesta VM.
4. Qwen2.5-Coder 7B avaliado e descartado — não cabe nos 15GB RAM da VM; 1.5B confirmado como
   escolha certa de tamanho (consistente com [[dev-machine-gpu-constraints]] e
   [[production-server-hardware]] no MEMORY.md do usuário).

**O que corrigi no `train_lora.py` atual (2026-09-02), antes de reiniciar:**
- Confirmei que o MESMO bug de masking do item 2 estava presente aqui também: o dataset usava
  um único campo `"text"` (prompt+completion concatenados) com `dataset_text_field="text"` no
  `SFTConfig` — em `trl==1.12.0` isso é tratado como "language modeling dataset" e a loss roda
  sobre a sequência inteira, independente de `completion_only_loss` (confirmado lendo o
  dataclass `SFTConfig.completion_only_loss` em runtime na VM: só tem efeito real com dataset em
  formato `prompt`/`completion`). Corrigido trocando `load_dataset()` para produzir
  `{"prompt": ..., "completion": ...}` e setando `completion_only_loss=True` explicitamente. Log
  do novo treino confirma "Dropping fully masked examples" — sinal de que o masking está ativo.
- Adicionei split treino/validação 90/10 com seed fixa (`split_train_eval`, seed=42 default) —
  6044 exemplos → 5440 treino / 604 validação.
- `eval_strategy="steps"` + `eval_steps=200` + `load_best_model_at_end=True` +
  `metric_for_best_model="eval_loss"` + `greater_is_better=False`.
- `EarlyStoppingCallback(early_stopping_patience=3)` (avaliações sem melhora, não épocas).
- Mantive `save_strategy="steps"`/`save_steps`/`save_total_limit=3` (checkpoints intermediários
  reais existem desta vez, ao contrário do achado 3 acima).
- Versões confirmadas na VM: `transformers==4.57.6`, `trl==1.12.0`, `peft==0.20.0` — API de
  `SFTConfig` já suporta `eval_strategy`/`load_best_model_at_end`/`completion_only_loss`
  nativamente, sem precisar de wrapper manual.

**Treino relançado:** PID 247915 na VM `172.25.32.5`, `--output-dir ~/lora-out-v2` (script
antigo `--output-dir ~/lora-out` do PID 242117 morto fica intocado para comparação se quiser).
`nohup ... > ~/train_lora_v2.log 2>&1 & disown`.

**Pendência sinalizada, não meu escopo agora:** as 8 branches órfãs acima (+ possivelmente mais
com "smoke-test"/"finetuning" na mensagem de commit) contêm conhecimento valioso nunca
consolidado em `develop`. Vale um PR de documentação dedicado (`@lp-doc` ou `@lp-architect`)
trazendo `docs/architecture/plano-finetuning-especializacao-mapeamento-sysmiddle-2026-08-29.md`
pra `develop` antes que as branches sejam limpas/esquecidas. Decisão de fazer esse PR é à parte,
não tomada aqui.

Relacionado: [[no-fine-tuning-ai-decision]] (memória de usuário, decisão anterior revertida
depois pelo dono em 2026-08-29 — ver o próprio doc do plano para o histórico da reversão),
[[finetuning-poc-fase1-dataset]], [[finetuning-vm-hardware-toolchain]].
