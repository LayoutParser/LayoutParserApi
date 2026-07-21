---
name: nt-pipeline-p1-p2-real-run
description: P-1/P-2 do nt-pipeline-design.md rodados de verdade 2026-07-21 — nfephp-org/sped-nfe tem PL_009_V4 E PL_010_V1.30 (a NT2025.002 real!), diff real rodado como bônus (290 deltas, achado grande de CHAVEACESSO/CNPJ alfanumérico); cert do nfe.fazenda.gov.br confirmado quebrado de verdade (não só WebFetch); PdfPig funciona mas Page.Text não tem quebra de linha confiável
metadata:
  type: project
---

P-1 (`XsdDiff` CLI, já existia em `ai/XslSynth/NtPipeline/XsdDiffer.cs` desde a
Trilha B, nunca tinha sido rodado de verdade) e P-2 (smoke de extração de PDF,
código novo `PdfSmokeExtractor.cs`) rodados 2026-07-21 com autorização do dono
do projeto. Ver `docs/architecture/nt-pipeline-design.md` §5.

**Achado grande — `nfephp-org/sped-nfe` tem os DOIS pacotes, não só o antigo:**
o dispatch (item 7.2) só pedia pra puxar `PL_009_V4` (pacote antigo) desse
mirror. Descobri que o MESMO mirror também tem `PL_010_V1.30` — que É a
NT2025.002 v1.30 (Reforma Tributária IBS/CBS/IS), confirmado pelos arquivos
extras de evento (e112xxx, e211xxx, e212xxx, e412xxx) que só aparecem nessa
pasta. Ou seja, **P-3 (diff real PL_009×PL_010, antes tido como bloqueado por
dado externo) não está mais bloqueado** — rodei como bônus (não era meu
mandato formal, só P-1/P-2 eram): **290 deltas** (277 Added, 13 FacetChanged,
0 Removed) entre os dois pacotes reais.

**Achados fiscais relevantes dentro desse diff (não é só sobre a Reforma
Tributária):**
- `TChNFe`/`TNFe/infNFe/@Id`: chave de acesso muda de `[0-9]{44}` (puramente
  numérica) para `[0-9]{6}[0-9A-Z]{12}[0-9]{26}` (alfanumérica) — a decomposição
  de 44 dígitos que documentei em [[poc3-r4-estado]] (cUF/AAMM/CNPJ/mod/serie/
  nNF/tpEmis/cNF/cDV) vai precisar de revisão quando esse formato entrar em
  vigor. Relevante direto pro item 3.1 do Dex (`FieldContentValidationService.
  ValidateChaveAcessoNFe`, hoje assume 44 dígitos numéricos).
- `TCnpj`/`TCnpjVar`: mesma mudança, `[0-9]{14}` → `[0-9A-Z]{12}[0-9]{2}`
  (CNPJ alfanumérico, iniciativa já pública da Receita Federal).
- 277 campos novos, quase todos sob `ide/`, `det/prod/`, `det/imposto/ICMS/
  ICMS90/` — grupos IBS/CBS (cMunFGIBS, gCompraGov, tpCredPresIBSZFM, etc.).
Deltas salvos em `.claude/tmp/nt-pipeline/delta-pl009-pl010.json` (gitignored,
só nesta máquina).

**Cert do `nfe.fazenda.gov.br` — confirmado problema real do servidor, não da
ferramenta:** a memória de `@lp-architect` (`sefaz-xsd-schema-source.md`)
tinha essa dúvida em aberto. Testei com `curl` puro (não só WebFetch) contra
`nfe.fazenda.gov.br/portal/exibirArquivo.aspx` — MESMO erro ("unable to get
local issuer certificate"). É a cadeia de certificado do lado do site
(provável ICP-Brasil fora do bundle CA global padrão), não limitação de
ferramenta. `sped.rfb.gov.br` (subdomínio diferente da RFB) respondeu 200 sem
problema — o problema é específico do host/domínio `nfe.fazenda.gov.br`.

**P-2 (PdfPig, não pypdf):** sem pip/apt com root nesta máquina (ver
[[session-environment-gotchas]]) — usei `PdfPig` (pacote NuGet "PdfPig", NÃO
"UglyToad.PdfPig" que tem versionamento "-custom-N" estranho e parece legado/
paralelo — checar sempre o ID canônico via `api.nuget.org` antes de assumir o
nome do pacote). Rodado contra uma NT real da SEFAZ (mirror `nfephp-org`,
`NT2023.002 - Emitente CPF - NFCe.pdf`, não a NT2025.002 que não achei em PDF
público): extraiu texto legível, achou "Grupo D" e 5 códigos de regra
(B26-30 etc.) — viabilidade confirmada. **Limitação real:** `Page.Text` do
PdfPig não preserva quebra de linha confiável (heurística de título de seção
por numeração decimal deu 0 matches) — o S2 de verdade (NtSemantics, ainda não
implementado) vai precisar de reconstrução de linha por posição
(`Page.GetWords()`/coordenadas de letra, ou o pacote companion
`PdfPig.DocumentLayoutAnalysis`), não `Page.Text` puro.

**How to apply:** se retomar P-3 formalmente ou o S2 de verdade, os artefatos
desta rodada (delta JSON, achados de CHAVEACESSO/CNPJ) são ponto de partida —
não precisa rerodar do zero. Ao buscar qualquer pacote XSD da NF-e (antigo OU
atual), checar `nfephp-org/sped-nfe` PRIMEIRO via GitHub API
(`/repos/nfephp-org/sped-nfe/contents/schemes`) antes de tentar scraping ou
assumir que só a SEFAZ tem.
