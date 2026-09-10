# Handoff — F4 retraining automatizado: o que falta no lado da VM (issue #351)

Destino: `@lp-devops`. Origem: `@lp-parser-llm`. Refere-se ao ADR
`docs/architecture/adr-backfill-catalogo-e-retraining-automatizado-2026-09-08.md`.

## O que já está pronto em C# (branch `feat/retraining-automatizado-f4-351`)

| Sub-fase | Implementado | Onde |
|---|---|---|
| **F4.1** | Telemetria de `DuracaoSegundos`/`Iteracoes`/`Convergiu`/`XsdValido` do `RepairOrchestrator` em runtime, linha `Source=AiMetrics` (`RepairOrchestrator runtime concluido. ...`). Emitida sempre que `RunAsync` retorna, convergindo ou não. | `Services/Transformation/Ai/RepairOrchestratorXslSynthesizerService.cs` (`LogRuntimeMetrics`) |
| **F4.2** | Contador de exemplos novos alimentado pelo hook de F3; estado durável em JSON; avaliador de gatilho (volume ≥ N **OU** ≥ 90 dias); background service que avalia a cada 6h; escrita do arquivo-marcador `retraining.trigger.json`; detecção de conclusão. | `Services/Transformation/Ai/Retraining/*` |
| **F4.3** | Checagem de `retraining.lock` **antes de acionar o Ollama** em runtime (recusa graciosa, sem exceção); coordenador não dispara treino com lock ativo; comparador de métricas pós-treino (regressão → não promove). | `FileRetrainingLock.cs`, `RepairOrchestratorXslSynthesizerService.cs`, `ModelMetricsComparer.cs` |

Config: seção `XslSynth:Retraining` no `appsettings.json`, **`Enabled: false`**. Com `false`, a
API só mantém o contador e lê o lock — nunca escreve o marcador de disparo.

Caminhos default (todos relativos a `XslSynth:TrainingDataPath`, na VM `172.25.32.5`):
- `retraining-state.json` — contador + timestamps (a API é dona)
- `retraining.trigger.json` — marcador de disparo (a API escreve, **a VM apaga**)
- `retraining.lock` — exclusão mútua (**a VM cria e apaga**, a API só lê)

## O que falta no lado da VM (`@lp-devops`)

1. **Cron/serviço na VM `172.25.32.5`** que, em intervalo curto (ex. a cada 15 min):
   - checa se `retraining.trigger.json` existe **e** `retraining.lock` **não** existe;
   - se sim: cria `retraining.lock` (com PID + timestamp), roda
     `python3 ~/ft_venv/.../train_lora.py` (o script de treino já resumível, ver
     `feat/finetuning-resume-apos-reboot`), com o dataset = JSONL batch atual +
     `runtime-capture-*.jsonl` acumulados;
   - ao terminar o treino: roda a **validação pós-treino** (passo 2 abaixo);
   - **só então** apaga `retraining.trigger.json` e `retraining.lock` (nessa ordem).
   - A API detecta a conclusão sozinha no próximo tick (marcador sumiu + lock livre) e zera
     o contador, preservando o que F3 capturou durante as ~40h.

2. **Validação pós-treino antes de promover** (ADR §6.3):
   - rodar `ai/XslSynth --mode=metrics-batch` com o modelo novo contra o held-out set atual;
   - montar dois `ModelMetricsSnapshot` (atual em produção × candidato) — taxas de convergência,
     XSD válido e diff==0;
   - a regra de decisão já está em `ModelMetricsComparer.Decide(...)` (C#). No lado da VM, ou se
     replica a regra em shell (candidato regride em qualquer das 3 taxas além da tolerância →
     não promove), ou expõe-se um pequeno endpoint/CLI que chama o comparador. Recomendo replicar
     em shell — é uma comparação de 3 números, não vale uma round-trip HTTP.
   - **promover** = trocar a tag/symlink do Ollama (`layoutparser-sysmiddle-dsl:1.5b`) para o
     GGUF novo (merge + convert + `Q4_K_M` + `ollama create`). Se regrediu: manter o modelo
     atual, arquivar o candidato, logar.

3. **Alerta de lock órfão**: a API já loga warning se `retraining.lock` passa de
   `StaleLockAfterHours` (default 60h) — encaminhar esse warning pro canal de alerta de deploy
   (e-mail, `deploy.yml`) ou um cron simples que cheque a idade do arquivo. A API **não** remove
   o lock sozinha de propósito (decisão operacional).

4. **`Enabled: true`** no ambiente de produção (`XslSynth__Retraining__Enabled=true`) só depois
   que o item 1 estiver no ar — antes disso o marcador nunca é escrito e o resto é inócuo.

## Fora de escopo (ADR §5, §8)

Backfill em lote sobre o catálogo (sem corpus real) e piloto de 1 layout — não entram aqui.
Enumeração de mappers, se algum dia necessária, usa o método filtrado por
`ProjectId`/`AllowedPackageGuids` de `MapperDatabaseService` (~linha 284), nunca
`GetAllMappersAsync`.
