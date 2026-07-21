---
name: cfop-catalog-6-1
description: Base CFOP×operação (item 6.1) implementada 2026-07-21 — fonte, 3 bugs de dado corrigidos, sinal "ALL-CAPS=cabeçalho de grupo", finNFe=4 é o campo estruturado real por trás de "tipo de operação"; nível de verificação honesto (amostral, não auditoria linha-a-linha)
metadata:
  type: project
---

`CfopOperationCatalogService` (`Services/Validation/`) + `Data/Fiscal/cfop-tabela.csv`
(embedded resource, 608 linhas). Ver [[a6-provenance-publisher]] (mesma sessão,
mesmo dispatch).

**Fonte:** mirror público GitHub `jansenfelipe/cfop` (branch 1.0, `cfop.csv`) —
não a tabela oficial direto do CONFAZ/RFB (não achei versão JSON/CSV completa
lá; `sped.rfb.gov.br/arquivo/show/85` só tem um XLS de subconjunto "operações
geradoras de créditos", não a tabela inteira). 30+ códigos cross-checados contra
conhecimento de domínio (5102, 1102, 6108, 5405, 5910/6910, 1201, 3949...) —
bateram exatamente. **Sem auditoria linha-a-linha contra o texto oficial do
Ajuste SINIEF 07/01** — nível de verificação real, não fingir 100%.

**3 bugs de dado encontrados e corrigidos na fonte bruta** (documentados no
docblock do serviço, não repito aqui) — o mais importante para não esquecer:
classificação por palavra-chave precisa priorizar a ABERTURA da descrição, não
"contains" em qualquer posição — "Compra para industrialização, em VENDA à
ordem..." (2120) caía errado em categoria "Venda" por causa da cláusula
composta "venda à ordem" (termo técnico de triangulação) no meio do texto.

**Sinal estrutural útil (não documentado em lugar nenhum, achado empírico):**
na tabela oficial, TODA linha cujo código termina em "00" OU "50" é cabeçalho
de GRUPO/subgrupo (descrição em ALL-CAPS), nunca um código transacionável —
confirmado sem exceção nas 608 linhas. Uso isso pra `IsGrupo` em vez de mais
uma lista hardcoded.

**Achado de domínio (não estava explícito no dispatch, inferido):** o campo
estruturado por trás de "tipo de operação sempre declarado de forma
confiável" (ia-fiscal-diagnosis-vision.md §4.2) é quase certamente
`ide/finNFe` da NF-e (1=Normal, 2=Complementar, 3=Ajuste, **4=Devolução/
Retorno** — enum fechado do XSD, não texto livre como `natOp`). O exemplo da
visão ("CFOP de venda está errado porque a nota é devolução") só faz sentido
completo cruzando Categoria do CFOP × `finNFe`, não CFOP × `natOp` (que é
free-text). `CheckConsistenciaComFinalidade` já implementa essa direção
(confiança alta) + a direção inversa (CFOP Devolução/Retorno com finNFe≠4,
confiança mais branda — não confirmei se toda devolução real sempre usa
finNFe=4 em todo regime/estado). Isso é o ponto de partida do item 6.2
(Lia+Dex) — falta wiring em controller/endpoint.

**How to apply:** ao expandir a categorização ou trocar a fonte de dado por
algo mais oficial, reusar o pipeline de limpeza já validado (split de
concatenação, priorizar abertura da descrição) — não redescobrir os mesmos 3
bugs. Ao trabalhar em 6.2, `CfopOperationCatalogService` não expõe enumeração
pública ainda (só lookup por código) — se precisar filtrar por categoria em
volume, promover a um método `Entries` público em vez de reimplementar a
varredura ad-hoc que fiz em `SyntheticFiscalScenarioGenerator.
EscolherCfopDaCategoria`.
