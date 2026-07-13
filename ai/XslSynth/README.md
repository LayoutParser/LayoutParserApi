# XslSynth — MVP do loop de síntese de XSLT guiada por verificador

> **PT-BR** · Prova de conceito (Fase 0-2) do loop que reproduz a transformação
> low-code do Sysmiddle como um **XSLT gerado**, validado contra o XML final
> (gabarito) por **diff canônico + XSD**. Roda **100% em Linux/WSL** e **100% offline**.
>
> Arquitetura completa: [`docs/architecture/ia-xslt-synthesis.md`](../../docs/architecture/ia-xslt-synthesis.md).

Projeto **.NET 10 console STANDALONE** — **não** referencia o runtime da API
(que é Windows-only por causa da cripto Sysmiddle). Opera sobre **dados exportados**,
exatamente como a síntese rodará na futura VM Linux.

---

## O que é

Implementa o **loop fechado** do documento de arquitetura (§4):

```
1. EXTRAIR    MapperVO (XML) → LinkMappings (diretos) + Rules (C#) + XslContent
2. TRANSPILAR LinkMappings → XSLT baseline           ......... por CÓDIGO (sem IA)
3. GERAR      Rules C# → templates XSLT               ......... LLM (ou Mock)
4. APLICAR    roda o XSLT no input                    ......... por CÓDIGO
5. VERIFICAR  diff canônico vs gabarito + XSD         ......... por CÓDIGO
6. CORRIGIR   realimenta os diffs → LLM conserta      ......... LLM (ou Mock)
      repete 4→6 até diff == 0 e XSD válido
```

Os passos 1, 2, 4, 5 são **determinísticos** (baratos/confiáveis). O LLM só entra em
3 e 6, **guiado por erro concreto**. Nenhuma saída do LLM é aceita sem `diff == 0` **e**
XSD válido — o verificador é o coração (decisão não-negociável da arquitetura).

### Componentes (`Core/`, `Synthesis/`)

| Arquivo | Papel |
|---------|-------|
| `Core/MapperExtractor.cs` | MapperVO (XML) → `MapperVo` (LinkMappings + Rules + XslContent) |
| `Core/DeterministicXslTranspiler.cs` | LinkMappings → XSLT baseline (normalize-space, DefaultValue) |
| `Synthesis/IXslSynthesizer.cs` | Contrato do gerador (traduz Rule C#→XSLT e conserta por diff) |
| `Synthesis/MockXslSynthesizer.cs` | Determinístico — traduz o C# do exemplo; faz o loop convergir **offline** |
| `Synthesis/OllamaXslSynthesizer.cs` | LLM local via Ollama `/api/generate` |
| `Core/XsltApplier.cs` | Aplica o XSLT (`XslCompiledTransform`, XSLT 1.0) |
| `Core/CanonicalDiffer.cs` | Diff node-a-node (normaliza espaços/atributos/namespaces), reporta XPaths |
| `Core/XsdValidator.cs` | Valida a saída contra um XSD |
| `Core/RepairOrchestrator.cs` | O loop: transpilar → completar → aplicar → diff → consertar |

---

## Como rodar o demo (offline, sem Ollama)

```bash
dotnet run --project ai/XslSynth
```

Usa o `MockXslSynthesizer` e o **exemplo sintético embutido** em `sample/`
(4 LinkMappings diretos + 1 Rule C# com um ternário de comparação). Saída esperada:

```
== Baseline (transpilador determinístico) ==
   Campos diretos transpilados: 4/5 (80%)
   Diffs: 1 | XSD: INVÁLIDO
     [FALTA]   /Nota/faixa (esperado: <faixa>)
== Iteração 1: síntese de regras (Mock (determinístico, offline)) ==
   + regra 'ClassificaFaixa' → /Nota
   Diffs: 0 | XSD: válido
✅ CONVERGIU (diff == 0 e XSD válido).
```

Ou seja: o **baseline determinístico** resolve 4/5 campos (mas fica com 1 diff e XSD
inválido, pois falta a regra); a **síntese da Rule** fecha o diff → **CONVERGIU**.
Exit code `0` em convergência, `1` caso contrário.

### O exemplo (`sample/`)

| Arquivo | Conteúdo |
|---------|----------|
| `mapper.xml` | MapperVO sintético (4 LinkMappings + 1 Rule) |
| `input.xml` | documento de entrada (posicional já parseado em XML) |
| `expected.xml` | gabarito (o XML final que o Sysmiddle produziria) |
| `schema.xsd` | XSD pequeno (faz o papel do XSD da NF-e) |

> **Nota de modelagem:** no MVP, `InputLayoutGuid`/`TargetLayoutGuid`/`TargetElementGuid`
> já contêm o **XPath resolvido**. No sistema real são GUIDs resolvidos via catálogo de
> layout — ponto de extensão `MapperExtractor.ResolvePath()`.

---

## Como plugar o Ollama (LLM local)

```bash
# 1. Ollama rodando no WSL/host, com um modelo code-tuned:
ollama pull qwen2.5-coder        # 1ª escolha (ver arquitetura §6)

# 2. Rodar o loop usando o LLM:
dotnet run --project ai/XslSynth -- --ollama
#   ou:  XSLSYNTH_SYNTH=ollama dotnet run --project ai/XslSynth
```

Config por variável de ambiente:

| Env | Default | Papel |
|-----|---------|-------|
| `OLLAMA_URL` | `http://localhost:11434` | endpoint do Ollama |
| `OLLAMA_MODEL` | `qwen2.5-coder` | modelo (substitui o `deepseek-coder:6.7b` atual) |

O `OllamaXslSynthesizer` traduz cada Rule C# em fragmento XSLT (`SynthesizeRulesAsync`)
e conserta o XSLT a partir dos diffs residuais (`RepairFromDiffAsync`), com
`temperature = 0.0` para máxima reprodutibilidade.

> ⚠️ **Segurança:** LLM **local** mantém dado fiscal sensível no servidor. **Nunca** envie
> documentos/dados reais de cliente para LLM em nuvem sem autorização explícita — prefira
> Ollama on-premise e, para nuvem, apenas dado anonimizado/sintético.

---

## Como plugar dados reais (pontos de extensão)

1. **Corpus** — troque `sample/` por pares reais `input → XML final` exportados por
   mapeador (seção `TransformationPipeline` da API: `ExamplesPath`, `ExpectedOutputsPath`).
2. **Resolução de GUID → XPath** — implemente `MapperExtractor.ResolvePath()` consultando
   o catálogo de layout (a API já tem o catálogo).
3. **Mais regras C#** — o `OllamaXslSynthesizer` já cobre C# arbitrário via prompt; o
   `MockXslSynthesizer` cobre apenas o padrão do demo (ternário de comparação).
4. **XSD real** — aponte o `XsdValidator` para os XSDs de NF-e/CT-e/NFCom/MDFe
   (seção `XsdValidation`).

---

## Nota sobre o engine XSLT (Saxon em produção)

O MVP usa **`System.Xml.Xsl.XslCompiledTransform`**, que é **XSLT 1.0** (cross-platform,
sem dependências externas — ótimo para provar o loop). A **NF-e costuma exigir XSLT
2.0/3.0**, então **produção deve adotar Saxon** (Saxon-HE grátis; `SaxonCS`/`Saxon-HE`
para .NET) — decisão de arquitetura (§8). A troca é isolada em `Core/XsltApplier.cs`.

---

*LayoutParser · XslSynth (MVP Fase 0-2) · `@lp-parser-llm` · roda em Linux/WSL, offline.*
