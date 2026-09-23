---
name: geracao-documento-exemplo-2026-09-09
description: ADR sobre gerar documento de exemplo a partir de layout Sysmiddle (XML e posicional); achado central é que SyntheticDataGeneratorService existe mas não cobre árvore XML, e CNPJ/CPF gerado hoje não tem DV real apesar do comentário dizer o contrário.
metadata:
  type: project
---

Pedido do dono em 2026-09-09, a partir de uma POC Python (fora do repo) que anda a árvore de um
`LayoutVO` tipo `Xml` (`GroupTagElementVO`/`TagElementVO`/`AttributeElementVO`) e gera XML de
exemplo. ADR completo em
`docs/architecture/adr-geracao-documento-exemplo-2026-09-09.md` (branch
`docs/adr-geracao-documento-exemplo`, commit `04c8c5f`, não mergeada/não pushed).

## Achados principais

1. **`Services/Generation/Implementations/SyntheticDataGeneratorService.cs` já existe** e gera
   dado sintético, mas só para layout `TextPositional` (`LineElement`/`FieldElement`,
   `PadLeft`/`PadRight`) — não cobre o schema em árvore (`GroupTagElementVO` etc.) que a POC do
   dono percorre. Confirmado por grep: **não existem classes C# para
   `GroupTagElementVO`/`TagElementVO`/`AttributeElementVO`** em `Models/`/`Services/` — cobrir
   layout `Xml` é capacidade nova, não extensão.
2. **`GenerateCnpj()`/`GenerateCpf()` têm comentário enganoso**: dizem "gerar CNPJ/CPF válido
   sinteticamente" mas não calculam DV real (módulo 11) — são dígitos aleatórios com padding
   fixo. Mesma limitação da POC do dono, escondida atrás de um comentário incorreto no código
   já existente. Fix barato e desacoplado (Fase 1 do ADR).
3. **`request.UseAI` já é ignorado deliberadamente** nesse serviço desde o decommission de
   Gemini/OpenAI (2026-08-10) — geração é 100% por regra hoje, sem caminho de nuvem a reativar
   sem decisão nova.
4. **Risco de memorização de [[gemini-openai-decommission-decision]] não se aplica aqui** —
   geração é por regra determinística, não por modelo treinado sobre documento real. Confirmado
   explicitamente no ADR, não assumido.
5. **DV válido não destrava sozinho o backfill de #151/#352** — resolve rejeição estrutural
   (Pollux por CNPJ/CPF matematicamente inválido), não resolve coerência semântica entre campos
   nem existência fiscal real. Ressalva registrada explicitamente no ADR para não superprometer.

## How to apply

- Se o dono voltar a perguntar sobre geração de dado sintético/exemplo, este ADR é o ponto de
  partida — não reabrir a análise do zero.
- Se `@lp-backend-dev` for implementar a Fase 1 (DV real), apontar direto para
  `SyntheticDataGeneratorService.GenerateCnpj`/`GenerateCpf` — é o único código a tocar.
- Recomendei a `@lp-pm` avaliar issues para as Fases 1–3 e comentar na issue #151 conectando
  esta linha — não executei nenhuma das duas (fora da minha autoridade).
