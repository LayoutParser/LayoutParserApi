---
name: runner-lowcode-roda-da-bin-nao-de-functions
description: O runner low-code só bootstrapa de dentro da Bin da instância Sysmiddle (Spring 3.0.2). A pasta tools/LowCodeRunner/Functions/ tem Spring 2.0.1 e derruba o bootstrap com FileLoadException.
metadata:
  type: project
---

`tools/LowCodeRunner/Functions/` **não é** um runtime home válido para o runner low-code, apesar
de ter ~306 DLLs e o `.exe` versionado dentro. Ela é cópia do subdiretório `Bin/Functions/` do
Sysmiddle (assemblies de função da DSL) e carrega **Spring.Aop/Spring.Core 2.0.1.40000**. O
`.csproj` compila contra a Bin da instância v4.4.1, que exige **3.0.2.0** — rodar de dentro de
`Functions/` mata o bootstrap com `FileLoadException: Spring.Aop, Version=3.0.2.0`.

O runtime home real é a **Bin da instância**
(`.claude/tmp/servidor/fiatmq/Instance_FiatMQ/AppConnector.DIR/Bin`, gitignored), exatamente como o
comentário do `.csproj` já dizia ("o exe roda DE DENTRO da Bin"). De lá, verificado em 2026-08-10:
bootstrap em ~0,6s, `LIST` com exit=0 e uma transformação real do par de `.claude/tmp/exemplos/`
(mapper FIAT `MAP_f31a6758-69c9-4cf6-92d2-24f0e27a1ab5`) saindo byte a byte igual ao gabarito, com a
única diferença de um espaço duplo em `<?xml  version=` (vem do produtor do gabarito, não do runner —
confirmado rodando com e sem `--fileName`, saídas idênticas).

**Why:** perdi um ciclo inteiro achando que tinha quebrado o bootstrap. Só descobri que era
pré-existente compilando o fonte de `HEAD` fora da árvore (`git archive` + `-p:InstanceBin=<abs>`) e
rodando os dois lado a lado. O `.exe` que estava versionado em `Functions/` era ainda mais antigo que
o fonte — nem tinha o `Bootstrap()`, então nunca chegava a tocar em Spring e mascarava o problema.

**How to apply:** para exercitar o runner de verdade, copie o build Release para dentro da Bin da
instância (junto do `.exe.config`) e rode de lá — nunca de `Functions/`. Antes de acusar regressão no
bootstrap/licença, compile `HEAD` no scratchpad com `-p:InstanceBin` e compare; a diferença quase
sempre é ambiente, não código. O tempo de processo é alto e importa: **~22s só até o `LIST`
responder** e **~58s** numa transformação real a frio (bootstrap 0,6s + init da InstanceFactory ~9s
+ `ExecuteMappingDocumentById` ~47s) — muito acima de `LowCode:RunnerTimeoutSeconds = 15`. Ver
[[lowcode-nunca-rodou-em-producao]] e [[validar-suite-nova-por-mutacao]].
