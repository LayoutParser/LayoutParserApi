---
name: issue138-sectionmappings-fase0-qa-gate
description: QA gate do contrato sectionMappings/xmlNamespaces (Fase 0, issue #138/#126) em execute-candidates
metadata:
  type: project
---

Commit `fa9afc0` (branch `feat/section-mappings-fase0-138`, worktree isolado) — veredito PASS
com 2 gaps reais fechados nesta sessão (commit `1718c38` no mesmo branch/worktree).

**O que existia:** `SysmiddleSectionMappingResolver.cs` resolve XPath/namespaces 100% estrutural
via `RealMapperParser` (nunca `LinkMappingItem`, nunca comparação de valor). `null` = pathway não
suporta (tcl-xsl); `[]` = suporta mas nada resolvível; lista preenchida = mappings. Doc XML explícita
já deixava claro que não resolve granularidade de campo e não desbloqueia PBI #128.

**Gaps encontrados e fechados:**
1. Os testes do resolver confirmavam só o FORMATO da string do XPath, nunca a resolução real —
   adicionei `XPathSelectElement`/`XPathSelectElements` (System.Xml.XPath) contra o XML de saída
   sintético, incluindo o caso de grupo repetido (2 nós `<det>/<cProd>`).
2. Não havia nenhum teste com um candidato tcl-xsl **bem-sucedido** confirmando
   `SectionMappings == null` (só havia cenários de falha/not_applicable) — os testes existentes
   nunca exercitavam a distinção null vs [] no lado tcl-xsl. Adicionei
   `TclXsl_bem_sucedido_reporta_SectionMappings_null_nao_lista_vazia` reaproveitando o fixture de
   `TransformationPipelineServiceMapFileTests`.

**Confirmado sem gap:** contrato aditivo (baseline idêntica: 4 falhas pré-existentes em
`LowCodeRunnerArgsTests`, path Windows-on-Linux, não relacionadas), sem log de
`MapperDecryptedContent` em nenhum ponto (grep confirmado), Swagger/OpenAPI segue pendente
(fora do escopo QA, sinalizar a `@lp-doc`).

**Baseline dotnet test neste ambiente:** 400 passando / 4 falhando (LowCodeRunnerArgsTests, path
Windows hardcoded nos testes, ambiente Linux) — não é regressão desta feature, ver
[[unified-logging-parse-bug-and-log-dir-incident]] para o mesmo tipo de ressalva de ambiente.
