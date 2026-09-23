---
name: finetuning-resume-apos-reboot
description: Retomada automatica do fine-tuning LoRA v2 apos reboot da VM Ollama (172.25.32.5) — script + cron @reboot
metadata:
  type: project
---

Em 2026-09-02, com o fine-tuning LoRA v2 rodando na VM (172.25.32.5, usuario `elson`,
PID 247915, script `~/train_lora.py`, dataset `~/sysmiddle-dsl-dataset-2026-09-02.jsonl`,
saida `~/lora-out-v2/`, log `~/train_lora_v2.log`, ETA ~3 dias em CPU puro — ver
[[no-fine-tuning-ai-decision]] e [[dev-machine-gpu-constraints]] no MEMORY.md geral para o
contexto de por que é CPU-only), foi adicionado suporte a retomada automatica de checkpoint.

## O que foi implementado

1. **`ai/XslSynth/training-data/train_lora.py`** (branch `feat/finetuning-resume-apos-reboot`,
   a partir de `develop`): funcao `find_latest_checkpoint(output_dir)` faz `glob` por
   `checkpoint-<N>` dentro de `--output-dir`, extrai o maior N via regex e retorna o path
   absoluto (ou `None` se não houver nenhum). `trainer.train(resume_from_checkpoint=resume_checkpoint)`
   substitui o antigo `trainer.train()` sem argumento — quando `None`, o comportamento é
   idêntico ao anterior (começa do zero), então é **retrocompativel** com quem já chamava o
   script sem essa lógica.
2. **`~/resume_training.sh`** na VM (script wrapper, não uma linha gigante no crontab):
   dorme 30s (estabilizar rede/disco pós-boot), checa `pgrep -f train_lora.py` — se já tem
   processo rodando, só loga e sai (idempotente a reboots múltiplos/cron duplicado); se não
   tem, dispara o mesmo comando de hoje via `nohup ... >> ~/train_lora_v2.log 2>&1 & disown`
   (note `>>` append, não `>`, para preservar o log já acumulado).
3. **Crontab do usuário `elson`** (sem sudo, `crontab -e`/`crontab -` direto):
   `@reboot /home/elson/resume_training.sh # lora-finetune-resume-apos-reboot` — adicionado
   preservando a entrada pré-existente (`layoutparser-ai-metrics-batch`, sábado 00:00).
4. **`train_lora.py` atualizado foi copiado para a VM por cima do arquivo antigo** (`scp`),
   mas o **treino em andamento (PID 247915) não foi reiniciado** — ele continua rodando com o
   código antigo carregado em memória (Python já importou o módulo); a lógica de resume só
   vale para a *próxima* execução (reboot ou manual).

## Gotcha: validação da lógica de resume ficou pendente por timing, não por bug

No momento da implementação (2026-09-02, ~3h22 de treino), o treino estava no **step 41/1020**
com `save_steps=200` (default) — **nenhum checkpoint existia ainda** em `~/lora-out-v2/`
(dir vazio). Não foi possível validar contra um checkpoint real sem esperar mais tempo, e
rodar o script manualmente para testar teria disparado um **segundo treino concorrente** com o
PID 247915 ativo (dois processos CPU-bound competindo por núcleos — pioraria drasticamente o
ETA de ambos). A validação foi feita **só por leitura de código**: a lógica
(`glob` + regex + `sort` pelo maior N) está correta para o padrão de diretório que o
`Trainer`/`SFTTrainer` do HuggingFace cria (`checkpoint-<step>`), mas **ainda não foi exercitada
contra um checkpoint real**. Quando o primeiro checkpoint aparecer (por volta do step 200,
~dia 1 de treino), vale conferir uma vez com `ls ~/lora-out-v2/` que o formato bate com o
esperado — se o HF mudar o formato de nome em alguma versão, a regex `checkpoint-(\d+)$`
precisaria de ajuste.

## Por que isso importa

VM sem GPU rodando ~3 dias em CPU puro é um investimento de tempo alto por tentativa
(ver [[no-fine-tuning-ai-decision]]) — perder um reboot no meio do caminho sem retomada
automática significaria recomeçar do zero e perder dias de progresso. A automação cobre
reboot inesperado (queda de energia, patch do host, etc.) sem exigir que alguém esteja de
plantão pra reiniciar manualmente.
