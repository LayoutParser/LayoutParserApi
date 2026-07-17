---
name: track-a2-a5-spec
description: Contrato consolidado de A2-A5 da Trilha A (§8) e veredito de execução real pós-Lia (§9)
metadata:
  type: project
---

Especificação objetiva de A2 (variantes fiscais ICMS/CSOSN), A3 (GuidXPathCatalog), A4
(LayoutSpecExtractor/G3) e A5 (generalizar MapperEmissionGuide/G4) está em
`docs/architecture/multi-session-execution-plan.md` §8, escrita em 2026-07-16 e **revisada em
2026-07-17** após `@lp-parser-llm` (Lia) executar de fato A1-A5 na branch
`feat/lowcode-batch-mode`. §9 foi adicionada com o veredito geral.

**Estado real (2026-07-17), por fase:**
- **A1**: confirmado com dado real — SWEEP rodou contra 4 clientes (FIAT/CNHI/IVECCO/MARELLI),
  100% bem-formados. Achado colateral: bug intermitente de interop no runner (1ª chamada
  pós-cópia do `.exe` não distingue `SWEEP`), sem causa-raiz — risco a monitorar se alguém
  automatizar o SWEEP sem retry.
- **A2**: parcial (1/2 clientes) — CNHI fecha diff==0, IVECCO tem 5 gaps concretos e
  diagnosticados (normalize-space colapsando espaço duplo, homônimos xPed/nItemPed,
  dest/enderDest/fone ausente, grupo IBSCBSTot/Reforma Tributária sem suporte, separador de
  infCpl variando por cliente). Reclassificada como bugfix dirigido, não redesenho — a premissa
  original ("único gabarito FIAT") já não procedia, `XslGenerator.cs` já generalizava por CST
  antes desta rodada.
- **A3**: praticamente completa, 235/237 LinkMappings resolvidos (2 restantes = exclusão por
  design). `layout-nfe.xml` é o LayoutVO real do Connect Us que destravou isso. Dois bugs
  corrigidos por Lia no próprio código (encoding UTF-16/UTF-8; `.Trim()` faltando em
  `<ElementGuid>`/`<Name>` no pretty-print) — sinalizei revisão de `@lp-qa` antes de considerar
  100% fechada, porque foram corrigidos como efeito colateral sem teste dedicado.
- **A4**: bloqueio real e não-negociável — falta o LayoutVO de ENTRADA (`LAY_ad4fb6f4-…`)
  exportado do Connect Us. Lia recusou corretamente implementar com dado fictício
  (`layout-mqseries.xml` tem GUID diferente). Ação concreta pendente do usuário: exportar esse
  LayoutVO específico para `Documentos/Layout/`.
- **A5**: minha spec original ("resolve 8 campos hardcoded") estava **desatualizada**, não
  errada — o motor já é parametrizado por `mapperVoPath` (qualquer mapeador) e extrai qualquer
  path sob guarda DSL `!= 0` via regex, não uma lista estática de 8 nomes. Distinção real
  registrada: genérico na SINTAXE (padrão `!= 0` não-aninhado), não necessariamente na SEMÂNTICA
  completa de condicionalidade fiscal (não cobre `> 0`, blocos aninhados, `IsNullOrEmpty` como
  guarda). Sem evidência ainda de que essa amplitude maior seja necessária — FIAT/CNHI só usam o
  padrão coberto. Falta validar contra IVECCO/MARELLI para descartar padrões não cobertos.

**Lição para specs futuras:** ao escrever contrato de fase baseado em premissas de código ainda
não lidas linha a linha (ex.: "isso resolve só 8 campos"), marcar explicitamente como "premissa a
confirmar" em vez de afirmar como fato — evita retrabalho de reconciliação depois. Mesmo padrão de
[[track-a-reconciliation]] e [[branch-audit-habit]]: spec anterior não bateu com o estado real do
código, exigiu auditoria.

**How to apply:** antes de reabrir discussão de arquitetura para qualquer fase A2-A5, ler §8+§9 do
plano primeiro; só revisar se o estado real do código divergir do que está descrito lá (checar via
`git log`/leitura de arquivo, não confiar cegamente no texto do plano).
