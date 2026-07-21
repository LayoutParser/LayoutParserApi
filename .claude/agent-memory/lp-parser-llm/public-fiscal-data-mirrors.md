---
name: public-fiscal-data-mirrors
description: Mirrors públicos no GitHub usados como fonte de dado fiscal (XSD NF-e, tabela CFOP) quando o portal oficial falha/está incompleto — checar aqui antes de tentar scraping
metadata:
  type: reference
---

Dois mirrors GitHub usados 2026-07-21 como fonte de dado fiscal público
(detalhe/racional completo em [[nt-pipeline-p1-p2-real-run]] e
[[cfop-catalog-6-1]]):

- **`nfephp-org/sped-nfe`** (`schemes/<pacote>/`) — pacotes XSD da NF-e por
  versão. Confirmado ter `PL_009_V4` (antigo) E `PL_010_V1.30` (a NT2025.002
  real, IBS/CBS/IS) — listar via
  `api.github.com/repos/nfephp-org/sped-nfe/contents/schemes`. Também tem um
  PDF de NT real solto na raiz do repo (`NT2023.002...pdf`), útil pra smoke
  test de extração sem depender do portal oficial.
- **`jansenfelipe/cfop`** (branch `1.0`, arquivo `cfop.csv`) — tabela CFOP
  completa (608 linhas incl. cabeçalhos de grupo). Tinha bugs de dado na fonte
  bruta (ver [[cfop-catalog-6-1]]) — não usar sem o pipeline de limpeza já
  validado.

**Why:** `nfe.fazenda.gov.br` tem problema de cadeia de certificado
confirmado (curl E WebFetch falham igual, "unable to get local issuer
certificate") — não adianta insistir nele pra baixar pacote/PDF. `sped.rfb.
gov.br` funciona, mas só tem um XLS de subconjunto CFOP ("operações geradoras
de créditos"), não a tabela inteira.

**How to apply:** antes de propor scraping do portal oficial da SEFAZ/RFB pra
qualquer dado fiscal público (XSD, CFOP, NT), checar esses dois mirrors
primeiro via GitHub API — mais barato, mais confiável, sem problema de cert.
