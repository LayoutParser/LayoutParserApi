---
name: ia-fiscal-diagnosis-vision
description: Visão expandida (2026-07-21) além da síntese de XSLT — diagnóstico semântico fiscal (CFOP), rastreamento input→output, filtro de assinatura, substituição do AppConnector. Registrada em docs/architecture/ia-fiscal-diagnosis-vision.md
metadata:
  type: project
---

Em 2026-07-21 o dono do projeto trouxe uma visão bem mais ampla do que "diagnóstico XSD + geração XSLT":
(1) pré-análise semântica fiscal (CFOP × tipo de operação, não só validação de schema), (2) rastreamento
de defeito input→output via o sidecar de proveniência (exemplo: CHAVEACESSO com 43 em vez de 44 dígitos),
(3) desenvolvimento autônomo de TCL/XSLT pra **substituir o AppConnector em produção** (app "FiatMQ"),
(4) auto-atualização por NT nova da SEFAZ, (5) filtro pra erro de assinatura digital (sempre presente,
sempre esperado, não é defeito), (6) reafirma o pedido de fork open-source treinado especificamente
(agora também pro diagnóstico fiscal, não só geração sintética — ver [[gemini-openai-decommission-decision]]
pro caso de geração sintética).

Registrei a visão completa, fases (near/mid/long-term) e recomendações em
`docs/architecture/ia-fiscal-diagnosis-vision.md` — este memo é só o índice + os achados mais
surpreendentes/caros de re-descobrir.

**Achados que mudam o cálculo, não óbvios:**

1. **"AppConnector" não é repositório externo desconhecido.** São as DLLs Sysmiddle já presentes em
   `tools/LowCodeRunner/Functions/` (`appConnector.Client.Data.dll.config`,
   `appConnector.Mapper.Functions.dll.config`); a instância real é `Instance_FiatMQ/AppConnector.DIR/Bin`
   — exatamente onde a Trilha A já roda o `.exe` do runner (memória da Lia `multi-client-mappers.md`).
   `tools/LowCodeRunner/SysmiddleMapperExecutor.cs` já **porta a lógica** do `appConnector.Client.MappersHelper`,
   incluindo pós-processamentos NF-e específicos (escape de `<`/`>` em `infCpl`/`infAdFisco`/`infAdProd`) —
   isso é um checklist concreto que qualquer XSLT substituto precisa replicar.
2. **O sidecar de proveniência (A6 da Trilha A) estava com escopo incompleto.** Só cobria
   `DslRuleTranslator` (98 campos via regra DSL), não `LinkMappingTranspiler` (237 campos via LinkMapping
   direto, a maioria). Pior: o `LinkMappingTranspiler` hoje embute um `XComment` de debug como filho
   literal do elemento de saída no XSLT — isso **sobrevive à transformação**, é o anti-padrão que a decisão
   original de proveniência queria evitar. Corrigido em `multi-session-execution-plan.md` §10 — ver
   [[xslsynth-trilha-a-overlap]].
3. **O exemplo da CHAVEACESSO não precisa de IA nenhuma.** A memória da Lia `poc3-r4-estado.md` já
   documenta a decomposição exata (44 dígitos: cUF/AAMM/CNPJ/mod/serie/nNF/tpEmis/cNF/cDV, posições
   verificadas contra gabarito real). Validar tamanho/checksum do campo de input isolado é 100%
   determinístico com dado que o projeto já tem — o LLM só entraria pra redigir a explicação em linguagem
   natural, não pra "detectar" o defeito.
4. **Fine-tuning reconsiderado pra CFOP/regra fiscal semântica — ainda não, mas por razão mais forte.**
   CFOP×tipo-de-operação é tabela pública, finita, enumerável — é problema de lookup/cruzamento de tabela,
   não de reconhecimento de padrão. Fine-tuning seria pedir pra rede neural decorar uma tabela de consulta
   que deveria ser indexada e consultada diretamente (RAG sobre a tabela real, não sobre documentos).
   Recomendo checar com Lia se alguma das 98 regras DSL já mapeadas na Trilha A já encoda parte dessa lógica
   antes de construir do zero.
5. **Risco compartilhado real:** memória da Lia `rag-fewshot-b4.md` documenta bug conhecido do
   `DslBlockInterpreter` em regras com `else`/`&&`/aninhamento (emissão incondicional errada "com cara de
   verificada") — regra fiscal tipo CFOP-vs-tipo-operação é exatamente esse padrão de lógica condicional
   aninhada. Tratar como pré-requisito/risco compartilhado com a validação semântica fiscal nova, não
   ignorar.
6. **`docs/architecture/nt-pipeline-design.md` já desenha boa parte do item de auto-atualização por NT**
   (pipeline S1-S5, separando diff determinístico de semântica via LLM com citação obrigatória) —
   inclusive a assimetria exata que o dono do projeto descreveu (lado output rastreável vs lado input
   sempre reativo, já no S4). Não presumir que precisa desenhar do zero; status de implementação real
   (P-1/P-2) não confirmado, perguntar à Lia.

**Why importa:** a escala genuína da visão (6 peças, 3 horizontes de tempo) exigia formalizar como doc de
arquitetura em vez de só responder no chat — segue o mesmo padrão já estabelecido em
`multi-session-execution-plan.md`/`ia-xslt-synthesis.md`.

**How to apply:** antes de despachar qualquer trabalho novo nesta área pra Dex/Lia, ler
`docs/architecture/ia-fiscal-diagnosis-vision.md` primeiro. Não tratar CFOP/validação semântica fiscal como
extensão trivial do diagnóstico XSD estrutural — é workstream novo, com fonte de conhecimento diferente
(tabela CFOP, não XSD).

**Fechamento (2026-07-21, mesmo dia):** as 5 perguntas do §9 foram todas respondidas pelo dono do projeto —
nada ficou pendente. Confirmações-chave: CHAVEACESSO é LinkMapping (validou a correção de escopo de A6);
CFOP é 100% greenfield (nenhuma das 98 regras DSL toca isso); "tipo de operação" é sempre confiável
(eliminou o sub-caso fuzzy do §4.2); NT-pipeline autorizado a rodar (P-1/P-2, dono Lia); AppConnector — o
próprio dono do projeto tem a visibilidade, gate de sequenciamento confirmado sem mudança. Plano de execução
consolidado de **toda a sessão** (não só visão fiscal) está em
`docs/architecture/ai-roadmap-dispatch.md` — esse é o documento pra Dex/Lia pegarem trabalho, não este
memo nem o doc de visão isoladamente.
