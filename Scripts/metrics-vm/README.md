# Scripts da VM de métricas de IA

Espelho versionado dos scripts que operam o Job 1 de métricas de geração de IA
(XslSynth `--mode=metrics-batch`).

## Onde eles rodam de verdade

- **VM Linux:** hostname `UBU220405RUN` (IP pode mudar — hoje `172.25.32.5`).
- **Usuário:** `elson`.
- **Pasta operacional:** `/home/elson/layoutparser-ai-metrics/`.
- **Job:** roda o dataset completo (54 pares, `dataset_pairs_filtered_v2.jsonl`) contra
  `qwen2.5-coder:7b` via Ollama local, semanalmente aos sábados às 00:00 (cron do usuário
  `elson`). Essa mesma VM também hospeda o Ollama usado pelo job.

**A VM é a fonte operacional — este diretório no repo é só o espelho versionado.** Editar
aqui não muda o comportamento em produção até que a mudança seja sincronizada manualmente
para a VM.

## O que NÃO está versionado aqui

A pasta `/home/elson/layoutparser-ai-metrics/` na VM também contém:

- Binários publicados do XslSynth (DLLs, runtime do .NET);
- O dataset (`dataset/dataset_pairs_filtered_v2.jsonl`);
- Logs (`logs/`).

Nenhum desses itens é versionado neste repositório — só os 3 scripts shell abaixo.

## Arquivos

| Arquivo | Função |
|---------|--------|
| `run-metrics-batch.sh` | Executa o batch de métricas (chamado pelo cron). |
| `enable-metrics-job.sh` | Cria/atualiza a entrada de cron (sábado 00:00), idempotente. |
| `disable-metrics-job.sh` | Remove a entrada de cron. |

## Como sincronizar (repo → VM)

Depois de alterar um script aqui e mergear, copie manualmente para a VM preservando a
permissão de execução:

```bash
scp scripts/metrics-vm/*.sh elson@172.25.32.5:/home/elson/layoutparser-ai-metrics/
ssh elson@172.25.32.5 "chmod +x /home/elson/layoutparser-ai-metrics/*.sh"
```

Se o IP da VM tiver mudado, confirme o endereço atual antes de copiar.
