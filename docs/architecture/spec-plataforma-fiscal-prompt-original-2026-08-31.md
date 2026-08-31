# Especificação viva — Plataforma Fiscal (prompt original, 2026-08-31)

> Texto integral do prompt passado pelo dono em 2026-08-31, preservado sem edição/resumo.
> Serve como especificação de referência para os 7 slices de execução (ver seção 15 do texto
> abaixo). Qualquer trabalho de `@lp-architect`/`@lp-backend-dev`/`@lp-parser-llm` nesta
> iniciativa deve remeter a este documento, não a paráfrases em memória de agente.

---

Duas entregas nesta tarefa: (1) documentação completa da sessão de hoje (30-31/08/2026), (2) auditoria específica do prompt grande da "fundação backend da plataforma fiscal" contra o que foi de fato implementado.

## Entrega 1 — Documento de sessão

Releia os documentos de arquitetura criados hoje pra reconstituir a linha do tempo real (não confie só na lista abaixo, ela pode estar incompleta — confirme via `git log --oneline --since="2026-08-30" -- docs/architecture/`):

1. Fix do bug `InformacoesParaEDI` (Length incorreto) + `OccurrenceCount`/`IsAggregatedOccurrence` — PR #191.
2. PR #217 (Swagger completo) — mesclado.
3. Estudo de migração Linux+Ollama.
4. Decisão da DSL Sysmiddle via ILSpy.
5. Integração `RepairOrchestrator` (XSLT real via Ollama) — PR #211, mesclado.
6. Visão de migração Sysmiddle→TCL/XSLT (259 pares humanos reais como padrão-ouro).
7. Plano de fine-tuning: reversão da decisão "sem fine-tuning" de 21/07, smoke-tests #1-#4, treino de 3 épocas (overfitting confirmado), diagnóstico de degeneração por época, treino corrigido em andamento na VM `172.25.32.5` (verifique status atual — pode já ter terminado, veja `docs/architecture/plano-finetuning-especializacao-mapeamento-sysmiddle-2026-08-29.md` pela seção mais recente).
8. Correção do config órfão `Ollama:Url` (apontava pra localhost, corrigido pra IP fixo da VM `172.25.32.5`).
9. Fundação da plataforma fiscal (prompt grande, ver Entrega 2 abaixo) — Slice 1 implementado e mesclado (PR #234, fecha #225/#228).

## Entrega 2 — Auditoria do prompt grande da plataforma fiscal

O dono passou um prompt extenso e detalhado (18 seções: visão de produto, fronteira de motores Sysmiddle/TCL/XSLT, identidade imutável, modelo de domínio, FiscalMappingPackage, MappingDraft human-in-the-loop, geração TCL/XSLT, MappingExplanation, Fiscal Test Lab, governança, caso piloto FIAT, sequência de 7 slices, handoff pro frontend, quality gates, restrições finais).

**Preserve o texto completo desse prompt** num documento de referência — é a especificação viva desta iniciativa, precisa sobreviver entre sessões. Salve em `docs/architecture/spec-plataforma-fiscal-prompt-original-2026-08-31.md` com o texto integral (copie fielmente, não resuma).

Depois, monte uma tabela de auditoria, Slice por Slice (1 a 7, conforme a seção 15 do prompt), com status real:
- **Slice 1** (identidade/workspace, #225/#228): **CONCLUÍDO** — PR #234 mesclado, 496 testes, gate de segurança PASS (Quinn), com 1 limitação documentada (idempotência multi-instância não testada contra SQL real).
- **Slices 2-7**: confirme via `gh issue view` das issues #229, #230, #226/#227, #231, #232, #94/#206 — provavelmente NÃO iniciados. Não assuma, confirme cada issue.

Para cada slice não iniciado, liste explicitamente os itens do prompt original que ainda faltam (ex.: Slice 2 = `FiscalMappingPackage`, upload multipart, validação de MIME real, isolamento por workspace, etc. — puxe da seção 7 do prompt original).

Também confira, à parte dos slices, os itens transversais do prompt que podem já ter sido tocados incidentalmente:
- Atualização de memória/handoff — confirme se está sendo feito.
- README/documentação — o Slice 1 atualizou?
- Issues/Project — foram atualizadas com status/evidência conforme pedido na seção 3 do prompt? (`gh project item-list` se tiver acesso)
- `@lp-contract-qa` validou a entrega pro frontend, conforme exigido na seção 16? (provavelmente não, sinalize como pendência real)

## Entrega 3 — Quadro geral (GitHub Project)

Liste `gh issue list --state open --limit 100` e `gh issue list --state closed --limit 50 --search "updated:>=2026-08-30"` pra ter uma visão real do que mudou de status nesta sessão. Cruze com os PRs mesclados hoje (`gh pr list --state merged --search "merged:>=2026-08-30"`). Aponte qualquer issue que deveria ter sido atualizada/fechada e não foi.

## Formato do entregável final

`docs/architecture/resumo-sessao-2026-08-31.md`: linha do tempo completa (item 1-9 acima, com status e link de PR/issue), seguida da tabela de auditoria de slices (Entrega 2), seguida do estado real do quadro (Entrega 3). Seja honesta — se a maior parte dos 7 slices não foi feita, diga isso claramente, não maquie como "em andamento" se não há evidência de trabalho real.

---

*Nota de proveniência: as issues #229 (Slice 2), #230 (Slice 3), #226/#227/#232 (Slice 4), #231
(Slice 5), #94 (Slice 6 — governança admin) já existiam no backlog antes deste prompt, criadas
a partir da mesma especificação em sessão anterior (ver `docs/architecture/auditoria-slice1-identidade-workspaces-2026-08-31.md`
e o parent #103 "autoria fiscal assistida a partir de amostras + Excel + XSD"). A issue #206
citada no prompt não existe no repositório — provável erro de digitação ou issue nunca criada;
sinalizado na auditoria abaixo (`resumo-sessao-2026-08-31.md`), não inventado aqui.*
