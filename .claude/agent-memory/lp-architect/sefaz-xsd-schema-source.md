---
name: sefaz-xsd-schema-source
description: nfephp-org/sped-nfe no GitHub já mirrora pacotes históricos de XSD da NF-e (confirmado PL_009_V4) — resolve o bloqueio do pacote XSD antigo sem precisar de scraping da SEFAZ
metadata:
  type: reference
---

Pra P-3 do `nt-pipeline-design.md` (diff real `PL_009×PL_010b`, bloqueado por falta do pacote XSD antigo),
o dono do projeto propôs (2026-07-21) fazer webscraping do portal da SEFAZ
(`nfe.fazenda.gov.br/portal/listaConteudo.aspx?tipoConteudo=...`) pra sempre pegar os pacotes novos.

**Achado (WebSearch, 2026-07-21): não precisa de scraping pra resolver P-3 — já existe.** O repositório
open-source `nfephp-org/sped-nfe` no GitHub já mirrora arquivos do pacote **PL_009_V4**
(`leiauteNFe_v4.00.xsd`, `nfe_v4.00.xsd`, `enviNFe_v4.00.xsd`, `tiposBasico_v4.00.xsd` e outros, em
`schemes/PL_009_V4/`). É um projeto estabelecido na comunidade de integração fiscal brasileira — mais
confiável de reusar do que montar um scraper do zero contra o `.aspx` da SEFAZ.

**WebFetch direto na URL da SEFAZ falhou** ("unable to get local issuer certificate") — não confirmei se é
problema de cadeia de certificado do lado do site (padrão histórico conhecido em portais `.gov.br`
brasileiros, que às vezes usam certificados ICP-Brasil fora do bundle de CA global padrão) ou só limitação
do ambiente desta ferramenta. Não decidi qual — se um scraper direto contra a SEFAZ for adiante no futuro
(pro caso contínuo/futuro, não pro P-3 específico), **testar isso cedo**, não assumir que vai funcionar de
primeira.

**Why importa:** resolve o bloqueio de P-3 hoje, sem precisar construir scraper nenhum. Separa a pergunta
"como pegar o pacote antigo específico" (resolvida) de "como acompanhar pacotes futuros automaticamente"
(ainda em aberto — ver `docs/architecture/nt-pipeline-design.md`).

**How to apply:** antes de propor scraping direto da SEFAZ pra qualquer necessidade de pacote XSD
(histórico ou atual), checar primeiro se `nfephp-org/sped-nfe` (ou mirror equivalente) já tem o que precisa
— mais barato e mais robusto que parsear HTML de portal de governo. Reservar scraping direto só pro caso de
acompanhamento contínuo de NT futura, e mesmo aí considerar monitorar esse mirror comunitário como
alternativa mais barata antes de construir scraper próprio.
