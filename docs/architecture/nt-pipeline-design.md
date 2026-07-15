# Design — Pipeline "NT nova" (regenerar a spec a partir de diff de XSD + PDF)

> **Autor:** @lp-architect (Aria) · **Status:** Proposta (design da fase **B5**, Trilha B) · **Data:** 2026-07-15
> **Origem:** roadmap P1 em [`poc-excel-generator.md`](poc-excel-generator.md) §5 ("Pipeline NT nova") · fase B5 do
> [`multi-session-execution-plan.md`](multi-session-execution-plan.md) §3.
> **Natureza:** design + plano de protótipo de exploração — **não toca código de produção** (restrição da própria fase).

---

## 1. Problema

Quando a SEFAZ publica uma **NT nova** (caso concreto: **NT 2025.002 — RTC IBS/CBS/IS**, grupo UB/W03), o XSD
oficial muda — e com ele **tudo que deriva da spec**: a planilha Excel do cliente, o `NfeLeiauteCatalog`
(#XML→XPath), o TCL (parser posicional) e o XSL (transformação). O insight já registrado no roadmap: *o pipeline
não só gera XSL/TCL — ele **regenera a própria spec***. Hoje esse trabalho seria inteiramente manual.

## 2. Insumos reais disponíveis (verificado nesta sessão, 2026-07-15)

| Insumo | Onde | Estado |
|---|---|---|
| **XSD novo** (pacote `PL_010b_NT2025_002_v1.30`: `leiauteNFe_v4.00.xsd` 346 KB + tipos) | `.claude/tmp/servidor/layoutparser/xsd/PL_010b_NT2025_002_v1.30/` | ✅ em disco |
| **PDF da NT** (`NT_2025.002_v1.30_RTC_NF-e_IBS_CBS_IS.pdf`, 1,7 MB) | `.claude/tmp/servidor/layoutparser/pdf/PL_010b_NT2025_002_v1.30/` | ✅ em disco (pypdf extrai; poppler não instalado) |
| **`XsdLeiauteIndex.Load`** — parser de XSD que já produz nós em ordem de documento com XPath | `ai/XslSynth/` (usado pelo fluxo `--catalog`) | ✅ reuso direto — é a metade do differ pronta |
| **`NfeLeiauteCatalog` + `TclGenerator`/`XslGenerator`** — o motor determinístico | `ai/XslSynth/Excel/` | ✅ reuso (S3/S5) |
| **XSD "antigo"** (pacote anterior, ex.: `PL_009_V4`) | — | 🔴 **NÃO existe no dump** — lacuna estruturante, ver §5 P-3 |
| Corpus multi-cliente (`Examples/LAY_CNHI_*`, `LAY_IVECCO_*`, `LAY_MARELLI_*`…) | `.claude/tmp/servidor/layoutparser/Examples/` | ✅ bônus descoberto nesta sessão — 2º/3º clientes reais p/ generalização |

**Fato que amarra o caso de teste:** o oráculo atual do XslSynth **já aponta para o PL_010b**
(`ai/XslSynth/Program.cs:131-132`) enquanto a spec Excel FIAT **ainda não cobre** o grupo IBS/CBS/IS — ou seja,
o delta da NT 2025.002 é exatamente o exemplo real com que este pipeline deve ser provado.

## 3. Princípio de arquitetura

**Backbone determinístico; LLM só onde o insumo é prosa.** O *o que mudou* é machine-readable (diff de XSD) e
nunca passa por LLM. O LLM entra apenas no *o que significa* (semântica/regras extraídas do PDF da NT), e **toda
saída de LLM carrega citação** (página/seção da NT) — mesmo princípio de rastreabilidade do gate desenhado em
[`multi-client-layout-generalization.md`](multi-client-layout-generalization.md) §4.4.

## 4. Arquitetura — estágios, contratos e gates

```
S1  XsdDiff        (determinístico)  → XsdDelta.json          gate: identidade==0; mutação sintética detectada
S2  NtSemantics    (LLM sobre PDF)   → NtRuleMap.json         gate: 100% das entradas citam página/seção da NT
S3  CatalogDelta   (determinístico)  → delta #XML→XPath       gate: zero conflito com o catálogo existente
S4  SpecDelta      (PROPOSTA)        → linhas novas da spec   gate: APROVAÇÃO HUMANA (posições são do cliente)
S5  Regen TCL/XSL  (reuso do motor)  → TCL/XSL delta          gate: XSD novo válido + regressão diff==0 nos campos antigos
```

- **S1 — `XsdDiff`:** carregar velho e novo com `XsdLeiauteIndex.Load` e diffar **por XPath**: nó
  adicionado/removido, tipo alterado, ocorrência (`minOccurs`/`maxOccurs`), tamanho/pattern. Saída tipada
  (`XsdDelta`), serializada como artefato versionável.
- **S2 — `NtSemantics`:** extrair texto com pypdf; segmentar por **Grupo** (tabelas de leiaute §6/§7 da NT +
  regras de validação `Nxxx`); para cada XPath do delta, o LLM produz `{descrição, condicionalidade, regras Nxxx,
  citação}`. **Ollama local por padrão** (a NT é documento público — nuvem é permitida, mas local evita custo e
  quota; a regra de dados sensíveis de `security.md` não é o motivo aqui).
- **S3 — `CatalogDelta`:** gerar as entradas novas `#XML→XPath` do `NfeLeiauteCatalog`. Conflito com entrada
  existente = erro, nunca sobrescrita silenciosa. **Atenção:** a autoridade do código `#XML` é a **spec do
  cliente**, não a NT — para campo novo sem `#XML` definido pelo cliente, gerar placeholder marcado
  (`#XML-PROPOSTO-nnn`) que só vira definitivo na aprovação humana (S4).
- **S4 — `SpecDelta` (a decisão de arquitetura central deste design):** **posição em layout posicional é
  autoridade do CLIENTE** (o layout vive no Connect Us). O pipeline **propõe** linhas novas — seguindo a
  convenção observada (seq. pos. 1–6, código de bloco 7–9, novas `LINHAnnn` ao final) — e **nunca aplica**
  automaticamente. Quando o cliente atualizar o layout no Connect Us, a **Fonte B** da ingestão
  (`LayoutSpecExtractor`, fase G3/A4 da Trilha A) revalida a proposta contra o layout real — as duas fontes
  convergentes de §4.3 do design de generalização se fecham aqui.
- **S5 — Regen:** reusar `TclGenerator`/`XslGenerator` sobre a spec estendida. Gate duplo: (a) saída valida no
  **XSD novo**; (b) **regressão** — os campos pré-existentes continuam com diff==0 contra o gabarito conhecido.

## 5. Plano do protótipo (exploração — pode rodar na Trilha B, sem runner)

| # | Entregável | Testável hoje? | Dono |
|---|---|---|---|
| P-1 | `XsdDiff` CLI (`dotnet run -- --xsd-diff <velho.xsd> <novo.xsd>`) em `ai/XslSynth/NtPipeline/` | ✅ sem o pacote velho: (a) **identidade** PL_010b × PL_010b ⇒ 0 deltas; (b) **mutação sintética** (cópia local com elemento removido/tipo alterado) ⇒ delta detectado e classificado | Lia |
| P-2 | Smoke de extração do PDF (pypdf): localizar seções do Grupo UB e as tabelas de leiaute; medir se a segmentação por Grupo é viável | ✅ | Lia |
| P-3 | Diff real `PL_009 × PL_010b` | 🔴 bloqueado — obter pacote anterior (Portal NF-e, público, ou dump do servidor). Pendência de asset, não de código | usuário/Gage |

## 6. Riscos

| Risco | Mitigação |
|---|---|
| Tabelas complexas no PDF (extração perde estrutura) | âncoras textuais por Grupo + gate de citação + revisão humana em S4; se pypdf degradar, avaliar extração por página com LLM multimodal |
| Pacote XSD antigo indisponível | P-1/P-2 não dependem dele; P-3 explicitamente bloqueado até o asset chegar |
| Churn de erratas (v1.**30** já indica dezenas de revisões) | o pipeline é idempotente por construção: re-rodar com XSD/PDF novos regenera os deltas; artefatos S1–S4 são versionáveis |
| `#XML` novo sem definição do cliente | placeholder proposto + aprovação humana (S3/S4) — nunca inventar código definitivo |
| Posições no layout posicional | S4 é proposta, nunca aplicação — autoridade permanece no Connect Us |

## 7. Relação com o restante do roadmap

- **Alimenta B4 (RAG few-shot):** as regras `Nxxx` extraídas em S2 são exemplos naturais para o índice.
- **Consome B1/B2:** os TCL novos herdam o tamanho de linha paramétrico (não mais 600 fixo).
- **Não toca C1:** este pipeline é ferramenta de engenharia (regeneração de spec), não runtime de produção.
