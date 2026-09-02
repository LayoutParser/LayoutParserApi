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
import json
import os

from datasets import Dataset
from peft import LoraConfig, get_peft_model
from transformers import AutoModelForCausalLM, AutoTokenizer, TrainingArguments
from trl import SFTTrainer, SFTConfig

BASE_MODEL = "Qwen/Qwen2.5-Coder-1.5B-Instruct"

PROMPT_TEMPLATE = (
    "### Instrucao:\n{instruction}\n\n"
    "### Contexto (elemento pai / campo alvo):\n{input}\n\n"
    "### Regra DSL Sysmiddle:\n{output}"
)


def load_dataset(path: str) -> Dataset:
    rows = []
    with open(path, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            obj = json.loads(line)
            text = PROMPT_TEMPLATE.format(
                instruction=obj.get("instruction", ""),
                input=obj.get("input", ""),
                output=obj.get("output", ""),
            )
            rows.append({"text": text})
    return Dataset.from_list(rows)


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
    args = parser.parse_args()

    os.makedirs(args.output_dir, exist_ok=True)

    print(f"[train_lora] carregando dataset de {args.dataset}")
    ds = load_dataset(args.dataset)
    print(f"[train_lora] {len(ds)} exemplos carregados")

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
        gradient_accumulation_steps=args.grad_accum,
        learning_rate=args.lr,
        logging_steps=args.logging_steps,
        save_steps=args.save_steps,
        save_total_limit=3,
        report_to=[],
        max_length=args.max_seq_length,
        dataset_text_field="text",
        bf16=False,
        fp16=False,
        optim="adamw_torch",
        gradient_checkpointing=True,
    )

    trainer = SFTTrainer(
        model=model,
        args=sft_config,
        train_dataset=ds,
        processing_class=tokenizer,
    )

    print("[train_lora] iniciando treino (isso deve rodar por horas/dias em CPU)")
    trainer.train()

    print(f"[train_lora] salvando adapter final em {args.output_dir}")
    trainer.save_model(args.output_dir)
    tokenizer.save_pretrained(args.output_dir)
    print("[train_lora] concluido")


if __name__ == "__main__":
    main()
