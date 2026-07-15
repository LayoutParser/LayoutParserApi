# Arquitetura — Generalização Multi-Cliente do Motor de Parsing/Transformação

> **Autor:** @lp-architect (Aria) · **Status:** Proposta (design) · **Data:** 2026-07-15
> **Escopo:** desenhar como o motor determinístico (Excel→TCL→XSL, `ai/XslSynth`) e o próprio parsing/validação
> do núcleo da API deixam de assumir **implicitamente "é sempre FIAT, é sempre 600 posições"**.
> **Gatilho:** correção de rumo pedida pelo usuário — ver §1.
> **Relacionado:** [`poc-excel-generator.md`](poc-excel-generator.md) (a PoC que expôs o problema) ·
> [`ia-xslt-synthesis.md`](ia-xslt-synthesis.md) ·
> [`multi-session-execution-plan.md`](multi-session-execution-plan.md) (divisão B1/B2 deste doc para a Trilha B).

---

## 1. Gatilho — o que o usuário corrigiu

Duas correções de rumo, em uma única mensagem (2026-07-15):

1. **"Isso com mais do que somente os layouts que estamos passando da FIAT."** — o pipeline determinístico
   (`ai/XslSynth.Excel`) e o roadmap até aqui foram construídos e validados **inteiramente sobre o layout FIAT**
   (a planilha `Layout_NF-e_Mensageria_Envio_Receb_v10.xlsx`, linhas de exatas 600 posições). Precisa generalizar.
2. **Correção de diagnóstico:** eu havia registrado, em `poc-excel-generator.md` §9.4/§11 (item 1), que "campos
   que dependem do input saem vazios" no runner era um **bug de formato** (layout `TextDelimited` alimentado como
   largura-fixa). O usuário corrigiu: **campo de saída vazio, por si só, não é evidência de bug** — depende do
   documento do cliente e das **regras de negócio fiscais** (ex.: item X só leva `retTrib` se tiver CFOP/NCM Y).
   Não existe "linha obrigatória → saída obrigatória" como regra universal.

A segunda correção não é um detalhe — ela invalida a forma como eu estava enquadrando "pendência" nesta trilha,
e por isso §4.4 formaliza a distinção certa antes de qualquer plano de generalização.

---

## 2. Achado-chave: o problema já existe no núcleo da plataforma — não é invenção do PoC

Antes de desenhar qualquer generalização, levantei onde a suposição "linha = 600 posições" está codificada
hoje. Resultado: **não é um hardcode isolado do PoC.** É uma dívida técnica pré-existente, espalhada por todo
o núcleo (`Services/`), que o PoC simplesmente **herdou e replicou** por ter sido construído rápido.

### 2.1 O contrato certo já existe — só não é usado consistentemente

`Models/Entities/Layout.cs` já tem `LimitOfCaracters` (populado do XML do layout via `XmlLayoutLoader`,
`LayoutParserService`, `LayoutValidationService` — a mesma fonte-da-verdade Connect Us que o resto da API usa).
`Services/Generation/TxtGenerator/Models/{RecordLayout,FileLayout}.cs` também já modelam `TotalLength`/
`LimitOfCharacters` **por registro/arquivo**, não como constante global. **O modelo de dados já é genérico.**

### 2.2 Mas o literal `600` ignora esse contrato em pelo menos 20 pontos

| Arquivo | Linhas (aprox.) | Papel |
|---|---|---|
| `Services/Implementations/LayoutParserService .cs` | 209, 240, 1110, 1115, 1183, 1987, 1990, 2104, 2328, 2345, 2672, 2724 | parsing/validação principal — 12 ocorrências |
| `Services/Parsing/Implementations/LayoutValidator.cs` | 99, 153 | `isValid = hasChildren ? totalLength <= 600 : totalLength == 600` |
| `Services/Parsing/Implementations/LayoutDetector.cs` | 91 | contagem de linhas lógicas |
| `Services/Transformation/MapperTransformationService.cs` | 551 | `const int mqseriesLineLength = 600` |
| `Services/Validation/DocumentValidationService.cs` | 76, 105 | limites de posição |
| `Services/Validation/DocumentMLValidationService.cs` | 250, 265 | features de ML (`lineCount`) |
| `Models/Validation/{DocumentLineError,LineValidationError}.cs` | 10, 9 | `ExpectedLength { get; set; } = 600` (default) |
| `ai/XslSynth/Excel/{RootTreeBuilder,NfeGabaritoMiner}.cs` | `LineLength = 600` | **o PoC herdou o mesmo padrão** |

### 2.3 O achado mais importante: já existe uma 2ª família de layout na base — e ela é tratada por allowlist, não por generalização

`Models/Configuration/LayoutLineSizeConfiguration.cs` é um `HashSet<string>` **hardcoded** de LayoutGuids:

```
Layouts600Chars  = { 5 GUIDs }   // FIAT
Layouts2500Chars = { 4 GUIDs }   // outro(s) cliente(s)/documento(s) — JÁ EM PRODUÇÃO
```

`ParseController.cs:125` e `LayoutValidationService.cs:83` usam essa lista como **allowlist**: só os **9 GUIDs
cadastrados manualmente** recebem validação estrutural de linha. Qualquer outro layout cadastrado no Connect
Us hoje passa pela API **sem nenhuma validação de tamanho de linha** — não porque a regra não se aplique a ele,
mas porque **ninguém adicionou o GUID na lista**. `ValidateAllLayoutsAsync` (linha 154) itera exatamente
sobre essa allowlist: layouts fora dela são estruturalmente invisíveis para o validador.

**Conclusão:** a plataforma **já opera com múltiplos clientes/formatos** (600 e 2500 chars convivem hoje), mas
a generalização é feita por **patch manual de GUID**, não pelo contrato de dados que já existe
(`Layout.LimitOfCaracters`). O pedido do usuário não é "suportar um 2º formato no futuro" — é **destravar uma
generalização que os dados já suportam e o código não usa**.

---

## 3. Dois conceitos que a mensagem do usuário separa (e que o código de hoje mistura)

### 3.1 Estrutura física da linha — invariante do layout, validada no LOAD

"Cada linha tem exatas 600 posições, nem mais nem menos; há um cálculo que verifica isso quando o layout é
carregado do Connect Us." Isto é uma invariante **por layout** (pode ser 600, pode ser 2500, pode ser outra
coisa amanhã), não uma constante global do sistema. Já existe até o mecanismo certo de validação
(`LayoutValidator.ValidateLineLayoutWithResult`, `hasChildren ? totalLength <= 600 : totalLength == 600`) — só
precisa deixar de ler `600` do texto do código e passar a ler `layout.LimitOfCaracters`.

**É correto** um documento reprovar aqui: se a linha física não bate no tamanho esperado do layout, é erro
estrutural do documento do cliente (ou do parser), ponto final. Isso continua sendo *gate rígido*.

### 3.2 Presença condicional de campo na saída — regra de negócio fiscal, nunca estrutural

"Campo que depende do input sair vazio é normal — depende do documento do cliente e das regras fiscais
(item X precisa de CFOP Y e NCM H)." Isto **não é** uma invariante de layout — é uma decisão de negócio que só
o **mapeador real** (as regras DSL do Sysmiddle) sabe resolver, documento a documento. O projeto já tem a prova
disso funcionando: `MapperEmissionGuide` (`ai/XslSynth/Excel/MapperEmissionGuide.cs`) foi construído
exatamente para isto — ele lê as 98 regras do MapperVO real e descobre que `retTrib`/`cobr/fat/vLiq` só devem
ser emitidos quando o valor é `!= 0`, e aplica essa máscara ao gerador. **É o padrão certo — só foi aplicado a
8 campos (os que apareceram no único gabarito disponível), não generalizado como princípio.**

**É incorreto** tratar "campo ausente na saída" como falha de gate a menos que a **regra de negócio real**
(mapeador, não a spec Excel nem o XSD) diga que ele deveria estar presente para aquele documento específico.

### 3.3 Consequência prática para o diagnóstico do runner (correção de `poc-excel-generator.md` §11 item 1)

O item "bug do parser TextDelimited vs largura-fixa" continua válido **como hipótese de diagnóstico da
estrutura física** (§3.1: se o layout `LAY_ad4fb6f4` é realmente `TextDelimited` no Connect Us, alimentá-lo
como largura-fixa 600 É um erro estrutural, correto investigar). Mas **não é correto** interpretar "os campos
dependentes de input saíram vazios" como *sintoma desse bug* — pode ser simplesmente o documento de teste não
exercitando aquelas regras fiscais (§3.2). São dois problemas independentes; só o primeiro é um "bug" no
sentido estrutural. Vou corrigir a redação da pendência #1 no documento da PoC para refletir isso.

---

## 4. Arquitetura proposta

### 4.1 Fonte única de verdade estrutural: `Layout.LimitOfCaracters`

- Eliminar o literal `600` dos ~20 pontos listados em §2.2, substituindo por leitura de
  `layout.LimitOfCaracters` (ou o `RecordLayout.TotalLength`/`FileLayout.LimitOfCharacters` já existentes no
  módulo `Generation`, que é o lugar onde o modelo já está certo — usar como referência).
- **Não é reescrever a lógica de validação** — é trocar a constante pela leitura do dado que já existe e já
  chega até esses pontos (o `Layout`/`FileLayout` já está em escopo nesses métodos).
- No `ai/XslSynth.Excel`: `RootTreeBuilder` e `NfeGabaritoMiner` passam a receber o tamanho de linha como
  parâmetro (vindo do `Layout.LimitOfCaracters` real do layout sendo processado), não mais `const int
  LineLength = 600`.

### 4.2 `LayoutLineSizeConfiguration` NÃO é redundante — é uma correção necessária (G0 concluído, 2026-07-15)

**Investigação G0 concluída.** Verificado contra o dump real do layout `LAY_e339073e-32d1-492e-ae8a-dcf6337b21a1`
(um dos 5 GUIDs FIAT/600 da allowlist, XML **decriptado**, a mesma fonte que `LayoutDatabaseService.cs` trata
como autoritativa — "LayoutGuid do XML é sempre priorizado sobre o do banco"): o campo real é
`<LimitOfCaracters>0</LimitOfCaracters>`. **Zero, não 600.** E não é acidente isolado: `LayoutDatabaseService.cs`
linhas 316–417 já documentam e tratam o caso de `LayoutGuid` vir zerado do banco/XML como algo **esperado e
recorrente** ("Se o LayoutGuid do banco estiver zerado, será preenchido com o valor do XML"). Dado sujo em
metadados de layout é um problema estrutural conhecido do Connect Us, não uma hipótese.

**Conclusão — revisão da recomendação de §4.1:** `LayoutLineSizeConfiguration` **não deve ser deletada.** Ela é
a correção manual para exatamente esse buraco de dado. A arquitetura correta é **mesclar as duas fontes**, não
substituir uma pela outra:

```
tamanho de linha efetivo =
    layout.LimitOfCaracters,  se > 0            (fonte primária — cresce sozinha p/ layouts novos bem cadastrados)
    senão LayoutLineSizeConfiguration.GetLineSizeForLayout(guid)   (override manual p/ dado sabidamente sujo)
    senão null → SEM validação estrutural (comportamento atual p/ desconhecido, mantém-se)
```

Isso resolve o problema original do usuário (layouts fora da allowlist hoje não são validados) **sem
descartar** a correção que já existe para os 9 GUIDs conhecidos-com-dado-ruim. `@lp-backend-dev` implementa
essa função de resolução única (`ILineLengthResolver` ou método estático equivalente) e substitui os ~20
literais `600` por uma chamada a ela — não por leitura direta de `LimitOfCaracters`.

### 4.3 Camada de ingestão de spec com duas fontes convergentes (Ports & Adapters)

O `SpecModel` (`ai/XslSynth/Excel/SpecModel.cs`) hoje só nasce de UMA fonte: a planilha específica que a FIAT
forneceu (`ExcelSpecParser`, acoplado à aba `Layout-Emissão-XML-4.00` e ao esquema de colunas dela). Isso é
**um atalho válido para FIAT** (só funciona porque ela tem uma spec machine-readable), mas não generaliza —
outros clientes não necessariamente têm (ou vão querer manter) uma planilha assim.

O roadmap já continha o caminho certo para o caso geral, ainda não iniciado (`poc-excel-generator.md` §5, P0
"Fechar o catálogo GUID→XPath"): extrair o equivalente ao `SpecModel` **direto do layout/mapeador do Connect
Us**, via o `LowCodeRunner` — que agora funciona (destravado em 14/07). Proponho formalizar isso como
arquitetura de dois adaptadores que convergem para o **mesmo contrato**:

```
 Fonte A: Excel spec (client-provided)     Fonte B: Connect Us (via LowCodeRunner)
      ExcelSpecParser                           LayoutSpecExtractor (novo)
              │                                          │
              └──────────────► SpecModel ◄───────────────┘
                                    │
                     TclGenerator · XslGenerator · NfeLeiauteCatalog
                          (não mudam — já são genéricos o suficiente)
```

- **Fonte A (Excel)** continua existindo como atalho/validação cruzada — útil quando o cliente fornece.
- **Fonte B (Connect Us/runner)** é a via **client-agnostic real**: funciona para qualquer layout já
  cadastrado, sem depender de planilha nenhuma. É estrategicamente mais importante — deve ser priorizada como
  o P0 do roadmap, não como "trabalho futuro".
- As duas fontes descrevendo o mesmo layout (quando ambas existirem, como no caso FIAT) **se validam
  mutuamente** — mesma ideia que já está registrada em `poc-excel-generator.md` §5 para os catálogos Excel×GUID.

### 4.4 Generalizar o `MapperEmissionGuide` como motor universal de condicionalidade

Hoje o `MapperEmissionGuide` resolve 8 campos específicos (os que apareceram como SOBRA no único gabarito
FIAT). Para generalizar por cliente/documento, ele precisa deixar de ser "o script que fecha os 8 SOBRAs
conhecidos" e virar **o mecanismo padrão de decisão de presença** para qualquer campo condicional, de qualquer
mapeador: toda vez que o `XslGenerator` decidir se emite um campo, a pergunta correta é **"o mapeador real
emitiria isto para este documento?"**, respondida pelas regras DSL reais — nunca "a spec/XSD marca isso como
obrigatório?". Isso é consistente com §3.2: a fonte de verdade para condicionalidade é sempre o mapeador, nunca
uma lista estática.

**Implicação para o gate de QA (`CanonicalDiffer`/set-diff):** o gate `diff==0` continua correto **contra um
gabarito conhecido**, mas ao generalizar para novos documentos/clientes sem gabarito prévio, `@lp-qa` precisa
de um segundo tipo de gate: não "bate com o gabarito X" (que só existe para o par que já temos), mas "todo
campo emitido tem uma regra do mapeador que o justifica, e todo campo omitido tem uma regra do mapeador que
justifica a omissão" — rastreabilidade da decisão, não comparação posicional.

---

## 5. Plano de trabalho (fases)

| Fase | Entregável | Dono | Depende de |
|---|---|---|---|
| G0 | ✅ **Concluído (2026-07-15, arquiteto inline)** — Investigar `LayoutLineSizeConfiguration` vs `Layout.LimitOfCaracters`. Achado: `LimitOfCaracters=0` real para ao menos 1 dos 9 GUIDs (evidência em XML decriptado); a allowlist compensa dado sujo conhecido, não é redundante (§4.2) | Aria | nada |
| G1 | ✅ **Concluído (2026-07-15, Trilha B — commit `d096364`, merged PR #3, auditado pela Aria):** `Models/Configuration/LineLengthResolver.cs` (precedência `LimitOfCaracters>0` → allowlist → null; `LegacyDefaultLineLength=600` é o único literal remanescente); 3 gates de allowlist migrados (`ParseController`, `MonitoringController`, `LayoutValidationService`); fallback de parse `? limit : 600` corrigido para `? limit : 0` (um 600 fabricado venceria a allowlist e quebraria os layouts de 2500); literais residuais só em `Services/Generation/**` (fora do escopo, follow-up registrado) | Dex | G0 ✅ |
| G2 | ✅ **Concluído (2026-07-15, Trilha B — mesmo commit):** `RootTreeBuilder.Build`/`NfeGabarito.Load` com parâmetro `lineLength` (default 600 p/ CLI; guard `ArgumentOutOfRangeException` p/ ≤0); valor real virá de `Layout.LimitOfCaracters` via `LineLengthResolver` na integração C1 | Dex | nada |
| G3 | `LayoutSpecExtractor` — 2º adaptador de ingestão de spec via Connect Us/LowCodeRunner (Fonte B, §4.3) | Lia | runner em modo lote (`poc-excel-generator.md` §11 item 2) |
| G4 | Generalizar `MapperEmissionGuide` de "8 campos conhecidos" para motor de condicionalidade genérico (§4.4) | Lia | G3 (precisa de mais mapeadores reais para generalizar o padrão) |
| G5 | Corrigir a redação da pendência #1 em `poc-excel-generator.md` §11 (feito nesta sessão, ver §7 abaixo) | Aria | — |

**G0–G2 não dependem de um segundo cliente real** — são generalização do que já existe (o layout de 2500 chars
já em produção é suficiente para provar e testar). **G3–G4 se beneficiam de mais gabaritos** (via runner em
lote, já no roadmap da PoC).

---

## 6. Riscos e perguntas em aberto

| Risco/pergunta | Impacto | Mitigação |
|---|---|---|
| `LimitOfCaracters` pode estar zerado/errado para layouts legados no banco (evidência: exemplo em `TestController.cs` com valor `0`) | G1 quebraria validação de layouts que hoje "funcionam" só por estarem fora da allowlist | G0 investiga ANTES de G1; se sujo, corrigir dado no Connect Us antes de generalizar o código |
| Layouts genuinamente não-fixed-width (delimitados, IDOC) — `FileLayout.LayoutType` já modela `"TextPositional"`, `"Xml"`, `"IDOC"` | §3.1 (validação de tamanho exato) só se aplica a `TextPositional`; não travar os outros tipos numa checagem que não faz sentido para eles | escopo desta generalização = layouts posicionais (`TextPositional`); os outros tipos já têm caminho próprio, não tocar |
| Sem um segundo cliente real (fora do 2500-char já existente) para validar a generalização ponta-a-ponta do `ai/XslSynth.Excel` | G3/G4 ficam parcialmente especulativos até haver um 2º mapeador real disponível | priorizar G0–G2 (não especulativos, geram valor imediato); G3 usa o layout de 2500 chars já existente como 2º caso real, não hipotético |

---

## 7. O que já foi corrigido nesta sessão

`poc-excel-generator.md` §11, item 1 tinha a redação: *"Bug do parser TextDelimited vs largura-fixa... campos
que dependem do input saem vazios"*. Ver §3.3 acima — a redação será ajustada para separar (a) a investigação
estrutural legítima (o layout é mesmo `TextDelimited`? confirmar contra o Connect Us) de (b) a explicação
correta para campos vazios (regra de negócio fiscal, não falha).

---

## 8. Divisão de trabalho

- **@lp-architect (Aria):** este design; decisão de arquitetura sobre promoção PoC→produção (fica pendente até G3/G4).
- **@lp-backend-dev (Dex):** G0, G1, G2 — mudanças no núcleo (`Services/`) e no `ai/XslSynth.Excel`.
- **@lp-parser-llm (Lia):** G3 (`LayoutSpecExtractor`), G4 (generalização do `MapperEmissionGuide`).
- **@lp-qa (Quinn):** redesenhar o gate de "rastreabilidade da decisão" descrito em §4.4, para quando não houver gabarito prévio.
- **@lp-devops (Gage):** nenhuma ação nesta frente (sem mudança de CI/secrets/push).

---

## 9. Escopo permanente — o que esta aplicação NÃO faz (confirmado pelo usuário, 2026-07-15)

**Assinatura digital (`<Signature>`) NÃO é, e nunca será, responsabilidade desta aplicação.** Correção de
enquadramento: eu havia registrado isso como "fora do escopo da PoC" (implicando que seria retomado numa fase
futura de produção). O usuário corrigiu — é fora de escopo **permanente e definitivo**: quem assina o
documento é o **e-forms** (outro sistema do ecossistema, fora deste repositório). O papel desta aplicação,
hoje e no roadmap inteiro, é:

1. Geração determinística de **TCL** (parser posicional TXT→ROOT) e **XSL/XSLT** (ROOT→NF-e) a partir do
   layout/mapeador do cliente.
2. **Validação posicional** do documento TXT, campo a campo, contra o layout esperado (§3.1 desta arquitetura).

Isso é um limite de escopo, não uma pendência — **não deve reaparecer em nenhuma lista de "o que falta"**
daqui em diante. Removido de `poc-excel-generator.md` §11 item 6 (marcado como fechado) e da nota em §7.9.
