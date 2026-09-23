#!/usr/bin/env python3
"""
Fine-tuning LoRA (CPU-only) do modelo base pequeno em cima do dataset real de
regras DSL Sysmiddle (extraidas do package 938f9978, ~170 mapeadores reais).

Modelo base: Qwen/Qwen2.5-Coder-1.5B-Instruct
  - 1.5B parametros, treinado com foco em codigo/instrucao -> viavel em CPU puro
    para LoRA (rank baixo, batch pequeno), diferente do qwen2.5-coder:7b usado hoje
    so para inferencia via Ollama.
  - Suporta formato de chat (instruction/output) nativamente via seu chat template,
    o que casa bem com o formato do dataset (instruction/input/output).

Dataset: ai/XslSynth/training-data/sysmiddle-dsl-dataset-2026-09-02.jsonl
  Cada linha: {"instruction": "...", "input": "...", "output": "<regra DSL real>"}

Uso:
  python3 train_lora.py \
      --dataset sysmiddle-dsl-dataset-2026-09-02.jsonl \
      --output-dir ./lora-out \
      --epochs 3 \
      --batch-size 1 \
      --grad-accum 16

Rodar em background na VM (persistente a desconexao SSH):
  nohup ~/ft_venv/bin/python3 train_lora.py --dataset ... --output-dir ~/lora-out \
      > ~/train_lora.log 2>&1 &
"""
import argparse
import glob
import json
import os
import random
import re

from datasets import Dataset
from peft import LoraConfig, get_peft_model
from transformers import AutoModelForCausalLM, AutoTokenizer, EarlyStoppingCallback
from trl import SFTTrainer, SFTConfig

BASE_MODEL = "Qwen/Qwen2.5-Coder-1.5B-Instruct"

# NOTA (2026-09-02): formato prompt/completion, NAO um unico campo "text".
#
# Bug de masking encontrado e corrigido em sessao anterior de fine-tuning
# (branch orfa worktree-agent-a10c96231bc44d391, commit 8943a759, nunca
# mergeada em develop): quando o dataset expoe um unico campo de texto livre
# (aqui era "text"), o SFTTrainer trata como "language modeling dataset" e
# calcula a loss sobre a sequencia INTEIRA (prompt + completion), nao so
# sobre a completion. Isso faz o modelo aprender a "prever o proprio
# prompt" tambem, o que pesou na degeneracao (eco puro do TCL de entrada)
# observada com 3 epocas fixas.
#
# Corrigido usando o formato prompt/completion nativo do trl>=1.x
# (SFTConfig.completion_only_loss=True so tem efeito com esse formato -
# confirmado lendo o dataclass em runtime: com campo "text" unico o efeito
# e sempre loss na sequencia inteira, independente da flag).
PROMPT_TEMPLATE = (
    "### Instrucao:\n{instruction}\n\n"
    "### Contexto (elemento pai / campo alvo):\n{input}\n\n"
    "### Regra DSL Sysmiddle:\n"
)


def load_dataset(path: str) -> Dataset:
    rows = []
    with open(path, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            obj = json.loads(line)
            prompt = PROMPT_TEMPLATE.format(
                instruction=obj.get("instruction", ""),
                input=obj.get("input", ""),
            )
            completion = obj.get("output", "")
            rows.append({"prompt": prompt, "completion": completion})
    return Dataset.from_list(rows)


def find_latest_checkpoint(output_dir: str):
    """Procura o checkpoint mais recente em output_dir (padrao checkpoint-<step>
    salvo pelo Trainer/SFTTrainer com save_strategy="steps"). Retorna o caminho
    absoluto do checkpoint de maior step, ou None se nao houver nenhum.

    Usado para retomar o treino automaticamente apos reboot/crash da VM, sem
    exigir que quem chama o script saiba o numero exato do ultimo step.
    """
    if not os.path.isdir(output_dir):
        return None

    candidates = []
    for path in glob.glob(os.path.join(output_dir, "checkpoint-*")):
        if not os.path.isdir(path):
            continue
        match = re.search(r"checkpoint-(\d+)$", path)
        if match:
            candidates.append((int(match.group(1)), path))

    if not candidates:
        return None

    candidates.sort(key=lambda item: item[0])
    return candidates[-1][1]


def split_train_eval(ds: Dataset, eval_fraction: float, seed: int) -> tuple[Dataset, Dataset]:
    """Split treino/validacao com seed fixa (90/10 por padrao).

    Sem isso nao ha como medir overfitting por epoca (achado da sessao anterior:
    treino com numero fixo de epocas, sem validacao, degenerou na 3a epoca sem
    nenhum sinal objetivo ate a geracao final ser inspecionada manualmente).
    """
    n = len(ds)
    n_eval = max(1, int(n * eval_fraction))
    indices = list(range(n))
    random.Random(seed).shuffle(indices)
    eval_idx = set(indices[:n_eval])
    train_idx = [i for i in indices if i not in eval_idx]
    return ds.select(train_idx), ds.select(sorted(eval_idx))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--dataset", required=True, help="Caminho do JSONL de treino")
    parser.add_argument("--output-dir", default="./lora-out", help="Diretorio de saida do adapter LoRA")
    parser.add_argument("--base-model", default=BASE_MODEL, help="Modelo base do HuggingFace")
    parser.add_argument("--epochs", type=float, default=3.0)
    parser.add_argument("--batch-size", type=int, default=1)
    parser.add_argument("--grad-accum", type=int, default=16)
    parser.add_argument("--lr", type=float, default=2e-4)
    parser.add_argument("--max-seq-length", type=int, default=1024)
    parser.add_argument("--lora-r", type=int, default=8)
    parser.add_argument("--lora-alpha", type=int, default=16)
    parser.add_argument("--save-steps", type=int, default=200)
    parser.add_argument("--logging-steps", type=int, default=10)
    parser.add_argument("--eval-fraction", type=float, default=0.1, help="Fracao do dataset reservada para validacao (90/10 por padrao)")
    parser.add_argument("--eval-steps", type=int, default=200)
    parser.add_argument("--seed", type=int, default=42, help="Seed do split treino/validacao")
    parser.add_argument("--early-stopping-patience", type=int, default=3, help="Numero de avaliacoes sem melhora antes de parar")
    args = parser.parse_args()

    os.makedirs(args.output_dir, exist_ok=True)

    print(f"[train_lora] carregando dataset de {args.dataset}")
    ds_full = load_dataset(args.dataset)
    print(f"[train_lora] {len(ds_full)} exemplos carregados")

    ds, eval_ds = split_train_eval(ds_full, args.eval_fraction, args.seed)
    print(f"[train_lora] split treino/validacao (seed={args.seed}): {len(ds)} treino / {len(eval_ds)} validacao")

    print(f"[train_lora] carregando tokenizer/modelo base: {args.base_model}")
    tokenizer = AutoTokenizer.from_pretrained(args.base_model)
    if tokenizer.pad_token is None:
        tokenizer.pad_token = tokenizer.eos_token

    model = AutoModelForCausalLM.from_pretrained(
        args.base_model,
        torch_dtype="float32",  # CPU-only: sem fp16/bf16 acelerado
        low_cpu_mem_usage=True,
    )

    lora_config = LoraConfig(
        r=args.lora_r,
        lora_alpha=args.lora_alpha,
        lora_dropout=0.05,
        bias="none",
        task_type="CAUSAL_LM",
        target_modules=["q_proj", "k_proj", "v_proj", "o_proj"],
    )
    model = get_peft_model(model, lora_config)
    model.print_trainable_parameters()

    sft_config = SFTConfig(
        output_dir=args.output_dir,
        num_train_epochs=args.epochs,
        per_device_train_batch_size=args.batch_size,
        per_device_eval_batch_size=args.batch_size,
        gradient_accumulation_steps=args.grad_accum,
        learning_rate=args.lr,
        logging_steps=args.logging_steps,
        save_strategy="steps",
        save_steps=args.save_steps,
        save_total_limit=3,
        eval_strategy="steps",
        eval_steps=args.eval_steps,
        load_best_model_at_end=True,
        metric_for_best_model="eval_loss",
        greater_is_better=False,
        report_to=[],
        max_length=args.max_seq_length,
        # Formato prompt/completion (ver comentario acima de PROMPT_TEMPLATE):
        # completion_only_loss=True so mascara o prompt de fato com esse
        # formato de dataset - com um campo "text" unico a flag nao tem efeito
        # (confirmado lendo trl 1.12 SFTConfig em runtime nesta sessao).
        completion_only_loss=True,
        bf16=False,
        fp16=False,
        optim="adamw_torch",
        gradient_checkpointing=True,
    )

    trainer = SFTTrainer(
        model=model,
        args=sft_config,
        train_dataset=ds,
        eval_dataset=eval_ds,
        processing_class=tokenizer,
        callbacks=[EarlyStoppingCallback(early_stopping_patience=args.early_stopping_patience)],
    )

    resume_checkpoint = find_latest_checkpoint(args.output_dir)
    if resume_checkpoint:
        print(f"[train_lora] checkpoint encontrado em {resume_checkpoint} — retomando treino a partir dele")
    else:
        print("[train_lora] nenhum checkpoint encontrado — iniciando treino do zero")

    print("[train_lora] iniciando treino (isso deve rodar por horas/dias em CPU)")
    trainer.train(resume_from_checkpoint=resume_checkpoint)

    print(f"[train_lora] salvando adapter final (melhor checkpoint por eval_loss) em {args.output_dir}")
    trainer.save_model(args.output_dir)
    tokenizer.save_pretrained(args.output_dir)
    print("[train_lora] concluido")


if __name__ == "__main__":
    main()
