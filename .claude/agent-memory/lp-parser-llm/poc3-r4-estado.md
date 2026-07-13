---
name: poc3-r4-estado
description: PoC-3 R4 concluído (2026-07-12) — set-diff FALTA=0/TEXTO=0; restam 8 SOBRAs + 7 erros XSD = Etapa B (máscara do mapeador)
metadata:
  type: project
---

R4 da PoC-3 concluído em 2026-07-12: gate set-diff por path com **FALTA=0 e TEXTO=0** no par real.
Restam **SOBRA=8** (retTrib×7 + cobr/fat/vLiq) e **7 erros XSD** (todos `TDec_1302Opc` proíbe '0.00' nos
mesmos retTrib) — ambos são a **Etapa B**: usar os 237 links + 98 regras do MapperVO real como máscara de emissão.

**Why:** o gerador determinístico spec-Excel→XSL não tem como saber que o mapeador FiatMQ omite retTrib zerado
mas emite fat/vOrig,vDesc zerados — só o mapeador real carrega essa decisão.

**How to apply:** próxima missão nesta trilha = Etapa B (máscara do mapeador) ou `<dadosAdic>`. Detalhes
completos dos fixes em `docs/architecture/poc-excel-generator.md` §7.8 (tabela T1/T2a–g). NÃO refazer as
12 FALTA/TEXTO — já zeradas.

Gotchas de domínio não óbvios (custaram diagnóstico):
- `nfe-leiaute-catalog.csv` em `.claude/tmp/export/` só é regenerado pelo modo `--catalog` — no `--generate`
  ele fica STALE. Diagnóstico do generate = `generated-notes.txt` + `--catalog --debug-ref <n>`.
- Vocabulário do XSD ≠ spec: doc do vICMSUFDest é "ICMS de **partilha** p/ UF do **destinatário**" (spec diz
  "Interestadual"/"destino"); vFCPST×vFCPSTRet diferem só por "anteriormente" (margem semântica 0.03).
- Chave de acesso NF-e (44 díg., layout fixo verificado no gabarito): cUF(1-2) AAMM(3-6) CNPJ(7-20) mod(21-22)
  serie(23-25) nNF(26-34) tpEmis(35) cNF(36-43) cDV(44). procEmi='0' é constante do mapeador.
- Diff posicional infla por cascata (1 elemento fora → todos os irmãos viram [NOME]); o gate honesto é o
  set-diff por path (implementado no A3 do Program.cs do XslSynth).
