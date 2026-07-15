---
name: multi-client-mappers
description: Enumeração multi-cliente via runner LIST/EXEC (2026-07-15) — package único 938f9978 tem TODOS os 170 mapeadores; GUIDs SEND_ENV e variantes fiscais p/ G3/G4
metadata:
  type: project
---

Enumeração multi-cliente do LowCodeRunner concluída em 2026-07-15 (alicerce de G3/G4 do
[[poc3-r4-estado]] e de `multi-client-layout-generalization.md`).

**Achado central (só descobrível rodando o runner, não está no repo):** NÃO existem packages por
cliente. O package único da instância FiatMQ — `938f9978-836f-48c1-9c0f-c2898caf4b20`
(`ProjectInUseIdentifier` no global.config) — contém os **170 mapeadores de TODOS os clientes**
(FIAT, CNHI, IVECCO, MARELLI, COMAU, PSCA, ECOMEX, SAP…). Generalizar = escolher o mapeador certo,
não trocar de package. Catálogo GUID→nome salvo em `.claude/tmp/gabaritos/mappers-catalog-fiatmq.tsv`
(regenerável pelo modo LIST; `.claude/tmp` é gitignored).

**Mapeadores SEND_ENV (TXT→enviNFe) por cliente:** FIAT=`MAP_f31a6758` (baseline),
CNHI=`MAP_f1a6453f`, IVECCO=`MAP_166b4df6`, MARELLI(SAP)=`MAP_204a020e` /
MARELLI(MQSeries)=`MAP_1cfab556`. COMAU/PSCA têm mapeador mas SEM input de exemplo no repo.

**Como rodar (Trilha A):** o `.exe` net481/x86 executa direto do WSL via interop, DE DENTRO da Bin da
instância (`.claude/tmp/servidor/fiatmq/Instance_FiatMQ/AppConnector.DIR/Bin`). Args:
`<globalFolder=Bin> <package=938f9978…> <MAP_guid|LIST> <input> <output>`. Bootstrap ~0.5–1s.
UMA instância por vez (estado compartilhado do Instance_FiatMQ). Paths dos args = Windows (`C:\…`).

**Why:** o baseline FIAT é monótono (quase todo CST=ICMS00/IPINT/PISAliq). G4 (generalizar o
`MapperEmissionGuide`) precisa de diversidade fiscal real. Um EXEC por cliente já rendeu variantes
novas (manifesto em `.claude/tmp/gabaritos/multi-client-manifest.tsv`):
- **ICMS10** (IVECCO, do IDOC .env) — tributado COM Substituição Tributária (traz vBCST/vICMSST que
  o FIAT nunca exercita); **ICMS40** (MARELLI) — isento.
- **IPITrib** (CNHI, IVECCO) — IPI tributado; FIAT só tem IPINT.
- **PISNT/COFINSNT** (CNHI) e **PISOutr/COFINSOutr** (MARELLI); FIAT só PIS/COFINSAliq.
- **Família de input não-600:** MARELLI (TXT_SAP) e IVECCO (.env) são **IDOC/EDI** (segmentos
  EDI_DC/ZRSDM_*, linhas variáveis), não posicional 600. MARELLI ainda emite namespace NFe
  explícito, verProc "4.00" (vs "400") e indSinc=1.

**How to apply:** FIAT/CNHI/IVECCO (MQSeries) compartilham o layout posicional 600 (linha física =
múltiplo exato de 600, registro `HEADER`) → mesmo gate estrutural §3.1. MARELLI e o .env do IVECCO
são IDOC → NÃO aplicar o gate de 600 neles (§3.1 só vale p/ TextPositional). Ao generalizar o
`MapperEmissionGuide`, use estes gabaritos como casos reais além do FIAT.
