# Arquitetura — Diagnóstico fiscal semântico, rastreamento input→output e substituição do AppConnector

> **PT-BR** · Visão de longo prazo pra IA nesta API, além da síntese de XSLT (que já tem doc próprio:
> [`ia-xslt-synthesis.md`](ia-xslt-synthesis.md)). Cobre: pré-análise semântica fiscal (CFOP × tipo de
> operação), rastreamento de defeito input→output via o sidecar de proveniência, filtro de erro
> conhecido-e-aceito (assinatura digital), e o objetivo de longo prazo de substituir o **AppConnector**
> (Sysmiddle) em produção pelo TCL/XSLT gerado.
>
> **Autor:** `@lp-architect` (Aria) · **Origem:** visão do dono do projeto, recebida via handoff do
> coordenador em 2026-07-21 · **Status:** design — nada aqui é código de produção.
> **Relação com outros docs:** [`ia-xslt-synthesis.md`](ia-xslt-synthesis.md) cobre a mecânica de gerar
> XSLT (é pré-requisito do §5 mid-term aqui); [`multi-session-execution-plan.md`](multi-session-execution-plan.md)
> §10 registra a fase A6 (proveniência) com escopo corrigido por este doc; [`nt-pipeline-design.md`](nt-pipeline-design.md)
> já cobre boa parte do §7 aqui (auto-atualização por NT).

---

## 1. A visão completa (traduzida/organizada, exemplos preservados)

| # | Peça | Exemplo concreto dado pelo dono do projeto |
|---|------|---------------------------------------------|
| 1 | Pré-análise semântica fiscal, não só estrutural | XSD sozinho não pega "CFOP de venda está errado porque a nota é devolução" |
| 2 | Rastreamento defeito input→output via sidecar | Cliente manda CHAVEACESSO com 43 dígitos (não 44); o XML output sai errado — precisa rastrear o campo de output até o campo de input culpado |
| 3 | IA desenvolve TCL/XSL autonomamente pra produção | Hoje o "gabarito" roda via **AppConnector** (Sysmiddle) no app **FiatMQ**; visão é substituir por TCL/XSLT gerado |
| 4 | Auto-atualização quando sai NT nova da SEFAZ | Buscar/receber NT nova, ajustar a transformação sozinha; assimetria: lado output é rastreável, lado input é sempre reativo (depende do cliente mandar) |
| 5 | Filtro de erro conhecido-e-aceito | Validação XSD do XML final **sempre** acusa erro de assinatura digital (documento ainda não assinado) — não é defeito real |
| 6 | Fork open-source de verdade, treinado especificamente | Reafirma o pedido de trazer código pra casa e treinar — agora também pro diagnóstico semântico fiscal, não só geração sintética |

## 2. Fases — near / mid / long term

```
NEAR-TERM  (podem começar já, paralelos entre si)
├─ 3. Diagnóstico + rastreamento (sidecar corrigido, validação determinística, filtro de assinatura)
└─ 4. Validação semântica fiscal (CFOP × tipo de operação) — workstream NOVO, ainda não iniciado

MID-TERM   (= Trilha A já em andamento, agora com racional de negócio mais forte)
└─ 5. Qualidade do TCL/XSLT gerado (A1-A6, ver multi-session-execution-plan.md)

LONG-TERM  (gated no mid-term provar qualidade suficiente; maior incerteza de sistemas)
└─ 6. Substituir AppConnector em produção (FiatMQ)

TRANSVERSAL (parcialmente já desenhado em doc separado)
└─ 7. Auto-atualização por NT nova (ver nt-pipeline-design.md)
```

---

## 3. Near-term A: diagnóstico + rastreamento

### 3.1 Correção de escopo do sidecar de proveniência (achado nesta sessão)

O design original do sidecar (`generated-provenance.json`, XPath de saída → {regra, fonte, campos de
input}) foi escopado só em `DslRuleTranslator.TranslateAsync` (`ai/XslSynth/Synthesis/`) — cobre os **~98
campos via regra DSL**. Lendo `ai/XslSynth/Core/LinkMappingTranspiler.cs` (os **~237 campos via LinkMapping
direto** — a maioria) descobri que esse caminho **não emite o sidecar** — ele embute um `XComment` (`target=
{TargetGuid} type={TargetType} input={InputGuid} guid-catalog=...xpath=...`) como **filho do próprio
elemento de saída, dentro do XSLT**. Isso tem duas consequências:

1. **O sidecar como escopado originalmente perdia a maioria dos campos — confirmado, não era só
   suspeita.** Pergunta fechada em 2026-07-21 (§9): **CHAVEACESSO é LinkMapping**, confirmado direto pelo
   dono do projeto ("CHAVEACESSO seria uma tag no layout txt que vai ter um linkmapping para o output
   xml"), sem precisar decriptar nada. Ou seja, o exemplo real que motivou a peça 2 da visão **ficaria
   invisível** no escopo original (só `DslRuleTranslator`). A correção já aplicada (cobrir
   `LinkMappingTranspiler` **e** `DslRuleTranslator`) era necessária de verdade, não precaução — mantida.
2. **O `XComment` atual é exatamente o anti-padrão que a decisão original de proveniência (nunca embutir
   no XML de saída) queria evitar** — e, diferente da dúvida original ("a sobrevivência de comentário
   através de `XslCompiledTransform.Transform()` nem está confirmada"), agora tenho mais certeza: como é
   filho literal do elemento de resultado (não uma instrução `xsl:`), esse comentário **sobrevive sim** à
   transformação por padrão. Hoje isso só afeta o loop offline/prototype (não é usado em produção), mas
   **precisa de gate explícito antes de qualquer promoção pra produção:** um passo de "publicação" que (a)
   remove esses comentários de debug do XSLT final, (b) gera o sidecar de verdade a partir da mesma
   informação (target/input/xpath já estão ali, só precisam virar JSON estruturado em vez de comentário
   inline), (c) grava o XSLT limpo em `Mapper.XslContent`. **Esse passo de publicação não existe hoje** —
   é um item de trabalho novo, não é conserto de bug, é peça faltante do pipeline.

### 3.2 Validação determinística de campo de input (o caso CHAVEACESSO não precisa de IA)

A memória da Lia `poc3-r4-estado.md` já documenta a decomposição exata: **CHAVEACESSO (44 dígitos) =
cUF(1-2) AAMM(3-6) CNPJ(7-20) mod(21-22) serie(23-25) nNF(26-34) tpEmis(35) cNF(36-43) cDV(44)** — dado
verificado contra gabarito real. Combinado com o fato de o Layout XML já declarar `LengthField` por campo
(`GeminiAIService.ExtractDetailedLayoutInfo` já lê isso), **o defeito do exemplo (43 em vez de 44 dígitos)
é 100% detectável sem IA nenhuma:** validar o campo de input isolado contra seu próprio `LengthField`
declarado (e, se quiser ir além, recalcular o dígito verificador `cDV` — algoritmo módulo-11 público da
NF-e — pra confirmar integridade, não só tamanho).

**Implicação de design:** o loop de diagnóstico não deveria chamar Ollama pra esse tipo de defeito. A
sequência correta é: (1) validação determinística por campo de input (tamanho/formato/checksum, usando
dado que o projeto já tem) → se pegar o defeito, **explica sem IA, ou com IA só pra redigir a explicação em
linguagem natural** (tarefa fácil, não precisa de "entendimento", só de template); (2) só se a validação
determinística **não** explicar o erro XSD é que vale montar o prompt cirúrgico pro Ollama (usando o sidecar
corrigido do §3.1 pra apontar a regra/campo envolvido). Isso reduz a carga do LLM pros casos que
genuinamente precisam de "julgamento", não pra checagem de tamanho de campo.

### 3.3 Filtro de erro conhecido-e-aceito (assinatura digital) — requisito de design novo

Confirmado como requisito, não nice-to-have: validar o XML final contra XSD **sempre** vai acusar ausência
de assinatura digital (documento não assinado ainda) — isso não é defeito. **O loop de diagnóstico precisa
de uma camada de classificação ANTES de gerar qualquer explicação:** reconhecer o padrão de erro de
assinatura ausente (mensagem/elemento XSD conhecido, ex. relacionado a `Signature`/`ds:Signature`) e
**suprimir silenciosamente** esse item da lista de defeitos reportados ao usuário (ou reportar como
categoria separada "esperado", nunca como "erro"). Sem isso, 100% dos documentos vão gerar o mesmo ruído
repetido, e qualquer diagnóstico real fica enterrado. Isso é um filtro determinístico (pattern-match no
erro XSD), não precisa de IA — só precisa ser lembrado explicitamente no design dos itens 3-5.

---

## 4. Near-term B: validação semântica fiscal (CFOP × tipo de operação) — workstream novo

**Confirmado (grounding do coordenador):** `XsdValidationService.cs:380` já cita CFOP, mas só como dica
textual genérica pro usuário ("valide que CFOP está correto") — não é validação semântica real. **Isso é
escopo novo, não retrabalho.**

### 4.1 Fine-tuning reconsiderado — ainda não, mas a razão muda (fica mais forte, não mais fraca)

Reconsiderei de verdade, não só reafirmei a decisão anterior por inércia. Conclusão: **ainda não fine-tuning
— mas por um motivo diferente e mais forte** do que o caso XSD-estrutural/geração-XSLT:

- CFOP é uma **tabela pública, finita, enumerável e versionada** (publicada por SEFAZ/Receita Federal) que
  classifica a natureza de uma operação (venda, devolução, transferência…). A pergunta "CFOP X é válido pra
  uma operação do tipo Y" é fundamentalmente um **problema de lookup/cruzamento de tabela**, não um problema
  de reconhecimento de padrão que se beneficia de "intuição aprendida".
- Fine-tuning aqui seria pedir pra uma rede neural **decorar uma tabela de consulta** que deveria simplesmente
  ser **indexada e consultada diretamente** (RAG sobre a tabela real de CFOP × tipo de operação, não sobre
  exemplos de documentos). É estritamente pior que indexar a tabela: opaco, precisa re-treinar a cada
  atualização da tabela (SEFAZ atualiza CFOP com menos frequência que NT, mas atualiza), pode alucinar em
  caso de borda.
- **O papel do LLM continua modesto:** explicar a divergência em linguagem natural depois que o
  cruzamento de tabela (determinístico) já identificou o problema — mesma divisão de trabalho do §3.2.

**O que precisa existir de novo (confirmado em 2026-07-21 — é 100% greenfield, sem atalho de
reaproveitamento, mas também mais simples do que parecia):**
1. **Tabela/base de CFOP × tipo de operação indexada** — construir a partir das tabelas públicas da
   SEFAZ/Receita Federal (não existe hoje no projeto, além da dica textual genérica confirmada no início
   deste §4). **Confirmado pelo dono do projeto: nenhuma das 98 regras DSL já mapeadas toca CFOP** — não
   há reaproveitamento de regra existente, é construção nova de ponta a ponta. Ele também corrigiu o
   enquadramento: não é sobre achar atalho, é sobre o vácuo que existe HOJE (nenhum mecanismo — regra DSL,
   validação, nada — pega CFOP semanticamente errado) — é exatamente esse vácuo que a feature preenche, e
   é a justificativa de negócio mais direta que temos pra essa peça especificamente.
2. **Confirmado: "tipo de operação" é SEMPRE declarado de forma confiável no documento** (resposta direta
   do dono do projeto, sem ressalva — o exemplo dado era só ilustrativo). **Isso elimina o sub-caso fuzzy
   que este documento tinha aqui antes** (classificador dedicado pra inferir tipo de operação quando não
   declarado) — não existe essa complicação. A peça inteira vira **lookup determinístico puro** assim que
   a base do item 1 existir: cruzar CFOP declarado × tipo de operação declarado × tabela válida. O LLM
   entra só pra explicar a divergência em linguagem natural depois que o lookup já identificou o problema
   — mesma divisão de trabalho do §3.2.
3. **Risco real a resolver antes/junto:** a memória da Lia `rag-fewshot-b4.md` documenta um bug conhecido
   do `DslBlockInterpreter` em regras DIFÍCEIS (com `else`/`&&`/aninhamento) — consome só os blocos que
   reconhece e pode gerar emissão incondicional errada "com cara de verificada". Regra de negócio tipo
   "CFOP inválido pra esse tipo de operação" é EXATAMENTE o tipo de lógica condicional aninhada que
   dispararia esse bug (mesmo que não exista ainda como regra DSL — se algum dia essa validação for
   expressa nesse formato, herda o risco). Recomendo tratar isso como pré-requisito/risco compartilhado,
   não ignorar.

---

## 5. Mid-term: qualidade do TCL/XSLT gerado (= Trilha A, já em andamento)

Isso já é o Trilha A (A1-A6, ver `multi-session-execution-plan.md`). O racional de negócio que o dono do
projeto deu (substituir o AppConnector em produção) não muda a mecânica, mas **eleva a régua de qualidade**
e adiciona um item concreto ao critério de aceite:

**Achado (`tools/LowCodeRunner/SysmiddleMapperExecutor.cs`):** o comentário do próprio arquivo diz "Lógica e
pós-processamento NF-e portados do appConnector.Client MappersHelper" — ou seja, **já existe código real
que documenta como o AppConnector processa hoje**, incluindo pós-processamentos específicos
(`ChangeInfCplValues`/`ChangeInfIdFiscoValues`/`ChangeInfAdProdValues` — escapam `<`/`>` dentro de
`infCpl`/`infAdFisco`/`infAdProd`). **Qualquer TCL/XSLT que pretenda substituir o AppConnector em produção
precisa replicar esses mesmos pós-processamentos** — é um checklist concreto, não abstrato, pra adicionar
ao critério de "qualidade suficiente" antes do long-term (§6).

**Lembrete que já está documentado** (`ia-xslt-synthesis.md` §8): o protótipo usa `XslCompiledTransform`
(XSLT 1.0); produção provavelmente exige Saxon (XSLT 2.0/3.0). Isso importa MAIS agora que existe um
objetivo real de produção — não é só nice-to-have de longo prazo.

---

## 6. Long-term: substituir o AppConnector em produção (app "FiatMQ")

**Correção da premissa "repo desconhecido/distante":** busquei "AppConnector" no repo inteiro — **não é um
repositório externo desconhecido.** São as DLLs Sysmiddle já presentes localmente em
`tools/LowCodeRunner/Functions/` (`appConnector.Client.Data.dll.config`,
`appConnector.Mapper.Functions.dll.config`), e a instância real (`Instance_FiatMQ/AppConnector.DIR/Bin`) é
exatamente onde a Trilha A já roda o `.exe` do runner pra gerar gabaritos (confirmado na memória da Lia
`multi-client-mappers.md`). "FiatMQ" = nome da instância do cliente FIAT (MQSeries) que a Trilha A já usa,
não um produto desconhecido à parte.

**O que isso muda:** já existe visibilidade real e código funcional (`SysmiddleMapperExecutor.cs`) sobre
**como o AppConnector transforma hoje** (via `APIManager`/`APIExecutor` do SDK Sysmiddle) — isso é
groundwork de verdade, não zero.

**O que continua genuinamente incerto (por isso mantenho como long-term, sequenciado por último):**
1. **Não temos código-fonte do AppConnector em si** (é DLL compilada + config, não repo buildável nosso) —
   substituir o PAPEL dele não é "fazer fork e modificar", é construir nossa própria alternativa e trocar
   com segurança, o que é um tipo de risco diferente (integração/corte de produção), não falta de
   visibilidade.
2. **Não sabemos como o AppConnector é de fato invocado no fluxo de produção real** (listener de MQ? job
   agendado? outro gatilho?) — o que a Trilha A tem hoje é o **modo manual/CLI** (`SWEEP`/`EXEC` pra gerar
   gabaritos), não necessariamente o mesmo mecanismo de disparo da produção real.
3. **Gate de sequenciamento:** só faz sentido avançar aqui depois que o mid-term (§5) provar qualidade
   suficiente em múltiplos clientes reais (não só FIAT) — cortar produção pra um XSLT ainda não validado
   contra IVECCO/MARELLI/etc. é risco desproporcional ao ganho.

**Recomendação:** tratar como iniciativa separada e sequenciada por último, mas **não** como "distante e sem
visibilidade" — há mais chão já pisado do que a formulação original sugeria.

**Confirmado em 2026-07-21:** o próprio dono do projeto tem a visibilidade do disparo real de produção do
AppConnector ("Eu tenho a visibilidade do AppConnector, mas isso é futuro, só vamos trocar Sysmiddle pelos
mapeadores TCL e XSL quando eles estiverem 100% confiáveis"). Ou seja, não é bloqueio de informação — é
bloqueio de sequenciamento mesmo, exatamente como já desenhado aqui, e ele é a fonte quando chegar a hora.
Nada muda no gate do mid-term; só fica confirmado que ele existe e sabe operá-lo.

---

## 7. Auto-atualização por NT nova — já parcialmente desenhado

`docs/architecture/nt-pipeline-design.md` (status: "Proposta", fase B5) **já cobre boa parte disso** —
não precisei desenhar do zero:

- Já separa "o que mudou" (diff de XSD, determinístico, S1) de "o que significa" (semântica do PDF da NT,
  LLM com citação obrigatória de página/seção, S2) — mesmo princípio que o dono do projeto está pedindo.
- **A assimetria que ele descreveu (lado output rastreável/buscável vs lado input sempre reativo) já está
  registrada explicitamente no S4** ("posição em layout posicional é autoridade do CLIENTE... o pipeline
  propõe... nunca aplica automaticamente... revalida a proposta contra o layout real" quando o cliente
  manda a versão nova). Não precisa de doc novo pra isso, já existe.
- **Confirmado em 2026-07-21: P-1/P-2 não rodaram ainda** — o dono do projeto deu sinal verde explícito
  ("Não foi rodado ainda o protótipo do nt-pipeline-design.md então pode rodar ele"). Isso autoriza tirar
  P-1 (XsdDiff CLI) e P-2 (smoke de extração do PDF) do estado "proposta" e despachar como trabalho ativo —
  dono conforme a tabela original do próprio doc (`nt-pipeline-design.md` §5): **Lia**. P-3 (diff real
  `PL_009×PL_010b`) continua bloqueado por dado externo (pacote XSD antigo), não por código.
- **Nota de consistência menor:** o doc permite nuvem pra S2 ("a NT é documento público... nuvem é
  permitida") — isso foi escrito antes da decisão de decomissionar Gemini/OpenAI por completo (ver memória
  `gemini-openai-decommission-decision`). Não é contradição grave (a lógica de "documento público" ainda é
  válida em princípio), mas por consistência com a decisão mais recente, recomendo usar Ollama local aqui
  também por padrão, e só reconsiderar nuvem se a qualidade local se provar insuficiente pra prosa jurídica
  complexa — não abrir uma exceção de nuvem sem necessidade comprovada.

---

## 8. Ordem recomendada

```
JÁ RODANDO, PARALELOS (near-term):
  A6-corrigido  [sidecar cobre LinkMapping+Rule, remove XComment de debug antes de produção] .... Lia+Dex
  filtro-assinatura [classificador determinístico, requisito dos itens 3-5] ..................... Dex
  validação-input-determinística [tamanho/checksum antes de acionar Ollama] ...................... Dex

NOVO, PODE COMEÇAR EM PARALELO AO ACIMA (confirmado greenfield, sem reaproveitamento — ver §4.2):
  cfop-rag [construir tabela CFOP×tipo-operação indexada, do zero] ................................ Lia
    (depende de: nada pra começar; considerar junto o bug do DslBlockInterpreter em regras aninhadas)
  nt-pipeline P-1/P-2 [autorizado a rodar 2026-07-21 — sai de "proposta" pra trabalho ativo] ....... Lia
    (depende de: nada — P-3 que segue bloqueado por dado externo, não P-1/P-2)

MID-TERM (Trilha A em andamento, critério de aceite ampliado com checklist do §5):
  A1-A5 (já rastreado em multi-session-execution-plan.md)

LONG-TERM (gated no mid-term):
  substituição do AppConnector em produção — não iniciar antes de qualidade multi-cliente provada
```

## 9. Perguntas fechadas em 2026-07-21 (respondidas pelo dono do projeto — nada pendente)

1. **CHAVEACESSO é Rule ou LinkMapping?** → **LinkMapping**, confirmado direto (sem decriptar nada). Validou
   a correção de escopo de A6 (§3.1) — era necessária de verdade.
2. **Alguma das 98 regras DSL já toca CFOP?** → **Não.** 100% greenfield, sem reaproveitamento (§4.2).
3. **"Tipo de operação" é sempre declarado de forma confiável?** → **Sim, sem ressalva.** Elimina o sub-caso
   fuzzy que existia no §4.2 (removido).
4. **Estado real do `nt-pipeline-design.md`?** → **Não rodou ainda; sinal verde pra rodar agora** (§7).
   Dono: Lia (já era o dono designado na tabela original do doc).
5. **Quem tem visibilidade do disparo real do AppConnector?** → **O próprio dono do projeto** (§6). Confirma
   o gate de sequenciamento já desenhado; nada muda.

Plano de execução consolidado (este doc + todas as decisões da sessão, não só a visão fiscal):
[`ai-roadmap-dispatch.md`](ai-roadmap-dispatch.md).

---

*LayoutParser · Diagnóstico fiscal semântico e rastreamento · v1 · `@lp-architect` · 2026-07-21*
