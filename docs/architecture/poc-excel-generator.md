# Arquitetura — PoC do Gerador Determinístico via Excel (TCL + XSL) + Roadmap

> **Autor:** @lp-architect (Aria) · **Status:** Proposta (design) · **Data:** 2026-07-10
> **Escopo:** desenhar o desenvolvimento. A implementação é de `@lp-backend-dev` (Dex) e `@lp-parser-llm` (Lia); QA por `@lp-qa` (Quinn).
> **Relacionado:** [`ia-xslt-synthesis.md`](ia-xslt-synthesis.md) · memória `server-assets-inventory`, `sysmiddle-runtime-e-sintese`.

---

## 1. Contexto e decisão

O dump do servidor revelou a **fonte-da-verdade** de onde o low-code Sysmiddle foi construído: a planilha
`Layout_NF-e_Mensageria_Envio_Receb_v10.xlsx`. Ela é **machine-parseável** e descreve, campo a campo, a
posição no arquivo TXT **e** o campo de destino na NF-e. Isso habilita um **gerador determinístico** que não
depende (a) de descriptografar o mapeador nem (b) de gabarito de runtime — os dois bloqueios atuais.

Esclarecimento de papéis (confirmado pelo usuário e pelas amostras do corpus G2KA):

```
  TXT posicional  ──(TCL)──►  XML intermediário "ROOT"  ──(XSL)──►  NF-e final (SEFAZ)
   (600 col/linha)   parser        <ROOT><Bloco…>            transform    <enviNFe>…
```

- **TCL** = *parser de layout posicional* → transforma o documento em uma árvore XML `ROOT` (NÃO é transformação).
  Formato: `<MAP><LINE identifier name><FIELD name length="…"/></LINE></MAP>`.
- **XSL** = *transformação* → converte a árvore `ROOT` no XML NF-e final, campo a campo.

**Decisão:** construir a PoC **dentro de `ai/XslSynth`** (reusa `XsdValidator`, `Xslt`, `XsltFragment`,
`DeterministicXslTranspiler.GetOrCreateChildPath`), num novo namespace `XslSynth.Excel`, acionada por
`dotnet run -- --excel <xlsx>`. Zero dependência nova obrigatória (ver §3.1).

---

## 2. O que a planilha realmente contém (schema real)

Aba **`Layout-Emissão-XML-4.00`** (a de envio; há uma 2ª `Layout-Receb-XML-4.00_REF` para o retorno).

Cabeçalho (linhas 5 e 14, repetido por seção) → colunas:

| Col | Cabeçalho | Papel |
|-----|-----------|-------|
| A | **Item** | nº sequencial do campo dentro do bloco |
| B | **Descrição** | nome do BLOCO quando é linha de cabeçalho (`Registro Header`, `Bloco-000`, `Bloco-001`…) |
| C | **Descrição** | nome do CAMPO (`Código da UF do Emitente`, `Número do Documento Fiscal`…) |
| D | **# XML** | **nº do campo no leiaute oficial NF-e** (`6`, `12`, `79a`, `81b`, `88f`…) ou `NA` (campo de controle, não vai pra NF-e) |
| E | **Inicio** | posição inicial (1-based) dentro da linha do bloco |
| F | **Fim** | posição final |
| G | **Tamanho** | comprimento |
| H | **Tipo** | `C`=char, `N`=numérico, `D`=data |
| I | **Decimais** | casas decimais (quando `N` monetário) |
| J | **Formato** | máscara (`AAAAMMDD`, `HHMMSSCCC`, `AAAA-MM-DD`, literais `"000000000"`…) |

Fatos estruturais decisivos:

1. **Cada `Bloco-NNN` é uma LINHA posicional** (`LINHA000`, `LINHA001`, `LINHA050`…). As posições **resetam** a cada
   bloco (Bloco-001 recomeça em `Inicio=10`). Isso casa 1:1 com o `I.LINHA050/Campo` da DSL do mapeador que já parseamos.
2. Linha fixa de **600 colunas** (o `Filler` sempre fecha em `Fim=600`). Posições 1-6 = "Tipo Registro", 7-9 = código do bloco.
3. **`# XML` é um índice, não um path.** Para gerar o XSL é preciso resolver `# XML (nº leiaute) → XPath NF-e`.
   Os sufixos de letra (`79a`, `88f`) são a forma como a SEFAZ insere campos entre versões — o resolvedor precisa
   aceitar IDs alfanuméricos.
4. A coluna **C (Descrição)** é semanticamente rica ("Número do Documento Fiscal" ≈ `nNF`) → serve de **fallback**
   quando o `# XML` for ambíguo/ausente.

---

## 3. Arquitetura da PoC

### 3.1 Componentes (fluxo de dados)

```
 xlsx ──►[1 ExcelSpecParser]──► SpecModel(blocos[], campos[])
                                   │
                    ┌──────────────┼───────────────────────────┐
                    ▼                                            ▼
          [2 TclGenerator]                          [4 XslGenerator]
             SpecModel                               SpecModel + NfeLeiauteCatalog
                │                                        │  (resolve # XML → XPath)
                ▼                                        ▼
          TCL <MAP> (parser)                       XSL (ROOT → NF-e)
                                                         │
                                              [5 XsdValidator] (reusa XslSynth)
                                               valida vs leiauteNFe_v4.00.xsd
                                                         │
                                            Relatório: cobertura + compila + XSD-válido

          [3 NfeLeiauteCatalog]  #XML(nº) → { xpath, tipo, ocorrência }
             fonte: XSD leiauteNFe_v4.00 (ordem/estrutura) + tabela de nº do leiaute (NT/legenda)
```

| # | Módulo | Entrada → Saída | Determinístico? |
|---|--------|-----------------|-----------------|
| 1 | **ExcelSpecParser** | `.xlsx` → `SpecModel` | Sim |
| 2 | **TclGenerator** | `SpecModel` → `TCL <MAP>` | **100% determinístico** |
| 3 | **NfeLeiauteCatalog** | XSD + tabela nº → `Map<#XML, XPath>` | Determinístico (com fallback semântico) |
| 4 | **XslGenerator** | `SpecModel` + catálogo → `XSL` | Determinístico p/ campos resolvidos |
| 5 | **XsdValidator** *(já existe)* | `XSL`+entrada → válido? | Sim |

**Leitura do xlsx em .NET:** não há reader nativo. Recomendação: `DocumentFormat.OpenXml` (pacote oficial MS, leve).
Alternativa **zero-dependência** (preferida pelo ethos enxuto do projeto): `System.IO.Compression.ZipArchive` +
`System.Xml.Linq` sobre `xl/worksheets/sheetN.xml` + `xl/sharedStrings.xml` — o parse já foi provado em protótipo
(PoC de reconhecimento). Decidir na 1ª issue; ambos são triviais.

### 3.2 Modelo de dados (contrato de design)

```csharp
// Namespace XslSynth.Excel — contratos (implementação delegada)
public sealed record SpecField(
    string Bloco,        // "Bloco-001"  → LINHA001
    int    Item,         // nº do campo no bloco
    string? FieldName,   // col C: "Número do Documento Fiscal"
    string? XmlRef,      // col D: "12" | "79a" | null (quando "NA")
    int    Inicio,       // col E (1-based, relativo à linha do bloco)
    int    Fim,          // col F
    int    Tamanho,      // col G
    char   Tipo,         // 'C' | 'N' | 'D'
    int?   Decimais,     // col I
    string? Formato      // col J
);

public sealed record SpecBlock(string Name, int LineCode, IReadOnlyList<SpecField> Fields); // LINHANNN
public sealed record SpecModel(string SheetName, IReadOnlyList<SpecBlock> Blocks);

// Catálogo do leiaute NF-e
public sealed record LeiauteEntry(string XmlRef, string XPath, string Tipo, string Occurs);
public interface INfeLeiauteCatalog { bool TryResolve(string xmlRef, out LeiauteEntry e); }
```

### 3.3 TclGenerator (determinístico puro)

Um `SpecBlock` → um `<LINE>`; cada `SpecField` → um `<FIELD name length>`. O `Tamanho` (col G) alimenta o `length`.
O `name` vem de um *slug* do `FieldName` (col C) — reaproveitar a normalização de NCName já existente no projeto.

```
<MAP>
  <LINE identifier="A" name="LINHA000"> <FIELD name="controleVersaoArquivo" length="3"/> … </LINE>
  <LINE identifier="B" name="LINHA001"> <FIELD name="codigoUfEmitente" length="2"/> … </LINE>
</MAP>
```

Saída = a árvore `ROOT` que o XSL consumirá (`ROOT/LINHA001/codigoUfEmitente`). **Não precisa de catálogo nem LLM.**

### 3.4 NfeLeiauteCatalog + XslGenerator (o núcleo)

Estratégia de resolução `# XML → XPath` em camadas (mais barata/segura primeiro):

1. **Catálogo por nº do leiaute** (determinístico): tabela `#XML → XPath` derivada do **leiaute oficial**.
   Fonte primária = `leiauteNFe_v4.00.xsd` (estrutura/ordem dos elementos) combinada com a numeração do leiaute
   (a "Nº" das tabelas da NT). *Construção do catálogo é uma sub-tarefa (§5).*
2. **Fallback semântico por Descrição** (col C): quando o `#XML` faltar/ambíguo, casar o texto da Descrição com a
   annotation/nome do elemento no XSD (dicionário + heurística; LLM opcional só aqui).
3. **Não resolvido** → emitir comentário honesto no XSL (como o `DslBlockInterpreter` já faz) e contabilizar como gap.

O `XslGenerator` então, para cada campo com destino resolvido, emite (reusando `Xslt`/`XsltFragment`):

```xml
<cUF><xsl:value-of select="ROOT/LINHA001/codigoUfEmitente"/></cUF>
<!-- Formato 'N' com decimais → format-number; 'D' AAAA-MM-DD → tradução de máscara -->
```

Formatação por `Tipo`/`Formato`/`Decimais`: `N`+decimais → `format-number(...,'0.00')`; `D` → conversão de máscara
(`AAAAMMDD` → `AAAA-MM-DD`) via `substring/concat`; literais (`"000000000"`) → `xsl:text`.

### 3.5 Verificação (sem gabarito)

Reusar o que o XslSynth já tem:
- **Compila?** `XslCompiledTransform.Load` (via `CoverageValidator`).
- **XSD-válido?** aplicar o XSL a uma entrada de exemplo (temos inputs reais em `Examples/`) e validar a saída
  contra `leiauteNFe_v4.00.xsd` com o `XsdValidator`.
- **Cobertura:** % de campos `#XML != NA` que viraram nó no XSL; % de blocos virando `<LINE>` no TCL.
- Quando **houver gabarito** (P0-catálogo/low-runner ou captura de produção) → fechar o loop `diff==0`.

---

## 4. Plano incremental da PoC (entregáveis + quality gates)

> **Estado (2026-07-10):** ✅ **PoC-0 + PoC-1 concluídas** (Dex). Build verde; `dotnet run -- --excel <xlsx>` roda.
> Números reais da planilha: **73 blocos** (1 HEADER + 71 LINHA000..098 + 1 TRAILER), **1042 campos** (712 com `#XML != NA`).
> TCL `<MAP>` gerado (`.claude/tmp/export/generated.tcl`, bem-formado, 0 FIELD duplicado). Cobertura de linha: 65/73 em 590–601;
> **8 blocos com grupos repetidos** (soma >601: LINHA001,022,038,039,048,057,073,098) = maior risco de domínio (§6).
> Arquivos: `ai/XslSynth/Excel/{SpecModel,ExcelSpecParser,TclGenerator}.cs` + `--excel` no `Program.cs` + `DocumentFormat.OpenXml 3.3.0`.
> ⚠️ Dex adicionou `ai/XslSynth/nuget.config` (contorna 401 do feed privado `tfs.ndd.tech`) → **revisão @lp-devops**.

| Fase | Entregável | Gate (Quinn) |
|------|-----------|--------------|
| PoC-0 ✅ | `ExcelSpecParser` → `SpecModel` (aba Emissão) + dump de conferência | ✅ 73 blocos / 1042 campos batem com a planilha |
| PoC-1 ✅ | `TclGenerator` → `TCL <MAP>` para NFe 4.00 | ✅ TCL bem-formado; 1042 FIELD; validado 10/10 contra o par gabarito |
| PoC-2 ✅ | `NfeLeiauteCatalog` (#XML→XPath, triangulação XsdOrder+Semantic+ValueAnchor) | ✅ 9/9 âncoras; **482/712 campos (67,7%)**; CSV com XPath+tipo XSD+occurs |

**Resultado PoC-2 (Lia + arquiteto, 2026-07-11):** modo `--catalog` no `Program.cs`; arquivos
`ai/XslSynth/Excel/{XsdLeiauteIndex,SemanticMatcher,NfeGabaritoMiner,NfeLeiauteCatalog}.cs`. Índice do XSD: 874 nós em
ordem de documento. Dos **628 refs distintos**: 55 Alta (2+ sinais), 383 Média (1 sinal), 190 não resolvidos —
concentrados em: **SubRef/MultiRef do choice ICMS** (111: precisam de seleção de variante por CST em runtime — PoC-3),
**LetraSufixo de grupos especializados** (49: veículos/ANVISA/ANP — ausentes do gabarito, sem ValueAnchor),
MocId RTC (7), extensão proprietária FIAT/ANFAVEA (2). Catálogo: `.claude/tmp/export/nfe-leiaute-catalog.csv`
(xpath;tipo;occurs;sinais;confiança). **Nota estratégica:** os campos presentes no gabarito real (venda simples, ICMS00)
caem nas regiões bem resolvidas → a PoC-3 pode mirar `diff==0` no par real antes de generalizar para os grupos raros.
| PoC-2 | `NfeLeiauteCatalog` (v1: só campos com `#XML` numérico direto) | ≥X% dos `#XML` resolvidos a XPath válido no XSD |
| PoC-3 | `XslGenerator` → XSL; **valida vs `leiauteNFe_v4.00.xsd`** | XSL compila; saída de exemplo valida no XSD (campos cobertos) |
| PoC-4 | Relatório de cobertura + honesto sobre gaps (fallback semântico) | métricas reproduzíveis; gaps listados |

`dotnet build` e `dotnet run -- --excel <xlsx>` verdes ao fim de cada fase. `ai/**` já está fora do build da API.

---

## 5. Roadmap detalhado (P0 → P2)

### P0 — Gerador determinístico via Excel  *(esta PoC, acima)*
- **Objetivo:** TCL + XSL para NFe 4.00 a partir da planilha, validados no XSD. Maior alavancagem, sem bloqueios.
- **Risco-chave:** construir o `NfeLeiauteCatalog` (#XML→XPath). *Mitigação:* começar pelos campos de `#XML` numérico
  simples do Grupo B/C; medir cobertura; fallback semântico para o resto.

### P0 — Fechar o catálogo GUID→XPath (destravar os 237 LinkMappings)
- **Objetivo:** obter `TargetGuid(TAG_/GRT_) → XPath NF-e` para os LinkMappings do XslSynth (o lado *input* já vem do `jsonGerado`).
- **Dependência dura:** **descriptografar Layouts e Mapeadores** → exige o **low-runner Sysmiddle funcionando**
  (hoje bloqueado na init de licença do host FiatMQ — ver `sysmiddle-runtime-e-sintese`). Sem isso, este P0 não anda.
- **Abordagem:** rodar o parser da API no **layout NF-e alvo** (mesmo mecanismo do `jsonGerado`, que provou `FLD→Nome→pos`)
  para extrair a árvore de `TAG_/GRT_ → path`. Combinar com o catálogo de leiaute do P0-Excel (as duas fontes convergem
  para o mesmo XPath NF-e → validação cruzada).
- **Nota de convergência:** o catálogo de leiaute do P0-Excel e o catálogo GUID→XPath descrevem o **mesmo destino** —
  um valida o outro.

### P1 — Indexar o corpus G2KA como RAG few-shot (reusar `RAG.ExamplesPath`)
- **Objetivo:** few-shot por `tipo × versão` (NFe/CTe/NFSe/MDFe) para as **9 regras difíceis** que o `DslBlockInterpreter`
  ainda deixa como stub (condição composta `&&`, `else`, aninhamento).
- **Abordagem:** índice leve (chave = tipo+versão+grupo) sobre `Examples/xsl`+`tcl`; recuperar exemplos análogos e
  injetar como few-shot no LLM local (Ollama). Reaproveitar a config `RAG.ExamplesPath` que o servidor já usa.

### P1 — Pipeline "NT nova" (o Excel também precisa ser novo)
- **Insight do usuário:** *quando sai uma NT nova, o próprio Excel-spec precisa ser atualizado.* O pipeline não só
  gera XSL/TCL — ele **regenera a spec**.
- **Fluxo proposto:**
  ```
  NT nova → diff(XSD_novo, XSD_antigo) = campos/tipos novos (ex.: Grupo UB IBS/CBS/IS, W03)
          → LLM extrai a semântica/regra do PDF por Grupo (§6/§7 da NT: leiaute + regras Nxxx)
          → delta no NfeLeiauteCatalog (novos #XML → XPath)
          → delta no Excel-spec (novas linhas: posição/tamanho/tipo/#XML)   ← "o excel novo"
          → regenera TCL + XSL do delta → valida no XSD novo
  ```
- **Backbone determinístico = o diff de XSD** (o que mudou é machine-readable); **LLM só na semântica** (PDF).
- **Insumo real disponível:** `xsd/PL_010b_NT2025_002_v1.30/*.xsd` + `pdf/…NT_2025.002…IBS_CBS_IS.pdf` (pypdf extrai; poppler não instalado).

### P2 — Detector de anomalia (documentos incorretos do cliente, meta 1.1)
- **Estado atual:** `MLData/DocumentPatterns` já **coleta features** (`totalLength, lineCount, hasHeader, numeric/alpha/spaceCharCount, ErrorsFound`)
  mas **não pontua** (`SuccessRate/Confidence=0`, `Suggestions=null`).
- **Abordagem:** treinar um **outlier detector** (não supervisionado, ex.: z-score/IsolationForest por `LayoutGuid`)
  sobre `TrainingSamples` "bons"; combinar com **validação estrutural** (o doc parseia no layout esperado? bate no XSD após transformar?).
  Saída: score de "provável incorreto" + campo/linha suspeitos.

### P2 — Segurança (rotação de segredos)
- `api/appsettings.json` do servidor tem **segredos em texto plano**: `Gemini.ApiKey`, `Database.Password`
  (Server `172.31.249.51`), `ElasticSearch.Password`. **Rotacionar** e mover para `user-secrets`/env (`Section__Key`).
  Ver `security` rule + memória `secrets-remediation`. **Ação do operador / `@lp-devops`.**

---

## 6. Riscos e decisões abertas

| Risco | Impacto | Mitigação |
|-------|---------|-----------|
| Catálogo `#XML→XPath` incompleto | XSL cobre só parte dos campos | começar pelo Grupo B/C; medir; fallback semântico; validar no XSD |
| Low-runner Sysmiddle segue bloqueado | P0-GUID não anda | P0-Excel é independente e segue; runner é pré-req só do P0-GUID |
| Máscaras de data/decimais variadas (col J) | formatação errada no XSL | tabela de máscaras conhecidas; teste por tipo |
| 2ª aba (Receb) e outras versões | escopo cresce | PoC fixa em Emissão/NFe 4.00; generalizar depois |

**Decisões fechadas (2026-07-10):**
- **Leitor xlsx = `DocumentFormat.OpenXml`** (robusto; alinhado à ideia futura de buscar NT direto do site da SEFAZ).
  `ZipArchive`+XLinq fica como alternativa zero-dep, não obrigatória.
- **Pipeline de transformação confirmado:** TCL (consome o TXT) → XSL (consome o XML `ROOT` gerado pelo TCL) → NF-e 4.00.
- **Excel = camada de *rules*** (a spec), não artefato de runtime.

- **Catálogo `#XML→XPath` = derivado do XSD** (decisão fechada 2026-07-11): o Excel é o *rules* do TXT de **entrada**;
  o **XSD é o *rules* do XML de saída**. Camadas: (1) ordem do documento no XSD calibrada por âncoras
  (`#6`=cUF, `#12`=nNF…), (2) fallback semântico Descrição↔`xs:documentation`, (3) **ancoragem por valor no gabarito**.

### Gabarito disponível (2026-07-11) — o loop diff==0 destravou

O usuário forneceu um **par real input→output** em `.claude/tmp/exemplos/`:
`txt input/QMWNFe1_….mq_series.txt` (59 linhas × 600 chars) → `xml output/…-env.xml` (`<enviNFe versao="4.00">` completo).
**Alvo do projeto: TCL+XSL devem reproduzir esse XML.** Nota: o output real inclui `<dadosAdic>` (extensão não-SEFAZ:
B2BDirectory, PrinterKey, bloco290…) — a validação XSD pura se aplica ao miolo `enviNFe`; a extensão é catalogada à parte.

**Validação da PoC-1 contra o par real (arquiteto, 2026-07-11):**
- Cobertura de blocos **100%** (56 códigos no input ⊂ 73 LINEs; "LINHA202" era artefato — ano 2025 no HEADER/TRAILER).
- **Spot-check de valores 10/10** ✅ (cUF=31, natOp, serie, nNF=150839, CNPJs emit/dest, logradouros, CEP) — a spec fatia
  o input exatamente nos valores do gabarito.
- Alinhamento estrutural: só **10 descontinuidades em 8 blocos** (HEADER, 030, 032, 035×3, 043, 083, 088, 098) — o
  `TclGenerator` deve usar **Inicio/Fim absolutos** (ou padding) nesses pontos, não só comprimento cumulativo.
- **Semântica de repetição descoberta:** blocos repetidos = **linhas físicas repetidas** (4× LINHA081 = continuação de
  `infCpl`, concatenadas no gabarito). O TCL plano funciona; o **XSL** é que precisa de `for-each`/concat (PoC-3).

**Nota de fidelidade do TCL:** o parser posicional precisa de **cobertura total da linha** (todo campo, inclusive
`Tipo Registro`, código do bloco e `Filler`, entram como `<FIELD>` para consumir os 600 chars). Só o XSL usa apenas os
campos com `#XML != NA`.

---

## 7. Spec da PoC-3 — `XslGenerator` (gate: diff==0 no par real)

> **Status:** em implementação (Dex, com Lia no domínio ICMS/CST) · handoff 2026-07-11.

### 7.1 Objetivo e gate

Gerar o **XSL** a partir de `SpecModel` + `NfeLeiauteCatalog`, aplicá-lo sobre a árvore `ROOT` construída do TXT real,
e comparar com o gabarito `…-env.xml`. **Gate primário: `diff==0` no subtree `<infNFe>`** (core SEFAZ) + **XSD como
oráculo** (`XsdValidator` existente). Gate secundário (Etapa B): estender ao `<dadosAdic>` (extensão proprietária).

### 7.2 Componentes novos (em `ai/XslSynth/Excel/`)

| Componente | Papel | Observação |
|---|---|---|
| `RootTreeBuilder` | TXT (59×600) → `XDocument` ROOT | Fatia pelo **Inicio/Fim absolutos do SpecModel** (autoritativo — cobre as 10 descontinuidades), não pelo comprimento cumulativo do TCL. O TCL permanece como artefato de export p/ a plataforma. Linhas repetidas → elementos repetidos em ordem (`<LINHA081/>…×4`). HEADER/TRAILER identificados por prefixo `HEADER`/seq `999999`, não por pos 7-9. |
| `XslGenerator` | `SpecModel`+catálogo → XSL | Só campos com resolução Alta/Média. Monta a árvore `enviNFe` pelos XPaths do catálogo (reusar `GetOrCreateChildPath`). Campo não resolvido → `<xsl:comment>` honesto (padrão do projeto). |
| modo `--generate` | pipeline completo + relatório | TXT→ROOT→XSL→saída→`CanonicalDiffer` vs gabarito + `XsdValidator`. Imprime nº de diffs por região e salva `generated.xsl`/`generated-output.xml` em `.claude/tmp/export/`. |

### 7.3 Regras de valor (descobertas no gabarito — normalização obrigatória)

1. **Zeros à esquerda**: `serie '006'→'6'`, `nNF '000150839'→'150839'` — strip em campos numéricos de identificação
   (tipos XSD `TSerie`, `TNF`…; usar o `tipo` do catálogo CSV).
2. **Decimais**: col `Decimais` da spec → `format-number` (`vProd 189.78` = 2 casas; `qCom 6.0000` = 4; `vUnCom
   31.6300000000` = 10). A col J (`Formato`) complementa.
3. **Datas**: `AAAA-MM-DD` já vem ISO no input; `dhEmi` composto com timezone (`2025-07-16T14:13:18-03:00`) — verificar
   se vem pronto do input ou se compõe data+hora+offset.
4. **Opcionais vazios são OMITIDOS** (o gabarito não tem elementos vazios) → envolver emissão em `xsl:if` de não-vazio
   (mesmo idioma do `DslBlockInterpreter`).
5. **`infCpl` = concat das LINHA081 repetidas**: trim de cada segmento e concatenação SEM separador
   (`…Guedes` + `PIS ST…` + `COFINS ST…`), via `xsl:for-each`.

### 7.4 Seleção de variante ICMS por CST (domínio — Lia)

O choice `ICMS00|ICMS10|…|ICMSSN…` é decidido **em runtime** pelo valor do CST/CSOSN do item:
`xsl:choose` sobre o campo CST → `00→ICMS00, 10→ICMS10, 20→ICMS20, 30→ICMS30, 40|41|50→ICMS40, 51→ICMS51, 60→ICMS60,
70→ICMS70, 90→ICMS90; CSOSN 101/102/201/202/500/900→ICMSSN*`. Os refs SubRef/MultiRef do catálogo (`#245.x`,
`#179|196|226|240`) resolvem **dentro** da variante escolhida. Gabarito exercita `ICMS00` (CST=00) — é o caminho da
Etapa A; as demais variantes ficam estruturadas mas só são exercitadas quando houver gabarito que as cubra.

### 7.5 Etapas e gates (SDD)

| Etapa | Escopo | Gate |
|---|---|---|
| A1 | `RootTreeBuilder` (TXT→ROOT) | ROOT com 59 ocorrências de linha; spot-check 10/10 do arquiteto reproduzido programaticamente |
| A2 | `XslGenerator` core (`ide/emit/dest/det/total/transp/cobr/pag/infAdic`) | XSL compila; saída **valida no XSD** (elemento `NFe`) |
| A3 | diff vs gabarito no `<infNFe>` | **diff==0** (usar `CanonicalDiffer`; relatório de diffs residuais por região se >0) |
| B | `<dadosAdic>` (extensão FIAT/ANFAVEA: B2BDirectory, PrinterKey, bloco290…, de campos de controle `NA`) | diff==0 no documento completo |

**Fora de escopo da PoC-3:** grupos especializados sem gabarito (veículos/ANVISA/ANP), Ollama/LLM (tudo determinístico),
runner Sysmiddle (retomar DEPOIS — a instância FiatMQ em `.claude/tmp/servidor/fiatmq/Instance_FiatMQ` vira fonte de
gabaritos em lote; o `FiatMQ_Instance_FiatMQ.exe.config` deve revelar o bootstrap de licença).

### 7.6 Rodada 1 do `--generate` (2026-07-11) — diagnóstico dos 59 diffs

Pipeline roda ponta-a-ponta: **A1 ✅** (ROOT 59 registros, spot-check 4/4), **A2 parcial** (XSL compila e aplica; 404
folhas de 712; XSD-validator com bug de wiring), **A3 = 59 diffs**. Comparação estrutural gerado×gabarito
(`ide/dest/total/ICMSTot/det`) prova que o cascata `[NOME]` = **conjunto de elementos difere**, não ordem. Causas-raiz,
priorizadas por alavancagem:

| # | Causa-raiz | Evidência | Dono | Fix |
|---|-----------|-----------|------|-----|
| R1 | **Opcionais vazios NÃO omitidos** (SOBRA) | `dest/idEstrangeiro`, `det/impostoDevol`, `det/DFeReferenciado`, `cobr/fat/vLiq` gerados mas ausentes no gabarito | Dex | aplicar §7.3.4 (`xsl:if` não-vazio) em TODO campo opcional; tratar o choice CNPJ/CPF/idEstrangeiro |
| R2 | **Catálogo DESCARTOU campos legítimos** (FALTA) | `ICMSTot/{vPIS,vCOFINS,vOutro,vNF,vFCPST,vICMSUFDest}` — a heurística de região do `NfeLeiauteCatalog` marcou "det≠total" e descartou (`#338 vPIS…descartado`) | Lia | relaxar o descarte por região para destinos `total/ICMSTot/*` (mesma folha, região total é válida) |
| R3 | **`det/@nItem` emitido como ELEMENTO** em vez de atributo | gerado `<det><nItem>` ; gabarito `<det nItem="1">` | Dex | XslGenerator: refs cujo XSD-node é `xs:attribute` viram `xsl:attribute`, não elemento |
| R4 | **Campos derivados/não resolvidos** (FALTA) | `ide/{cNF,tpEmis,procEmi}`, `emit/CRT`, `pag/detPag/vPag`, `compra`, `transp/vol` | Lia+Dex | resolver os diretos que faltaram (ex.: `#26 tpEmis`) + regra p/ derivados da chave (`cNF/cDV` = substring da Chave-Acesso) |
| R5 | **Formato decimal** | `vICMSDeson` gerado `0.0` vs `0.00` | Dex | `format-number` com casas da coluna Decimais/tipo XSD (não `0.0#`) |
| R6 | **XSD-validator wiring** | `Type TNFe is not declared` | Dex | carregar o SCHEMA SET (leiaute + `tiposBasico`+includes do dir), não o `.xsd` isolado |

**Leitura de arquiteto:** R1+R2 sozinhos devem colapsar a maioria dos 59 (SOBRA e FALTA em `ide/total/dest/det`). R3
é estrutural pontual. R4 são ~8 campos nominais. R5/R6 triviais. **Nenhum diff indica falha de abordagem** — o gerador
está correto; faltam refinamentos determinísticos. Próxima rodada mira colapsar `ide`, `dest`, `total`, `det` primeiro.

**Estado dos gates:** A1 ✅ · A2 🟡 (compila/aplica; XSD-wiring R6) · A3 ❌ 59 (alvo: iterar R1→R6).

### 7.7 Rodadas 2–7 (2026-07-11, arquiteto no loop `--generate`) — 59 → **20 divergências REAIS**

Iteração determinística com o diff colapsando a cada fix (o gate posicional acusa 46 por CASCATA; o **set-diff honesto
por path dá 20**). Fixes aplicados (todos documentados em comentário no código):

| Fix | O quê | Efeito |
|---|---|---|
| R1a | Choice escalar (CNPJ\|CPF\|idEstrangeiro) → `xsl:choose`, zeros-only = vazio | mata SOBRA `idEstrangeiro` |
| R1b | `EmbrulharGruposOpcionais` agora DESCE por `for-each`/`if`/`choose` + `RemoverCascasVazias` | mata cascas `impostoDevol`/`DFeReferenciado` |
| R2 | Reancoragem por afinidade de bloco — **só blocos não-det** (na região det inunda `prod/*`) | `ICMSTot` ganha `vPIS/vCOFINS/vOutro` |
| R3 | Folha que é ATRIBUTO no XSD → descartada (atributos têm regra própria) | mata `<nItem>` elemento |
| R5 | Corte decimal pela largura REAL (`translate`+`string-length`) | mata `0.0 ` com padding |
| R6 | `XsdValidator` com `XmlUrlResolver`+`Compile` (schema-set completo) | oráculo XSD funcional (23→13 erros) |
| F10 | Decimais da spec é RANGE (`0-4`,`11v0-4`) → último número; **tipo XSD manda** (`TDec_0302a04`→2, `TDec_1104v`→4) | `qCom 6.0000` ✅, `pICMS 0.00` ✅ |
| F8 | No det, decimal OPCIONAL zerado é omitido; zero-omit por PREFIXO de tipo | mata SOBRAs `prod/vFrete…`, `qUnid` |
| F9 | Grupos especializados de produto (veicProd/med/comb/DI…) sem driver de variante → descarte honesto | mata SOBRAs `DI`/`comb` (posições sobrepostas) |
| F11 | `infCpl` = concat LINHA081 ("Informações **para EDI**", pulando campos de controle) | `infCpl` idêntico ao gabarito ✅ |

**Fechados 100%:** `det/prod`, `det/imposto` (ICMS00+IPITrib+PIS/COFINS), `dest`, `infAdic/infCpl`, decimais.

**Restam 20 (inventário honesto):**
- **11 FALTA → R4 (Lia, domínio/catálogo):** `ide/{cNF,tpEmis,procEmi}` + `cDV` TEXTO (deriváveis da CHAVE DE ACESSO:
  cUF|AAMM|CNPJ|mod|serie|nNF|tpEmis|cNF|cDV; procEmi=constante), `emit/CRT` (#49a), `det/prod/indTot` (#116b),
  `pag/detPag/vPag`, `ICMSTot/{vFCPST,vICMSUFDest,vNF}` (refs não resolvidos p/ LINHA050), `transp/modFrete` (#357),
  `transporta/UF`.
- **8 SOBRA → Etapa B (emissão guiada pelo MAPEADOR):** `retTrib/*` (7) e `cobr/fat/vLiq` — zeros que o mapeador FiatMQ
  omite mas `fat/vOrig,vDesc` (também zeros) ele emite. Não derivável de spec/XSD → o filtro definitivo é o conjunto de
  targets do MapperVO real (que já parseamos no XslSynth). **Insight-chave da Etapa B: usar os 237 links + 98 regras do
  mapeador como máscara de emissão.**

**Nota p/ visão autônoma (Ollama):** `generated-notes.txt` (decisões/descartes) + o set-diff por path são exatamente os
sinais de feedback que o loop autônomo dará ao LLM local — o trilho determinístico está pronto; o LLM entra como
operador das exceções.

### 7.8 Rodada 8 — R4 (2026-07-12, Lia) — **FALTA=0 e TEXTO=0** ✅

As 12 FALTA/TEXTO zeraram. Gate agora é o **set-diff por path** (novo no relatório A3 do `Program.cs`:
multiconjuntos de folhas/atributos por caminho completo — FALTA/SOBRA por contagem, TEXTO por par ordinal;
o diff posicional vira detalhe, pois infla por cascata).

| Fix | O quê | Matou |
|---|---|---|
| T1 | **Derivação da CHAVE DE ACESSO** (`XslGenerator.EmitirDerivadosDaChave`): leiaute fixo cUF(2)AAMM(4)CNPJ(14)mod(2)serie(3)nNF(9)tpEmis(1)cNF(8)cDV(1) → `tpEmis/cNF/cDV` por substring + `procEmi`='0'; especial VENCE usos normais (dedup padrão infCpl) | `ide/{cNF,tpEmis,procEmi}` FALTA + `cDV` TEXTO |
| T2a | **Strip de enumerações/guidance** nas `xs:documentation` (`SemanticMatcher.Boilerplate`): " 0- Contratação…", ":0-Pagamento…", "Esta tag poderá ser omitida…", "Deve ser informado…" diluíam a precisão (vPag score 0.35 < 0.45) | `emit/CRT` (#49a), `transp/modFrete` (#357), `pag/detPag/vPag` (#398c) |
| T2b | **Sinônimos bidirecionais de domínio**: `nf↔nfe` ("NF-e"→{nf} × "Nfe"→{nfe}), `destino↔destinatario` + `interestadual↔partilha` (doc do vICMSUFDest = "ICMS de partilha p/ UF do destinatário"), `kilos↔kg`, composto `base+calculo→bc` | `ICMSTot/vNF` (#341), `ICMSTot/vICMSUFDest` (#329.05), `vol/pesoL` (#386), `modBC` |
| T2c | **Resgate por identidade** (score ≥ 0.999 = doc textualmente igual à descrição → margem só se o vice também for idêntico) em `Best` e `BestSemanticByName` | `ICMSTot/vFCPST` (#331.01: 1.0 × vFCPSTRet 0.97, margem 0.03), `modBC` multi-ref (1.0 × modBCST 0.95) |
| T2d | **Meia-janela no bracket unilateral** (numeração monotônica: #n > último pin vive DEPOIS dele; fallback global elegia homônimo de outra região) | `transporta/UF` (#365: caía no enderEmit/UF e perdia o dedup p/ #41) |
| T2e | **Alias verificado** `#116b → det/prod/indTot` (único caso: a semântica vive DENTRO da enumeração da doc, que o strip remove) — validado contra XSD em runtime, nunca inventa path | `det/prod/indTot` |
| T2f | Curinga `IPI/*/CST` reconhecido como região IPI (docs de IPITrib/CST e IPINT/CST ficam idênticas após o strip → multi-ref vira curinga) | regressão: IPITrib inteiro sumia (driver CST perdido) |
| T2g | `EmbrulharGruposOpcionais` com coleta RECURSIVA de testes (grupos 1-1 intermediários são atravessados) | regressão: casca `<impostoDevol><IPI/></impostoDevol>` |

**Estado (par real):** set-diff `FALTA=0, TEXTO=0, SOBRA=8` (retTrib×7 + `cobr/fat/vLiq` — **Etapa B**, máscara do
mapeador). Diff posicional: 2 (os próprios grupos SOBRA). **XSD (elemento NFe): 7 erros**, TODOS das SOBRAs
(`TDec_1302Opc` proíbe `0.00` em opcional — colapsam quando a Etapa B suprimir o retTrib); era 13.
Catálogo: **62 Alta / 400 Média / 166 não-resolvidos** (era 55/383/190); cobertura **509/712 campos (71,5%)**;
âncoras 9/9 ✅. Gates: A1 ✅ · A2 ✅ (compila, aplica; XSD 7 erros = Etapa B) · **R4 ✅** · A3 pleno = Etapa B.

### 7.9 🎯 MARCO — Etapa B1 (2026-07-11): **diff==0 no `<infNFe>` + XSD VÁLIDO**

```
[A3] set-diff por path <infNFe>: FALTA=0, SOBRA=0, TEXTO=0
     XSD (elemento NFe): VÁLIDO ✅ (resta só a assinatura — esperado, a PoC não assina)
```

**O pipeline determinístico Excel→TCL→ROOT→XSL reproduz o `<infNFe>` do mapeador de produção IDENTICAMENTE
e a saída valida no XSD da SEFAZ.** Zero LLM em runtime.

Como as 8 SOBRAs caíram — **`MapperEmissionGuide`** (`ai/XslSynth/Excel/MapperEmissionGuide.cs`): as regras DSL do
mapeador real têm guarda ANINHADA que spec/XSD não expressam — `if(IsNullOrEmpty(#.x)!=True()) begin if(#.x != 0)
begin T.path = … end end` (retTrib/*, cobr/fat/vLiq). O guia varre as 98 regras, extrai os destinos com guarda `!= 0`
(11 no total) e o XslGenerator troca o teste desses destinos para `number(...) > 0`. **Seguro por construção: só
aperta testes.** Degrade gracioso sem o MapperVO. Descobertas: typo real do mapeador `vBCIRRFL` (tolerância de 1 char
por pai igual) e variáveis DSL com ACENTOS (`#.valorRetençãoPrevidencia` → regex `\w` Unicode, não `[A-Za-z0-9_]`).

**Documento COMPLETO (enviNFe): SOBRA=0, TEXTO=0, FALTA=18** — todas na camada proprietária:
16 filhos de `dadosAdic` (B2BDirectory, PrinterKey, bloco290, vlr*ST '0,00' pt-BR…) + `idLote` + `indSinc`.
**Etapa B2 (próximo incremento):** todos os 18 são dirigidos por REGRAS do mapeador (ex.: `Rule_vlrCOFINSST`) —
traduzir com o `DslBlockInterpreter` já existente + fontes de controle (`#XML=NA`). Depois: **runner (passo 3)** —
descompilar o stub `FiatMQ_Instance_FiatMQ.exe` (6 KB) e fazer da Instance_FiatMQ a fábrica de gabaritos em lote.

## 8. Plano da Etapa B2 — envelope + `dadosAdic` (doc completo diff==0)

> **Status:** planejado (Aria, 2026-07-13) · execução: Dex (interpretador) + Lia (domínio DSL) · pós-merge PR #1.

### 8.1 Fato central (verificado no MapperVO)

**Os 18 campos faltantes são TODOS dirigidos por regras DSL do mapeador** (nenhum vem da spec Excel — lá são `NA`).
Padrões reais encontrados:

| Padrão | Exemplo real | Construto DSL |
|---|---|---|
| Constante | `Rule_idLote: T.enviNFe/idLote = '00001';` · `Rule_indSinc: = 0` | atribuição literal |
| **if/else com literal no else** | `Rule_vlrCOFINSST/vlrPISST`: `if(IsNullOrEmpty(x)!=True() && x!='xml' && ConvertToInt32(0,x)!=0) → FormaterDecimal(x,2) ELSE → '0,00'` | else + guarda tripla + **formato pt-BR com vírgula** |
| Campo de controle → tag | `B2BDirectory`, `PrinterKey`, `BaseForm`, `CodigoImpressao`, `Codigo_Connector` (fontes: LINHA000) | atribuição direta/condicional |
| **Acumulador string** | `Rule_bloco290`: `#.bloco290 = Concat(#.bloco290, PadRight('', ' ', 3, false)); … T.…/bloco290 = #.bloco290` | Concat/PadRight em loop — eco do bloco de controle |

### 8.2 Design

**Reusar o tradutor de regras que JÁ EXISTE** (`DslBlockInterpreter`/`DslRuleTranslator` do fluxo `--real`), plugado ao
`--generate` **escopado aos targets fora do `infNFe`** (envelope `enviNFe/*` + `NFe/dadosAdic/*`). Extensões necessárias
ao interpretador (todas determinísticas):

1. **`else`** → `xsl:choose` (hoje só `if…begin…end`);
2. **guardas compostas**: `&&`, `x != 'xml'` → `normalize-space(sel) != 'xml'`, `ConvertToInt32(0,x) != 0` → `number(sel) > 0`;
3. **literais** como valor (`'00001'`, `0`, `'0,00'` — preservar a vírgula pt-BR: são extensão, não SEFAZ);
4. **funções de string**: `Concat` → `concat()`, `PadRight(s,c,n)` → `substring(concat(s,'ccc…'),1,n)`;
5. **variáveis globais `$.`** (ex.: `$.buildChaveAcesso`) — resolver por pré-passo (já temos a chave no `--generate`).

Ordem de ataque: **B2.1** = 17 campos (constantes + if/else + controle) → **B2.2** = `bloco290` (acumulador, o único
complexo). Gate: **set-diff do DOCUMENTO COMPLETO = 0/0/0** (hoje: FALTA=18, SOBRA=0, TEXTO=0).

### 8.3 Semântica das funções DSL — fonte da verdade (2026-07-13)

As funções chamadas pelas regras vivem em duas DLLs (ambas em `tools/LowCodeRunner/`):
- **`Functions/ndd.ConnectUs.Functions.dll`** (NDD, ~2.8MB) — `IsNullOrEmpty`, `Concat`, `PadLeft/PadRight`,
  `StringFormat`, `Substring`, `DateTimeNow`, `ReplaceDotToComma`… **COM FONTE** em
  `D:\Projetos\git.ndd\ConnectUs.Functions\src\ndd.ConnectUs.Functions` (acessível nesta máquina) — ler a fonte real
  antes de traduzir qualquer função para XSLT.
- **`libs/SysMiddle.ConnectUs.Functions.dll`** (Sysmiddle 3ª parte, ~1.3MB, SEM fonte) — funções padrão no padrão
  `*Function` (`ConvertToInt32Function`, `ConvertToDecimalFunction`, `FormaterDecimal*`…) — semântica por
  descompilação (ILSpy) quando necessário.

### 8.4 Riscos
- `bloco290` reconstrói uma linha de 290+ chars por concatenação — traduzir fielmente exige PadRight correto (testar
  char a char vs gabarito). Se travar, alternativa honesta: eco bruto de região do input (comparar com o gabarito decide).
- Formato pt-BR (`'0,00'`) NÃO pode passar pelo pipeline de decimais SEFAZ (ponto) — manter como literal do else.

## 9. Plano do Runner (fábrica de gabaritos em lote)

> **Status:** planejado (Aria, 2026-07-13) · execução: Dex · pré-requisito: nenhum (independente de B2).

### 9.1 Descoberta que muda o jogo (strings do stub de 6 KB)

O `FiatMQ_Instance_FiatMQ.exe` é um **Windows Service** mínimo (assembly original `appConnector.Client.Connector.exe`):
`Service1.OnStart` instancia **`EDocsClientConnectorManager`** (campo `_eDocsClientManager`, interface `IManager` de
`appConnector.Client.Interface`), classe que vive em **`appConnector.Client.Core.dll`** — a MESMA que o
`tools/LowCodeRunner` já referencia. **É este manager que faz a init completa (licença incluída).**

### 9.2 Sequência de trabalho

1. **Descompilar** (ILSpy/ilspycmd) `EDocsClientConnectorManager` + `IManager` na Bin da instância: mapear o método de
   start exato, a ordem de init e onde a licença é registrada; confirmar dependências de config (`global.config`,
   `ConfigParameters/ConfigParameters.xml`, `Settings/`).
2. **Adaptar o `tools/LowCodeRunner`**: replicar o `OnStart` (instanciar o manager → start → aguardar init) e SÓ ENTÃO
   chamar `MappersHelper/ExecuteMapper`; `OnStop` no finally. Rodar DE DENTRO da Bin da instância trazida
   (`.claude/tmp/servidor/fiatmq/Instance_FiatMQ/AppConnector.DIR/Bin`).
3. **Modo lote**: varrer `Examples/LAY_*` (inputs reais .mq_series/.txt) → executar o mapeador → gravar pares
   input→XML em `.claude/tmp/gabaritos/` → alimentar o loop diff==0 com N pares (generalização além do par único).

### 9.3 Riscos (avaliar no passo 1 ANTES de rodar)
- ⚠️ **O manager é o CONECTOR completo**: o start pode tentar abrir filas MQ/diretórios/DB do servidor. Mitigação:
  inspecionar `ConfigParameters.xml` e **desabilitar componentes** de transporte antes de rodar (manter só o núcleo de
  mapeamento); ambiente sem VPN já isolaria endpoints, mas o desligamento explícito é o caminho limpo.
- Licença pode ser máquina-bound (falhou fora do host antes) — se o start do manager não bastar nesta máquina,
  fallback: rodar o runner NO SERVIDOR (onde a instância é licenciada), via o mesmo exe.
- Versões: usar EXCLUSIVAMENTE as DLLs da Bin da instância (v4.4.1 consistente) — nada de misturar com `.claude/tmp/sysmiddle` (v4.5).

## 10. Divisão de trabalho

- **@lp-parser-llm (Lia):** `ExcelSpecParser`, `NfeLeiauteCatalog` (domínio do leiaute/NT), resolução semântica, RAG.
- **@lp-backend-dev (Dex):** `TclGenerator`, `XslGenerator`, wiring no `Program.cs` (`--excel`), integração com `XsdValidator`.
- **@lp-qa (Quinn):** gates por fase, validação XSD, métricas de cobertura.
- **@lp-devops (Gage):** segredos/rotação; qualquer push.
- **@lp-architect (Aria):** este design; revisão de convergência dos dois catálogos (Excel × GUID).
