---
name: bootstrap-removal-nao-e-ganho-de-tempo
description: Remover o Bootstrap() do runner low-code tirou uma dependência e um crash, não tempo — o bootstrap era ~1s de uma execução de 48-130s. O custo real é o mapeador + init do APIManager, que ficaram.
metadata:
  type: project
---

A remoção do `Bootstrap()`/`appConnector` do runner low-code (2026-08-10) **não é uma otimização**.
Medido com as duas builds lado a lado, alternadas na mesma Bin:

| Fase | Custo | Saiu? |
|---|---|---|
| `Bootstrap()` (`EDocsClientConnectorManager.Start`) | **0,7-1,5s** | sim |
| Init `InstanceFactory` + `APIManager` + `GetApiExecutorByIdentifier` (gate de licença) | 12-38s | **não** — é o SDK, fica |
| `ExecuteMapper` (o mapeador em si) | 38-73s | **não** — é o SDK, fica |

**Why:** a hipótese registrada em `decisao-remover-dependencia-appconnector.md` §2 era que sairiam
"~9s de init da InstanceFactory + 0,6s de bootstrap". Está errada na atribuição: os ~9s são do
`APIManager`/licença, disparados pelo primeiro acesso a `APIManager.Instance`, e **continuam lá**.
O bootstrap sozinho era ~1-2% do total. `LowCode:RunnerTimeoutSeconds = 15` continua
irremediavelmente abaixo do custo real — isso **não** foi resolvido.

O ganho real é outro e é bom: (a) a dependência de `appConnector.Client.Core*` morreu, (b) sumiu um
crash — o bootstrap subia uma thread de PRIMEIRO plano `TH_FAI`
(`DirectoryFailureManager.DirectoryFailureProcess`) que estourava `ArgumentNullException` não tratada
e matava o processo antes de transformar (1 em 3 execuções na linha de base), e (c) sem ela o
processo **encerra sozinho** — verificado com uma build sem `Environment.Exit` (exit=0, saída
intacta), o que torna viável hospedar o executor in-process num projeto de teste.

**How to apply:** nunca comparar tempos do runner com medições de dias diferentes — nesta máquina a
MESMA build variou 48s → 137s, e a fase do mapeador (código idêntico) variou 38s → 73s. Se precisar
de um número, compile o commit-base no scratchpad (`git archive <sha> tools/LowCodeRunner`,
`-p:InstanceBin=<abs> -p:AssemblyName=RunnerAntes`), ponha os dois `.exe` na mesma Bin e rode
**alternado** A/B/A/B; mesmo assim espere que o ruído engula diferenças menores que ~2x. Para
diferenças pequenas, prefira as fases do próprio log (`[EXEC]` → `[OK]`) a wall-clock.
Ver [[runner-lowcode-roda-da-bin-nao-de-functions]].
