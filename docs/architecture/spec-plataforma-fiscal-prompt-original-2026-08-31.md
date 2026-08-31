# Especificação viva — Plataforma Fiscal (prompt original, 2026-08-31)

> Texto integral do prompt passado pelo dono em 2026-08-31, preservado sem edição/resumo.
> Serve como especificação de referência para os 7 slices de execução (ver seção 15 do texto
> abaixo). Qualquer trabalho de `@lp-architect`/`@lp-backend-dev`/`@lp-parser-llm` nesta
> iniciativa deve remeter a este documento, não a paráfrases em memória de agente.
>
> **Correção de proveniência (2026-08-31, tarde):** a primeira versão deste arquivo continha, por
> engano de um agente anterior (`@lp-pm`), o texto da TAREFA de auditoria dada a ele, não o prompt
> original do dono. Corrigido nesta revisão com o texto real, copiado fielmente da mensagem
> original do dono nesta sessão.

---

Você está trabalhando no repositório:

C:\Users\elson.lopes\source\repos\LayoutParserApi

Sua missão é iniciar a implementação da fundação backend da plataforma fiscal LayoutParser, seguindo o harness, os agentes, as regras de autoridade, os quality gates e o fluxo de Git existentes no repositório.

Use a sequência de agentes:

@lp-architect → @lp-backend-dev → @lp-parser-llm → @lp-qa → @lp-devops

O @lp-devops é o único autorizado a fazer push. Trabalhe em uma branch própria e abra PR para `develop`. Atualize memória, handoff, README, documentação, issues e o GitHub Project durante a execução.

Não implemente UI: o consumidor é o LayoutParserReact.

# 1. Visão do produto

O LayoutParser não é um mapeador genérico. Ele é uma plataforma especializada na criação, explicação, execução e validação de transformações para documentos fiscais brasileiros:

- NF-e;
- CT-e;
- MDF-e;
- NFS-e;
- NFCom.

NFS-e deve considerar município, provedor/padrão e versão como partes da identidade do schema.

Os formatos físicos, como TXT posicional, MQSeries, IDoc, XML e futuramente JSON, são meios de entrada e saída.

TCL e XSL/XSLT são os artefatos de autoria e execução.

Sysmiddle é somente um motor existente que pode ser executado e explicado. O LayoutParser não criará, editará, corrigirá, converterá, compilará ou publicará artefatos Sysmiddle.

O objetivo final é permitir que um especialista receba:

1. amostras do documento de entrada;
2. definição estrutural/layout da origem;
3. planilha Excel de especificação fornecida pelo cliente;
4. XSD oficial da SEFAZ ou provedor fiscal;
5. XML gabarito ou exemplos aprovados, quando disponíveis;
6. contexto fiscal: documento, versão, operação e jurisdição;

e, com a ajuda da IA e revisão humana, produza TCL e XSL/XSLT corretos, explicáveis, testáveis, versionados e publicáveis.

# 2. Fontes de arquitetura

A arquitetura aprovada está na branch/PR do frontend:

- Repositório: LayoutParser/LayoutParserReact
- Branch: `codex/feat-fiscal-workspaces-foundation`
- PR: https://github.com/LayoutParser/LayoutParserReact/pull/207

Documentos locais:

- `C:\Users\elson.lopes\source\repos\LayoutParserReact\docs\architecture\fiscal-document-platform.md`
- `C:\Users\elson.lopes\source\repos\LayoutParserReact\docs\product\fiscal-platform-roadmap.md`
- `C:\Users\elson.lopes\source\repos\LayoutParserReact\docs\product\ai-assisted-fiscal-mapping-studio.md`
- `C:\Users\elson.lopes\source\repos\LayoutParserReact\docs\contracts\fiscal-workspace-and-mapping-explanation-api.md`
- `C:\Users\elson.lopes\source\repos\LayoutParserReact\docs\architecture\adr\0004-sysmiddle-read-only-and-human-in-the-loop-authoring.md`

Leia esses documentos antes de tomar decisões de implementação.

# 3. Issues e governança

GitHub Project da API:

https://github.com/orgs/LayoutParser/projects/2

Issues principais:

- #94 — governança de mappings TCL/XSL/XSLT;
- #103 — feature principal de autoria fiscal assistida;
- #225 — identidade imutável, workspaces e histórico;
- #226 — MappingExplanation para TCL/XSL/XSLT;
- #227 — explicabilidade read-only do Sysmiddle;
- #228 — gate de isolamento cross-workspace;
- #229 — FiscalMappingPackage e inventário;
- #230 — MappingDraft human-in-the-loop e sugestões da IA;
- #231 — compilação TCL/XSL/XSLT e Fiscal Test Lab;
- #232 — gate contra qualquer mutação Sysmiddle.

Mantenha status, responsáveis, evidências, PRs e relações pai/filho atualizados no Project.

# 4. Fronteira obrigatória dos motores

Implemente capabilities explícitas por motor.

Exemplo:

{
  "engine": "sysmiddle",
  "capabilities": {
    "execute": true,
    "explain": true,
    "author": false,
    "compile": false,
    "publish": false
  }
}

Regras obrigatórias:

- TCL: pode explicar, criar Draft, editar, compilar, testar e publicar conforme RBAC/estado.
- XSL/XSLT: pode explicar, criar Draft, editar, compilar, testar e publicar conforme RBAC/estado.
- Sysmiddle: somente executar e explicar.
- Sysmiddle sempre terá `author=false`, `compile=false` e `publish=false`.
- A API deve negar qualquer tentativa de mutação Sysmiddle, mesmo se o cliente estiver adulterado.
- Não basta depender de o frontend esconder botões.
- Não criar endpoints alternativos que contornem essa restrição.
- Não decompilar Sysmiddle.
- Não publicar código, caminhos internos, segredos ou conteúdo protegido.
- Regras Sysmiddle desconhecidas devem aparecer como `opaque` ou `unsupported`, nunca como regra autoritativa inventada.

# 5. Identidade imutável e workspaces

O BFF do LayoutParserReact já foi preparado para remover headers forjados do navegador e encaminhar ao upstream confiável:

- `x-layoutparser-identity-provider`
- `x-layoutparser-identity-subject`
- `x-layoutparser-identity-tenant`

Os headers legados:

- `x-iis-user`
- `x-iis-roles`

não devem ser usados como identidade de propriedade dos novos recursos.

Modelo obrigatório:

unique(provider, tenant_or_issuer, subject)
    → ExternalIdentity
    → UserId interno imutável
    → WorkspaceMembership
    → FiscalWorkspace

Regras:

- nunca usar nome ou e-mail como chave;
- mudança de nome/e-mail não pode criar outro usuário;
- `subject` não pode voltar ao navegador nem aparecer em logs;
- workspace pessoal inicial deve ser criado de forma idempotente;
- concorrência não pode criar usuários/workspaces duplicados;
- um usuário pode participar de vários workspaces;
- toda leitura/escrita valida membership no servidor;
- não revelar se um ID existe em outro workspace;
- substituir gradualmente propriedade nova baseada em `ICurrentUser.Name`;
- avaliar e documentar a migração de dados/tickets legados particionados por nome.

Papéis iniciais:

- Owner;
- FiscalAdmin;
- Mapper;
- Reviewer;
- Operator;
- Viewer.

Endpoints iniciais:

- `GET /api/workspaces/me`
- `GET /api/workspaces/{workspaceId}`
- `POST /api/workspaces/{workspaceId}/projects`
- `POST /api/workspaces/{workspaceId}/projects/{projectId}/analyses`
- `GET /api/workspaces/{workspaceId}/projects/{projectId}/analyses`

O histórico deve suportar paginação por cursor, filtros e políticas:

- `none`;
- `metadata_only`;
- `artifacts_until`.

Não assuma que o conteúdo bruto será persistido porque houve análise.

# 6. Modelo de domínio esperado

Considere os seguintes agregados:

User
└── ExternalIdentity
└── WorkspaceMembership
    └── FiscalWorkspace
        ├── FiscalProject
        │   ├── DocumentAnalysis
        │   │   ├── SourceArtifact
        │   │   ├── ParseSnapshot
        │   │   └── TransformationRun
        │   ├── FiscalMappingPackage
        │   └── MappingDefinition
        │       ├── MappingVersion
        │       ├── MappingDraft
        │       ├── MappingDraftRule
        │       ├── MappingTestCase
        │       └── MappingRelease
        ├── SchemaAsset
        └── RetentionPolicy

Identificadores devem ser opacos e imutáveis, preferencialmente UUID/ULID.

`CorrelationId` rastreia requisições, mas não substitui `AnalysisId`, `RunId`, `PackageId`, `DraftId` ou `MappingId`.

# 7. FiscalMappingPackage

Implemente um pacote versionado e imutável por revisão contendo:

- amostras da origem;
- layout/estrutura da origem;
- planilha de especificação;
- XSD de destino;
- XML gabarito opcional;
- contexto fiscal.

Cada artefato deve registrar:

- ID;
- hash;
- versão/revisão;
- tipo;
- tamanho;
- autor;
- instante;
- classificação;
- política de retenção;
- status de inspeção.

Requisitos:

- validar extensão, conteúdo e MIME real;
- nunca confiar somente no MIME informado pelo navegador;
- aplicar limites de tamanho;
- aplicar antivírus ou política equivalente;
- uploads idempotentes;
- isolamento por workspace;
- conteúdo bruto não aparece em logs ou erros;
- alteração de artefato cria nova revisão;
- um Draft continua ligado à revisão exata que o originou;
- inventário de campos e schemas usa IDs estáveis;
- retornar conflitos, ausências e limitações sem completar lacunas silenciosamente.

Endpoints propostos:

- `POST /api/workspaces/{workspaceId}/projects/{projectId}/mapping-packages`
- `GET /api/workspaces/{workspaceId}/mapping-packages/{packageId}`

Use multipart com metadados JSON, seguindo os padrões já existentes na API.

# 8. MappingDraft human-in-the-loop

A IA não grava diretamente código oficial.

Ela produz uma representação intermediária estruturada, auditável e independente do motor:

{
  "id": "rule_emit_cnpj",
  "sourceRefs": ["source:LINHA004.CNPJ"],
  "targetRefs": ["xsd:nfe.infNFe.emit.CNPJ"],
  "operation": "copy",
  "conditions": [],
  "transformations": ["trim"],
  "cardinality": "1:1",
  "evidence": [
    {
      "kind": "spreadsheet-cell",
      "reference": "Mapeamento!F42"
    },
    {
      "kind": "xsd",
      "reference": "/NFe/infNFe/emit/CNPJ"
    }
  ],
  "confidence": "high",
  "status": "proposed",
  "questions": []
}

Estados mínimos:

- `proposed`;
- `accepted`;
- `edited`;
- `rejected`;
- `needs_input`;
- `validated`;
- `superseded`.

Cada sugestão precisa conter:

- referências de origem;
- referências de destino;
- operação;
- condição;
- transformação;
- cardinalidade;
- evidência;
- confiança;
- limitações;
- perguntas abertas.

Regras obrigatórias:

- ausência de evidência suficiente gera `needs_input`;
- nunca inventar mapping silenciosamente;
- usuário autorizado aceita, edita, rejeita ou responde;
- decisão registra ator, instante, revisão e justificativa;
- usar ETag/`If-Match` para concorrência;
- somente regras `accepted` ou `edited` entram na geração;
- IA não aprova, publica ou promove;
- jobs de IA são assíncronos, idempotentes, canceláveis e observáveis;
- documentos fiscais não podem ser enviados a provedor externo sem política e autorização explícitas;
- feedback humano pode melhorar prompts/contexto do workspace, mas não autoriza treinamento externo implícito.

Endpoints propostos:

- `POST /api/workspaces/{workspaceId}/mapping-packages/{packageId}/drafts`
- `POST /api/workspaces/{workspaceId}/mapping-drafts/{draftId}/suggestions`
- `PATCH /api/workspaces/{workspaceId}/mapping-drafts/{draftId}/rules/{ruleId}`

`engine=sysmiddle` deve ser recusado por todas essas rotas.

# 9. Geração TCL e XSL/XSLT

Implemente o desenho two-step da issue #103:

Etapa 1:
- interpretar pacote, XSD, planilha e exemplos;
- produzir regras intermediárias;
- resolver ambiguidades com o usuário;
- obter revisão humana.

Etapa 2:
- gerar TCL e XSL/XSLT a partir das regras aceitas;
- validar sintaxe;
- explicar o código;
- executar fixtures;
- validar a saída;
- registrar versão e artefatos.

Não pedir ao LLM para inferir regra fiscal e escrever código final em uma única operação opaca.

A geração deve:

- ser assíncrona;
- ser idempotente;
- produzir artefatos versionados;
- ligar diagnóstico sintático à `MappingDraftRule`;
- preservar provenance;
- retornar correlation ID;
- medir timeout, custo e duração;
- considerar que o ambiente atual pode usar modelo local CPU-only.

Endpoint proposto:

- `POST /api/workspaces/{workspaceId}/mapping-drafts/{draftId}/compile`

Sysmiddle deve ser categoricamente recusado.

# 10. MappingExplanation

Implemente contrato canônico independente do motor contendo:

- mapping e versão;
- engine;
- capabilities;
- schema de origem;
- schema de destino;
- regras ordenadas;
- IDs estáveis;
- sourceRefs;
- targetRefs;
- condição;
- operações;
- cardinalidade;
- evidência;
- descrição humana;
- detalhes técnicos;
- supportLevel;
- limitations;
- opaqueRuleCount.

Níveis:

- `authoritative`;
- `best_effort`;
- `opaque`;
- `unsupported`.

Endpoint:

- `GET /api/workspaces/{workspaceId}/mappings/{mappingId}/versions/{version}/explanation`

XSL/XSLT:

- analisar `xsl:template`;
- `xsl:value-of`;
- `xsl:for-each`;
- `xsl:if`;
- `xsl:choose`;
- variáveis;
- selects;
- chamadas conhecidas;
- extensões desconhecidas como `opaque`.

TCL:

- usar parser/AST real;
- não usar regex para inferir estrutura;
- preservar IDs e referências para diff/provenance.

Sysmiddle:

- explicar somente elementos declarativos permitidos;
- correlacionar com `fieldMappings` e `sectionMappings`;
- funções desconhecidas ficam `opaque`;
- nenhuma capability de autoria.

# 11. Fiscal Test Lab

Implemente:

- execução de fixture individual;
- execução de suite versionada;
- validação XML contra XSD;
- validações fiscais complementares existentes;
- comparação canônica de XML;
- cobertura de destinos obrigatórios/opcionais;
- identificação de destinos não mapeados;
- provenance saída → regra → origem/evidência;
- correlation ID entre geração, execução e validação;
- diagnóstico estruturado;
- bloqueio de aprovação/publicação quando um gate obrigatório falhar.

Endpoint proposto:

- `POST /api/workspaces/{workspaceId}/mapping-drafts/{draftId}/test-runs`

A resposta deve permitir ao frontend navegar de uma divergência até:

1. nó do XML;
2. regra aplicada;
3. evidência usada;
4. campo/posição física de origem.

# 12. Governança e publicação

Estados esperados:

Draft
→ InReview
→ Approved
→ Published
→ Deprecated
→ Archived

Fluxo de ambientes:

development
→ validation
→ production

Regras:

- versões publicadas são imutáveis;
- rollback promove versão anterior;
- toda transição registra ator, instante, checks e justificativa;
- produção nunca executa Draft mutável;
- alteração manual em TCL/XSL/XSLT cria nova revisão;
- nova revisão exige regressão;
- aprovação e promoção exigem RBAC;
- integrar com a governança já rastreada na issue #94.

# 13. Segurança e requisitos não funcionais

Obrigatório:

- autorização fail-closed;
- isolamento cross-workspace;
- rate limiting nas rotas de autorização e IA;
- paginação e limites;
- idempotência;
- ETag/concorrência otimista;
- auditoria de criação, edição, aceite, rejeição, aprovação, execução, download e promoção;
- payload fiscal nunca em log;
- subject OIDC nunca em log/resposta;
- URLs e caminhos internos nunca expostos;
- downloads autorizados e temporários;
- sanitização de nome de arquivo;
- proteção contra XML externo/XXE;
- proteção contra zip bomb e arquivos excessivos;
- validação segura de Excel e XSD;
- não executar código arbitrário;
- não usar conteúdo fiscal real em fixtures públicas;
- testes negativos devem falhar se o filtro de workspace for removido;
- zero mutação Sysmiddle.

# 14. Primeiro caso vertical: FIAT NF-e 4.00

O primeiro piloto é o caso FIAT.

Artefatos atualmente disponíveis no frontend:

C:\Users\elson.lopes\source\repos\LayoutParserReact\.codex\temp\teste

- `LAY_ad4fb6f4-9ff5-44fd-988b-3da5ed56b22c.xml`
- `QMWNFe1_QMWNFE1.SAPiens_MRB.INBOX_07-11-2025.mq_series.txt`

Ainda faltam no pacote:

- planilha Excel;
- XSD oficial;
- XML gabarito, preferencialmente.

Não publique os documentos reais no GitHub. Crie fixtures sintéticas ou sanitizadas para testes automatizados.

Gate FIAT:

- pacote criado no workspace correto;
- inventário determinístico;
- IA propõe regras com evidência;
- ambiguidades viram perguntas;
- especialista revisa todos os campos obrigatórios;
- TCL/XSL/XSLT são gerados;
- código é explicável;
- execução produz XML válido no XSD;
- XML pode ser comparado ao gabarito;
- divergências possuem provenance;
- regressão antecede publicação;
- todos os correlation IDs são conectáveis;
- nenhum artefato Sysmiddle é alterado.

# 15. Sequência de implementação

Execute em slices verticais:

## Slice 1 — Identidade e workspace

Issue #225 e gate #228.

Entregar:

- ExternalIdentity;
- UserId interno;
- FiscalWorkspace;
- Membership;
- `/api/workspaces/me`;
- isolamento e testes negativos.

## Slice 2 — FiscalMappingPackage

Issue #229.

Entregar:

- modelo;
- persistência;
- upload;
- inventário;
- revisão imutável;
- OpenAPI;
- testes de segurança.

## Slice 3 — MappingDraft human-in-the-loop

Issue #230 e feature #103.

Entregar:

- modelo intermediário;
- estados;
- revisão otimista;
- sugestões da IA;
- evidência;
- perguntas;
- auditoria.

## Slice 4 — MappingExplanation

Issues #226 e #227.

Entregar:

- contrato canônico;
- adapter XSL/XSLT;
- adapter TCL;
- explicação Sysmiddle read-only;
- capabilities.

## Slice 5 — Compilação e Test Lab

Issue #231.

Entregar:

- geração TCL/XSL/XSLT;
- execução;
- validação XSD;
- diff;
- cobertura;
- provenance;
- regressão.

## Slice 6 — Gate Sysmiddle

Issue #232.

Entregar testes provando que nenhum endpoint, payload adulterado, role ou estado permite mutação Sysmiddle.

## Slice 7 — Governança e piloto FIAT

Issue #94 e gate frontend #206.

Entregar:

- versionamento;
- revisão;
- aprovação;
- publicação;
- rollback;
- cenário ponta a ponta sanitizado.

# 16. Handoff esperado para o frontend

Para cada contrato entregue:

- atualizar OpenAPI;
- fornecer request/response completo;
- documentar status HTTP e taxonomia de erros;
- documentar ETag, idempotência e paginação;
- fornecer fixture sintética;
- informar capabilities;
- informar campos opcionais/nulos;
- preservar `X-Correlation-ID`;
- registrar versão do contrato;
- comentar nas issues correspondentes do LayoutParserReact;
- não marcar a entrega como aceita sem validação explícita do `@lp-contract-qa`.

Issues consumidoras do frontend:

- LayoutParserReact#195;
- #199;
- #200;
- #201;
- #202;
- #203;
- #204;
- #205;
- #206.

# 17. Quality gates e entrega

Antes de concluir cada slice:

- executar todos os testes do repositório;
- validar lint/format/build;
- executar testes de autorização;
- executar testes cross-workspace;
- executar testes de adulteração Sysmiddle;
- validar OpenAPI;
- executar análise de segurança;
- verificar ausência de payload fiscal/PII nos logs;
- atualizar README e arquitetura;
- atualizar memória e handoff;
- atualizar issues e Project;
- criar commit convencional;
- usar @lp-devops para push;
- abrir PR para `develop`.

Não faça push direto em `develop` ou `main`.

# 18. Restrições finais

Não:

- implemente frontend;
- use e-mail/nome como identidade;
- persista dados fora do workspace;
- invente regra fiscal;
- publique sugestão da IA automaticamente;
- implemente editor Sysmiddle;
- converta Sysmiddle para TCL/XSLT;
- altere mapper Sysmiddle;
- exponha código proprietário;
- registre documento fiscal em log;
- crie mocks de produção que escondam ausência de contrato;
- marque issue como concluída sem evidência.

Comece pela auditoria do estado atual das issues #225 e #228 e da implementação de identidade existente. Reaproveite o que já existe, registre qualquer drift e siga a sequência vertical. Não recomece do zero.

---

*Nota de proveniência: as issues #229 (Slice 2), #230 (Slice 3), #226/#227/#232 (Slice 4), #231
(Slice 5), #94 (Slice 6 — governança admin) já existiam no backlog antes deste prompt, criadas
a partir da mesma especificação em sessão anterior (ver `docs/architecture/auditoria-slice1-identidade-workspaces-2026-08-31.md`
e o parent #103 "autoria fiscal assistida a partir de amostras + Excel + XSD"). A issue #206
citada no prompt não existe no repositório — provável erro de digitação ou issue nunca criada;
sinalizado na auditoria (`resumo-sessao-2026-08-31.md`).*
