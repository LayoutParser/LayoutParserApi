# Slice 6 — Gate transversal contra mutação Sysmiddle (issue #232)

## 1. Inventário de endpoints Sysmiddle-adjacentes

### A) Pathway antigo (execução, pré-existente)
`TransformationExecutionController.ExecuteSysmiddleCandidatesAsync` (via `LowCodeAutoTransformationService`)
e `ParseController` (`candidateId=sysmiddle-{guid}`) só **executam** mapeadores `.exe`/DLL já
publicados no catálogo (`tbMapper`) — nunca escrevem/geram artefato Sysmiddle novo. `LayoutParserLib`
(cripto) e `LayoutParserDecrypt.exe` são consumidos como caixa-preta; a API não tem, nunca teve,
capacidade de *autoria* nesse formato (não existe serializer/writer Sysmiddle no código). Não é uma
garantia testada — é ausência estrutural de capacidade. Mesmo diagnóstico via
`LayoutDatabaseController` (rota de debug que descriptografa Base64, não escreve) e
`MapperDatabaseController` (lista/lê `tbMapper`, não grava mapeador). **Risco real: zero, mas não
testado formalmente.**

### B) Endpoints fiscais novos (Slices 1-5) com parâmetro `engine`
| Controller | `[ServiceFilter(MappingEngineGuardFilter)]`? | Nota |
|---|---|---|
| `MappingDraftsController` (Slice 3) | ✅ Sim, nível de controller | Recusa `engine=sysmiddle` em query e body |
| `MappingExplanationController` (Slice 4) | ❌ Não | Deliberado — rota de leitura, `sysmiddle` é `Engine` válido pra **explicar** (spec §4 permite `explain`) |
| `FiscalMappingPackagesController` (Slice 2) | N/A | Sem campo `engine` no contrato — não é vetor |
| `TransformationExecutionController` (Slice 5, `compile`/`test-runs`) | A verificar no código do Slice 5 (não confirmado nesta sessão) | Transpiladores geram XSLT/TCL a partir de `MappingDraftRule` — nunca "sysmiddle" como alvo de compilação; checar se aceita `engine` como parâmetro de saída |

## 2. Decisão: `MappingExplanationController` NÃO recebe o filtro

Mantém-se a exclusão. `MappingEngineGuardFilter` bloqueia por *ação sobre o valor*, não por
presença do literal "sysmiddle" — ele impede que `engine=sysmiddle` autorize escrita. No
`MappingExplanationController` não há escrita: é sempre leitura/explicação, e o próprio contrato
(`_sysmiddleAdapter.ExplainAsync`) é o caso permitido pela spec §4 ("Sysmiddle pode explain, nunca
author"). Aplicar o filtro aqui seria redundante e semanticamente errado (bloquearia o único uso
legítimo do valor "sysmiddle" no sistema). Defesa em profundidade não significa aplicar todo filtro
a todo endpoint — significa aplicar o filtro certo à ação certa. Decisão: **manter como está**,
documentar a razão inline no controller (já existe comentário, reforçar citando este slice).

## 3. Vetores de adulteração a cobrir em teste (além do já coberto no Slice 3)

1. `engine` como array/objeto em vez de string — `MappingEngineGuardFilter.ResolveEnginesAsync` só
   trata `ValueKind == String`; um payload `{"engine":["sysmiddle"]}` ou `{"engine":{"value":"sysmiddle"}}`
   passa sem detecção. **Lacuna real.**
2. Homoglyphs/Unicode que normalizam para "sysmiddle" (ex.: caracteres Cyrillic visualmente
   idênticos) — comparação é `OrdinalIgnoreCase` sobre string literal ASCII; não há normalização
   Unicode (NFKC) antes da comparação. Se o backend downstream faz qualquer `ToLowerInvariant`
   culture-aware ou trim de diacríticos antes de rotear pelo valor, um valor disfarçado poderia
   escapar do filtro e ainda ser interpretado como "sysmiddle" mais adiante. **Verificar se algum
   consumidor downstream do campo `engine` faz esse tipo de normalização — se não fizer, o vetor é
   teórico, não explorável (o valor disfarçado nunca vira "sysmiddle" de fato em lugar nenhum).**
3. `engine` ausente — o filtro só bloqueia valor explícito "sysmiddle"; ausência passa. Cada
   controller downstream precisa ter fail-closed próprio (allowlist explícita, como o comentário do
   filtro já adverte). Confirmar que `MappingDraftsController.CreateDraft` de fato rejeita `engine`
   ausente/vazio, não só "sysmiddle" — se hoje aceita ausência como default silencioso para algum
   motor, é lacuna a testar (não necessariamente a corrigir, se o default for `tcl`/`xslt` explícito
   e documentado).
4. Query vs. body divergente — já coberto no Slice 3; replicar como caso de regressão na suíte
   nova, não reimplementar.
5. Content-Type não-JSON (`multipart/form-data`, `application/x-www-form-urlencoded`) carregando
   `engine=sysmiddle` — o filtro só lê `request.HasJsonContentType()`; um upload multipart com campo
   de formulário `engine=sysmiddle` não é inspecionado pelo filtro. Verificar se algum endpoint dos
   Slices 1-5 aceita `engine` fora de JSON puro (ex.: upload de arquivo + campos de form). Se
   nenhum aceitar, vetor é teórico; se algum aceitar, é lacuna real.

## 4. RBAC/role — não é vetor adicional hoje

Confirmado nesta sessão: nenhum controller tem `[Authorize]` ou enforcement de papel — `ICurrentUser`/
`WorkspaceRole` (Slice 1) populam identidade e papel, mas nada no pipeline HTTP hoje decide "esse
papel pode isso". Não existe rota administrativa privilegiada que bypasse `MappingEngineGuardFilter`
porque não existe *nenhuma* rota com controle de acesso por papel ainda — o gate único e universal
(o filtro) é a única barreira que existe, para todo mundo. Isso não é uma lacuna do Slice 6: é fora
de escopo (autorização por papel é decisão de produto em aberto, `rollout-p2-autenticacao.md`).
Quando RBAC por papel for implementado, revisitar se algum papel deveria ter rota de bypass
intencional (não deveria, pela spec §4) — registrar como item futuro, não bloqueante deste slice.

## 5. Plano de teste (suíte nova, `tests/LayoutParserApi.Tests/Security/SysmiddleGateTests.cs`)

Majoritariamente teste, não código de produção novo — exceto o item 5.1 abaixo, que é lacuna real.

- **5.1 [CÓDIGO NOVO NECESSÁRIO]** Estender `MappingEngineGuardFilter.ResolveEnginesAsync` para
  tratar `engine` como array (`ValueKind == Array`, checar cada elemento string) — hoje silenciosamente
  ignorado. Sem isso, `{"engine":["sysmiddle"]}` passa despercebido. Pequeno, mesmo arquivo.
- 5.2 Suíte de integração cobrindo TODOS os endpoints da tabela §1-B com: `engine=sysmiddle` em
  query, em body, em ambos com valores diferentes, `engine` ausente, `engine` como array (após 5.1),
  content-type não-JSON quando aplicável.
- 5.3 Teste de regressão explícito no pathway antigo (§1-A): assert de que
  `ExecuteSysmiddleCandidatesAsync` e `LowCodeAutoTransformationService` não expõem nenhum
  método de escrita/publicação — teste estrutural (reflection sobre a superfície pública) mais do
  que teste de comportamento, já que a ausência de capacidade é a garantia.
- 5.4 Teste negativo específico do `MappingExplanationController`: `engine=sysmiddle` em `explain`
  retorna 200 (não bloqueado) — prova que a exclusão é intencional e continua válida após mudanças
  futuras (guarda contra alguém "corrigir" isso sem saber que é deliberado).
- 5.5 Se o Slice 5 (`compile`/`test-runs`) aceitar `engine`, replicar a matriz de §1-B para lá —
  confirmar isso lendo `TransformationExecutionController` antes de escrever a suíte final.

## Veredito

A garantia central (Sysmiddle não gera/edita nada) é sólida por **ausência estrutural de
capacidade** no pathway antigo, e o pathway novo (Slice 3) já tem defesa ativa testada. As lacunas
reais são pontuais e pequenas: (1) filtro não trata `engine` como array/objeto, (2) cobertura do
filtro não confirmada no Slice 5, (3) nenhuma suíte hoje prova a ausência de capacidade de escrita
Sysmiddle de forma automatizada — é confiança implícita, não gate testado.
