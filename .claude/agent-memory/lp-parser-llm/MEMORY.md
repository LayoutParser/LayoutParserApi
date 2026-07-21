# Memória — lp-parser-llm (Lia)

- [PoC-3 R4 estado](poc3-r4-estado.md) — FALTA=0/TEXTO=0 atingido 2026-07-12; SOBRA=8 + 7 XSD = Etapa B (máscara do mapeador); gotchas de diagnóstico.
- [RAG few-shot B4](rag-fewshot-b4.md) — corpus SEM pares DSL→XSLT (exportContext criptografado); 191 XSLs reais = estilo; gotcha: interpretador parcial em regra difícil.
- [Mapeadores multi-cliente](multi-client-mappers.md) — package único 938f9978 tem os 170 mapeadores de TODOS clientes; GUIDs SEND_ENV + variantes fiscais (ICMS10/40, IPITrib, PISNT/Outr, IDOC) p/ G3/G4.
- [Catálogo GUID→XPath (A3)](guid-xpath-catalog-a3.md) — layout-nfe.xml é o LayoutVO real (TargetLayoutGuid bate); gotchas de encoding+whitespace; 235/237 LinkMappings resolvidos.
- [A6 ProvenancePublisher](a6-provenance-publisher.md) — cobre LinkMapping+DslRule; CandidateBuilder tem o MESMO anti-padrão de XComment, não só LinkMappingTranspiler; ainda NÃO validado contra dado real.
- [Base CFOP×operação (6.1)](cfop-catalog-6-1.md) — fonte, 3 bugs de dado corrigidos, sinal "ALL-CAPS=grupo", finNFe=4 é o campo real por trás de "tipo de operação".
- [NT-pipeline P-1/P-2 rodados](nt-pipeline-p1-p2-real-run.md) — nfephp-org tem PL_009 E PL_010 (NT2025.002 real!); diff bônus achou CHAVEACESSO/CNPJ alfanumérico; cert nfe.fazenda.gov.br confirmado quebrado; PdfPig sem quebra de linha confiável.
- [Mirrors públicos de dado fiscal](public-fiscal-data-mirrors.md) — nfephp-org/sped-nfe (XSD) e jansenfelipe/cfop (CFOP) — checar antes de propor scraping do portal oficial.
- [Gotchas de ambiente desta sessão](session-environment-gotchas.md) — máquina começou sem nada em .claude/tmp/Documentos; sem pip/apt-root; não presumir dado de memória antiga ainda em disco.
