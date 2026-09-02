---
name: fine-tuning-nichado-ollama-2026-09-02
description: Dono reverteu no-fine-tuning-ai-decision — autorizou fine-tuning local nichado do Ollama (LoRA/QLoRA), hardware fraco não é mais bloqueador, aceita 1-2 meses de treino; inclui plano de decodificação de functions Sysmiddle/NDD
metadata:
  type: project
---

Em 2026-09-02, após finalizar manualmente `Rule_gIBSCBSMono` (regra IBS/CBS monofásico,
NT2025.002) como exercício de referência, o dono decidiu mudar a estratégia de IA do projeto:
"treinar o Ollama... finalize o treino com o fine-tuning, aumente a capacidade do modelo se
precisar... não interessa se não tem hardware, que demore 1,2 meses". Isso reverte
[[no-fine-tuning-ai-decision]].

ADR formal: `docs/architecture/adr-fine-tuning-nichado-ollama-2026-09-02.md`. Cobre: dataset a
partir de mappers reais + pares TCL/XSLT + XSDs NT2025.002; estratégia LoRA/QLoRA (full
fine-tuning é inviável mesmo aceitando prazo longo, é limite de RAM/gradientes não só tempo);
aumento de tamanho de modelo é condicional (medir 6.7B primeiro, só subir se insuficiente
estruturalmente, `@lp-devops` mede RAM/tok-s antes); plano de decodificação de functions
Sysmiddle (comercial) e NDD (customizada) priorizado por frequência de uso real nos mappers, não
todas as functions.

**Risco residual sinalizado, não bloqueante:** decompilar a DLL comercial Sysmiddle é risco
jurídico/licença — coordenador avisou via AskUserQuestion, dono escolheu decompilar mesmo assim
(opção explicitamente rotulada "só se já há aval jurídico"). Responsabilidade da confirmação é do
dono, registrada na seção 5 do ADR para caso de precisar ser revisitada.

**Why importa:** [[production-server-hardware]] segue válida como dado factual (specs da
máquina), mas sua conclusão prática ("não investir em treino") foi sobrescrita por decisão
explícita do dono — não usar mais para recomendar contra treino.

**How to apply:** qualquer trabalho futuro de `@lp-parser-llm`/`@lp-devops` sobre treino/fine-
tuning do Ollama parte deste ADR, não da memória antiga. Continuação prática do ADR #258
([[track-a2-a5-spec]] não é o mesmo, ver `adr-artefatos-gerados-redis-workspace-funcoes-2026-09-02.md`).
