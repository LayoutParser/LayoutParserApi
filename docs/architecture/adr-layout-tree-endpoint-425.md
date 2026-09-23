# ADR: Endpoint de árvore de layout dupla (origem/destino) para mapeador Sysmiddle — issue #425

- **Status:** Aceito
- **Data:** 2026-09-16
- **Autor:** @lp-architect (Aria)
- **Issues relacionadas:** LayoutParser/LayoutParserApi#425 (bloqueia LayoutParserReact#267). Fora de escopo: #417 (descoberta/catálogo de mappers por workspace).

## Contexto

O React precisa replicar a UI de edição de mapeador do Connect Us
(`SysMiddle.ConnectUs.WindowsApp.exe`): duas `TreeView`s lado a lado (layout de
origem TXT/MQSeries à esquerda, layout de destino XML à direita), cada nó com
cardinalidade `(-, min, max)` e sub-nós `Regra_xxx` pendurados nos elementos
com regra vinculada. O consumidor **já sabe** qual mapeador quer visualizar
(tem `mapperGuid`/`mappingId`) — não é descoberta de catálogo (#417), é leitura
de um mapeador específico.

Levantamento nesta sessão, antes da decompilação:

- `MappingExplanationController` (`GET .../mappings/{mappingId}/versions/{version}/explanation`)
  já devolve `sourceRefs`/`targetRefs` por regra, mas em formato inconsistente
  entre engines — só o formato Sysmiddle (GUIDs reais `FLD_<guid>`/`TAG_<guid>`)
  bate com IDs de nó de árvore verdadeiros.
- `Models/Entities/Layout.cs`/`LineElement.cs` (modelo hoje usado em produção via
  `CachedLayoutService`/`LayoutDatabaseService`) é **flat**, sem hierarquia,
  sem atributos, sem sequence/choice, e `MaximumOccurrence` já é dado morto
  (investigação anterior). Não serve de fonte para a árvore.
- `ai/XslSynth.Contracts/Core/GuidXPathCatalog.cs` já lê o **mesmo arquivo**
  LayoutVO exportado do Connect Us (`xsi:type="TextLayoutVO"`/`"XmlLayoutVO"`),
  reconstrói hierarquia real por `ElementGuid`, distingue elemento/atributo/
  wrapper estrutural (`Choice`/`Sequence`), mas (a) é ferramenta offline
  baseada em caminho de arquivo local, não integrada ao lookup por GUID do
  banco, e (b) não extrai `MinOccurs`/`MaxOccurs`.

Para fechar a lacuna de cardinalidade e confirmar que não existe uma
superfície de dados diferente (API in-process, cache, banco) por trás da
árvore do Connect Us, o dono sugeriu inspecionar as DLLs de referência em
`D:\ConnectUs\Assemblies\` via `ilspycmd`.

## Investigação (decompilação exploratória, só estrutura)

`ilspycmd` (10.1.0.8386) está disponível no PATH (`~/.dotnet/tools`).
Decompilação limitada a **assinaturas de classe/propriedade** — não a corpos
de método com lógica de negócio proprietária — nas DLLs:

- `SysMiddle.MapControl.Core.dll`: **ofuscada** (nomes de classe tipo
  `mjldbepFpfgR2sirhk.Kusbq8F7xd8hvTfPmi`, atributo `[ObfuscationAttribute]`
  presente). É o motor de conectores/execução, não tem tipos de
  layout/árvore — irrelevante para este ADR.
- `SysMiddle.ConnectUs.Core.dll`: só achados residuais (`TextLayoutEspecificFileVO`),
  nada de modelo de árvore.
- `SysMiddle.ConnectUs.WindowsApp.exe`: `Model.TreeElementVO` — o view-model da
  `TreeView` do desktop (renderização, não fonte de dados).
- `SysMiddle.Base.dll`: **não ofuscada nos nomes de tipo/propriedade** (os
  *corpos* de método vêm stripped/substituídos por stubs — proteção aplicada,
  mas não impede ler a forma dos dados). Contém exatamente a hierarquia que
  `GuidXPathCatalog` já lê:
  - `Elements.ElementVO` (abstrato, base) → `Elements.WithChildrenElementVO` →
    `Elements.ParentOccurrenceVO` com `[XmlElement("MinimalOccurrence")]` /
    `[XmlElement("MaximumOccurrence")]` — **a cardinalidade que falta**.
  - `Elements.Xml.GroupTagElementVO : ParentOccurrenceVO` (tem filhos e
    ocorrência — é o nó "grupo" da árvore, com `(-, min, max)`).
  - `Elements.Xml.TagElementVO : ValuedElementVO, IParent, IRepetionElement`
    (elemento folha/valor).
  - `Elements.Xml.AttributeElementVO`, `Elements.Xml.SequenceElementVO`,
    `Elements.Xml.ChoiceElementVO` — mesmos nomes que `GuidXPathCatalog` já
    trata como wrappers estruturais.
  - `LinkMappingItemVO : ElementVO` com `[XmlElement("InputLayoutGuid")] public
    string SourceElementGuid` e `[XmlElement("TargetLayoutGuid")] public string
    TargetElementGuid` — **confirma que o modelo de regra do Connect Us usa os
    mesmos GUIDs de nó da árvore** que já aparecem em `sourceRefs`/`targetRefs`
    da explanation Sysmiddle atual, e que `GuidXPathCatalog.TryResolve` já sabe
    casar.

**Conclusão da investigação:** não existe uma superfície de dados paralela
(API in-process, cache, banco separado). O Connect Us monta a árvore dupla e
os vínculos de regra a partir do **mesmo arquivo LayoutVO exportado** que
`GuidXPathCatalog` já sabe parsear — só falta ler dois campos a mais
(`MinimalOccurrence`/`MaximumOccurrence`) que já existem no XML, na classe
`ParentOccurrenceVO`, e não são lidos hoje pelo catálogo.

### Sobre a segurança/legitimidade da decompilação

Escopo respeitado: só nomes de tipo, propriedade e atributos de
serialização (`[XmlElement(...)]`) foram inspecionados — nenhum corpo de
método com lógica de negócio foi reconstruído ou copiado (e, de fato, os
corpos vêm stripped/obfuscados nas classes de execução, tornando isso
impraticável mesmo que fosse a intenção). Isso é equivalente a inspecionar o
schema de um formato de arquivo já documentado internamente pelo próprio
`GuidXPathCatalog.cs` (comentário do arquivo já descreve a estrutura por
`xsi:type`) — não há cópia de UI nem de algoritmo proprietário. Recomendo
**não versionar** o dump completo do decompile (`/tmp/base_decompiled.txt`,
fora do repo, correto) nem redistribuir as DLLs do Connect Us pelo
LayoutParserApi.

## Decisão

1. **Generalizar `GuidXPathCatalog`, não recomeçar do zero.** Ele já resolve
   ~90% do problema (parsing de hierarquia real + GUID estável). O trabalho
   novo é:
   a. Extrair `MinimalOccurrence`/`MaximumOccurrence` de cada
      `ParentOccurrenceVO`/`GroupTagElementVO` durante o `Caminha(...)`
      recursivo (hoje só monta `GuidXPathEntry` sem esses campos).
   b. Trocar a fonte de "caminho de arquivo local" (`GuidXPathCatalog.Load(path)`)
      por integração ao lookup por GUID já existente no banco — via o mesmo
      caminho que `LayoutDatabaseService`/`CachedLayoutService` usam para
      achar o layout físico associado a um `mapperGuid` (o achado da sessão já
      confirma que `layoutGuid` cobre tanto TXT quanto XML, distinguido por
      `xsi:type`). Não duplicar essa resolução — reaproveitar o serviço
      existente para localizar o arquivo/BLOB do LayoutVO, só trocar o
      consumidor final (de "gerar XSLT offline" para "responder árvore via
      API").
   c. Mover (ou extrair para um projeto/serviço compartilhado) a lógica de
      `GuidXPathCatalog` de `ai/XslSynth.Contracts` para um lugar consumível
      pela API principal sem acoplar o domínio de runtime ao projeto de
      síntese IA — ver "Consequências" abaixo sobre o *boundary*.
2. **Não usar `Models/Entities/Layout.cs`** como fonte da árvore — ele é
   estruturalmente incompatível (flat) e seria retrabalho para chegar a menos
   do que o parser do LayoutVO já entrega.
3. **Reaproveitar e consertar `sourceRefs`/`targetRefs`** da
   `MappingExplanationController`/adapter Sysmiddle para apontar
   exclusivamente pelos GUIDs de nó (`FLD_`/`TAG_`/`GRT_`/`ATT_`/`LIN_`) —
   já é o formato usado pelo Sysmiddle adapter; o trabalho aqui é validação
   cruzada, não redesenho.

## Alternativas consideradas

| Opção | Por que rejeitada |
|---|---|
| Reimplementar o parser de árvore do zero a partir da decompilação do Connect Us | Redundante — `GuidXPathCatalog` já faz o parsing correto do mesmo arquivo; decompilar serviu só para confirmar o modelo, não para copiar implementação. |
| Usar `Models/Entities/Layout.cs`/`LineElement.cs` como fonte | Estruturalmente flat, sem grupo/choice/sequence real, cardinalidade morta — exigiria reconstruir a hierarquia a partir de convenção de nome, frágil e já rejeitado em investigação anterior desta sessão. |
| Expor um novo serviço que chama o Connect Us/DLLs em runtime | Fora do boundary do projeto (regra `.claude/CLAUDE.md` §1: lógica de runtime fica na API, não em dependência de app desktop de terceiro); DLLs não são um contrato público, são obfuscadas nos pontos de execução, e acoplar a API a `SysMiddle.MapControl.Core.dll` introduz uma dependência externa frágil e sem contrato de resiliência (viola princípio "degradar, nunca derrubar"). |
| Fazer o React parsear o LayoutVO XML diretamente (bypass da API) | Viola boundary de repos (§1 do CLAUDE.md: lógica de domínio fica na API) e duplicaria a lógica de parsing GUID→hierarquia em dois lugares (Front e `GuidXPathCatalog`), risco de divergência. |

## Contrato de resposta proposto

```
GET /api/workspaces/{workspaceId}/mappings/{mappingId}/layout-tree

{
  "mapperGuid": "MAP_...",
  "source": { "layoutGuid": "LAY_...", "kind": "text", "root": <LayoutTreeNode> },
  "target": { "layoutGuid": "LAY_...", "kind": "xml",  "root": <LayoutTreeNode> },
  "rules": [
    { "ruleId": "Regra_001", "sourceElementGuid": "FLD_...", "targetElementGuid": "TAG_..." }
  ]
}

LayoutTreeNode {
  "elementGuid": "TAG_...",       // GUID estável, casa com sourceRefs/targetRefs
  "name": "enderDest",
  "kind": "group" | "element" | "attribute",
  "cardinality": { "min": 0, "max": 999 },   // de ParentOccurrenceVO; null para atributo/folha sem ocorrência
  "children": [ <LayoutTreeNode>, ... ]
}
```

- Nó recursivo único cobre grupo (`GroupTagElementVO`)/elemento
  (`TagElementVO`)/atributo (`AttributeElementVO`); wrappers `Choice`/
  `Sequence` continuam invisíveis no contrato (mesma convenção já usada no
  XPath), repassando cardinalidade/filhos ao pai como hoje.
- `rules[]` reaproveita o mesmo par de GUIDs que `LinkMappingItemVO`
  (`InputLayoutGuid`/`TargetLayoutGuid`) usa nativamente no Connect Us — não é
  invenção, é o contrato real do formato.

## Escopo — confirma que não colide com #417

Sim, integralmente. O endpoint é parametrizado por `mappingId`/`mapperGuid`
específico, resolvido a partir de um mapeador **já identificado** pelo
chamador. Não há descoberta, não há listagem por workspace, não há
resolução "qual mapper corresponde a este workspace" — isso é
inteiramente o problema do #417. Os dois podem evoluir em paralelo sem
dependência: #417 resolve "qual `mappingId` usar"; #425 resolve "dado um
`mappingId`, mostre as duas árvores".

## Consequências

- **Positivo:** reaproveita ~90% de lógica já testada e madura
  (`GuidXPathCatalog`), fecha o "LIMITE HONESTO" documentado sobre resolução
  de XPath por convenção de folha, e destrava a UI do React sem depender de
  nenhum artefato do Connect Us em runtime.
- **Atenção — boundary:** `GuidXPathCatalog` vive hoje em
  `ai/XslSynth.Contracts`, um projeto que a arquitetura já tratou como
  standalone/deliberadamente isolado (ver memória `xslsynth-trilha-a-overlap`).
  Movê-lo ou referenciá-lo da API principal precisa de decisão explícita de
  `@lp-backend-dev`/`@lp-devops` sobre se vira (a) projeto compartilhado
  referenciado por ambos, ou (b) código duplicado com testes de paridade. Não
  recomendo duplicar — prefira extrair para um projeto `Shared`/`Contracts`
  neutro referenciado pelos dois lados.
- **Atenção — resiliência:** hoje `GuidXPathCatalog.Load` degrada para
  catálogo vazio se o arquivo estiver ausente/ilegível (padrão correto). O
  endpoint novo deve preservar esse comportamento: se o LayoutVO não puder
  ser localizado/lido, responder com `404`/corpo com árvore vazia e log de
  aviso — nunca 500 derrubando o request.
- **Atenção — performance:** parsing de LayoutVO é feito por leitura de
  arquivo/BLOB a cada chamada; se os layouts forem grandes ou o endpoint for
  chamado com frequência, avaliar cache (mesmo padrão de `CachedLayoutService`)
  — não bloqueante para a primeira versão, mas sinalizar a `@lp-backend-dev`.
- **Segurança:** nenhum segredo em jogo; é leitura de estrutura de layout já
  tratada como dado interno legível pela API (mesmo grau de sensibilidade do
  que já é exposto por `MappingExplanationController`).

## Estimativa de esforço e próximo passo

**Esforço: pequeno–médio (S/M), baixo risco.** A parte difícil (parsing de
hierarquia GUID-based) já está pronta e testada. O trabalho novo é: (1) duas
propriedades a mais no parser (~1h), (2) resolver o `layoutGuid`→arquivo via
serviço existente em vez de path local (meio dia, depende de como
`LayoutDatabaseService` expõe isso hoje — checar se já existe um método ou é
preciso um novo), (3) decisão de projeto/referência entre `ai/XslSynth.Contracts`
e a API principal (conversa rápida, não é trabalho de código), (4) o
controller/endpoint novo em si (meio dia, segue o padrão de
`MappingExplanationController`).

**Próximo passo para `@lp-backend-dev` (Dex):**
1. Confirmar com `@lp-devops`/decisão de projeto se `GuidXPathCatalog` migra
   para um projeto compartilhado ou se a API ganha uma cópia com testes de
   paridade (recomendação: compartilhado).
2. Adicionar `MinOccurs`/`MaxOccurs` ao `GuidXPathEntry` e ao parser,
   lendo `MinimalOccurrence`/`MaximumOccurrence` de nós `ParentOccurrenceVO`
   (via `xsi:type` = `GroupTagElementVO` no XML exportado).
3. Implementar `GET /api/workspaces/{workspaceId}/mappings/{mappingId}/layout-tree`
   seguindo o contrato acima, reaproveitando a resolução de membership/
   workspace já existente em `MappingExplanationController` como referência
   de padrão (auth, 404 fail-closed, tratamento de erro 503 em falha de
   infra).
4. Validar contra o par real de exemplo já disponível em
   `.claude/tmp/exemplos/` (gabarito mencionado em memória de sessões
   anteriores) antes de entregar para o React consumir.
