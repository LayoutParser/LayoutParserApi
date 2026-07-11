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
| PoC-1 ✅ | `TclGenerator` → `TCL <MAP>` para NFe 4.00 | ✅ TCL bem-formado; 1042 FIELD; lengths cobrem ~600/linha |
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

## 7. Divisão de trabalho

- **@lp-parser-llm (Lia):** `ExcelSpecParser`, `NfeLeiauteCatalog` (domínio do leiaute/NT), resolução semântica, RAG.
- **@lp-backend-dev (Dex):** `TclGenerator`, `XslGenerator`, wiring no `Program.cs` (`--excel`), integração com `XsdValidator`.
- **@lp-qa (Quinn):** gates por fase, validação XSD, métricas de cobertura.
- **@lp-devops (Gage):** segredos/rotação; qualquer push.
- **@lp-architect (Aria):** este design; revisão de convergência dos dois catálogos (Excel × GUID).
