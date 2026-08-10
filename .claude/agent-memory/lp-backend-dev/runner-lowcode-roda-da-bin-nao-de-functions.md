---
name: runner-lowcode-roda-da-bin-nao-de-functions
description: O runner low-code só roda de dentro da Bin da instância Sysmiddle. A pasta tools/LowCodeRunner/Functions/ tem um conjunto de assemblies mais antigo e derruba a init; o motivo mudou de Spring (2026-08-10 manhã) para log4net (2026-08-10 tarde).
metadata:
  type: project
---

`tools/LowCodeRunner/Functions/` **não é** um runtime home válido para o runner low-code, apesar
de ter ~306 DLLs e o `.exe` versionado dentro. Ela é cópia do subdiretório `Bin/Functions/` do
Sysmiddle (assemblies de função da DSL) e carrega um conjunto **mais antigo** que o da Bin.

O runtime home real é a **Bin da instância**
(`.claude/tmp/servidor/fiatmq/Instance_FiatMQ/AppConnector.DIR/Bin`, gitignored), exatamente como o
comentário do `.csproj` já dizia ("o exe roda DE DENTRO da Bin").

**O sintoma mudou quando o bootstrap saiu (2026-08-10).** Não confie no erro antigo:

| Quando | Erro ao rodar de `Functions/` | Origem |
|---|---|---|
| Com `Bootstrap()`/appConnector | `FileLoadException: Spring.Aop, Version=3.0.2.0` (Functions tem 2.0.1.40000) | caminho do appConnector |
| Sem bootstrap (hoje) | `FileLoadException: log4net, Version=2.0.17.0` (Functions tem assembly 1.2.13.0; Bin tem 2.0.17.0) | `SysMiddle.Base.InstanceFactory.Initialize()` |

Ou seja: remover o `appConnector` **não** destravou a pasta `Functions/` — só trocou qual assembly
diverge primeiro. O `bindingRedirect` de log4net no `App.config` aponta para 2.0.17.0, que é o da
Bin; servir as duas pastas com um `App.config` só exigiria escolher uma.

**Why:** perdi um ciclo inteiro achando que tinha quebrado o bootstrap. Só descobri que era
pré-existente compilando o fonte de `HEAD` fora da árvore (`git archive` + `-p:InstanceBin=<abs>`) e
rodando os dois lado a lado. O `.exe` versionado em `Functions/` era ainda mais antigo que o fonte e
mascarava o problema.

**How to apply:** para exercitar o runner de verdade, copie o build Release para dentro da Bin da
instância (junto do `.exe.config`) e rode de lá — nunca de `Functions/`. Mantenha o `.exe` de
`Functions/` sincronizado com o fonte mesmo assim (evita a armadilha do binário antigo), mas não
espere que ele rode dali. Antes de acusar regressão, compile o commit anterior no scratchpad com
`-p:InstanceBin` e `-p:AssemblyName=RunnerAntes` e rode os dois **alternadamente** — ver
[[bootstrap-removal-nao-e-ganho-de-tempo]] para por que alternar importa.
