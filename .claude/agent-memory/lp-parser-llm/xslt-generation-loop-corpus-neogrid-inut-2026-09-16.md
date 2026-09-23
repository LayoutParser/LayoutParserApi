---
name: xslt-generation-loop-corpus-neogrid-inut-2026-09-16
description: Primeira rodada real do loop RAG→gerar→validar no par NFe006c_InutNFe_NeoGridToSefaz (tcl/xsl) usando o modelo fine-tuned do projeto — resultado, decisões de design, e o que ficou pendente (persistência em banco).
metadata:
  type: project
---

**Tarefa:** primeira versão funcional do gerador TCL/XSL/XSLT a partir de mapeadores
Sysmiddle, usando o loop RAG→gerar→validar→corrigir. Caso de teste: par
`NFe/2.06b/NFe006c_InutNFe_NeoGridToSefaz` (tcl+xsl) do corpus Neogrid em
`.claude/temp/servidor/layoutparser/Examples/{tcl,xsl}/`.

## Decisão 1 — reusar `ai/XslSynth` (Metrics/*) em vez de estender `RAGService`/`ImprovedXslGeneratorService`

`RAGService.cs` (API) é um matcher ingênuo de keyword sobre arquivos `*.txt` linha-a-linha
— não serve para pares estruturados TCL→XSLT completos. `ai/XslSynth/Metrics/` já tem
exatamente essa unidade de indexação (`DatasetFewShotIndex`, TF-IDF/cosseno sobre o TCL de
entrada, held-out por caso) e o loop completo (RAG → Ollama → `OutputValidator` → Serilog),
construído e testado em 2026-07-29 (ver [[metrics-batch-mode-item1]]). Mesma decisão já
documentada lá: "a unidade de recuperação é diferente — reaproveitar `RAGService`/`FewShotIndex`
forçaria um encaixe artificial". Protocolo IDS aplicado: reusei o indexador que já existe
para essa unidade específica, em vez de estender um serviço com propósito diferente ou criar
um terceiro do zero.

O que MUDEI de fato: `appsettings.Development.json` ganhou `RAG:ExamplesPath` e
`XsdValidation:BasePath` apontando pro corpus local (`.claude/temp/...`, gitignored,
específico da sessão) — documentado aqui, não em comentário dentro do JSON (JSON não suporta
comentário nativo e o projeto não usa parser tolerante a isso).

## Decisão 2 — corpus indexado: 159 pares reais (não só o par-alvo)

Script `python` (`build_neogrid_jsonl.py`, ficou em `.claude/tmp/scratch/`, NÃO commitado —
gera artefato grande) percorre `Examples/tcl/**` + `Examples/xsl/**`, pareia por nome de
arquivo + pasta (doc_type/version), produz 1 JSONL no schema de `DatasetPair` (mesmo usado
pelo modo `--mode=metrics-batch`). Resultado: **159 pares** (NFe=75, CTe=63, MDFe=12, NFSe=9).
O par-alvo (Inutilização) foi ordenado primeiro no JSONL e avaliado com `--limit 1` — os
outros 158 continuam no índice few-shot como held-out (nunca recupera a si mesmo).

## Rodada real (não simulada)

```
cd ai/XslSynth && dotnet build   # 0 erros
OLLAMA_URL=http://172.25.32.5:11434 dotnet run --no-build -- --mode=metrics-batch \
  --dataset <jsonl 159 pares> --model layoutparser-sysmiddle-dsl:1.5b --fewshot-k 3 \
  --limit 1 --run-dir <scratch>/run-inut --run-id run-inut-001
```

Ollama confirmado acessível (172.25.32.5:11434, modelo `layoutparser-sysmiddle-dsl:1.5b`
presente — NÃO usei o Ollama local da workstation, que só tem `qwen2.5-coder:7b` genérico).

- Few-shot recuperado: top-1 foi `NFe/2.06c/NFe006c_InutNFe_NeoGridToSefaz` (mesmo tipo de
  operação, versão de leiaute diferente — sim=0.995). Confirma que a recuperação por
  similaridade funciona (achou o análogo mais próximo real, não o próprio caso).
- Geração: 91-98s, ~13.6 tok/s (CPU do servidor, modelo 1.5B — bem mais rápido que os
  `qwen2.5-coder:7b` de spikes anteriores, ~1.3-3.3 tok/s, ver [[rag-spike-cpu-throughput-2026-07-29]]).
- Saída: XML bem-formado=sim. `tagOverlap=0.577`, `textSim=0.538` (métrica automática/tolerante).

## Validação por diff estrutural REAL (leitura manual dos dois arquivos, não só a métrica automática)

Candidato salvo em `attempts/NFe_2.06b_NFe006c_InutNFe_NeoGridToSefaz.xsl` (dentro do run-dir
do experimento, não commitado). Comparado campo a campo contra o gabarito real
(`Examples/xsl/NFe/2.06b/NFe006c_InutNFe_NeoGridToSefaz.xsl`):

- **10 de 11 campos de `<infInut>` saíram BYTE-A-BYTE idênticos ao gabarito de produção**:
  `@Id` (concat com format-number), `tpAmb`, `xServ`, `cUF`, `ano`, `CNPJ`, `mod`, `serie`,
  `nNFIni`, `nNFFin` — a lógica XPath exata (inclusive `number()`/`format-number()`) foi
  reproduzida sem erro.
- **1 campo divergente**: `xJust` — o candidato emitiu `ROOT/Header/xJust` puro; o gabarito
  usa `normalize-space(concat('Justificativa Inutilizacao:', substring(normalize-space(...),
  '1','227')))` — uma regra de negócio (prefixo fixo + truncamento a 227 chars) que não está
  no TCL de entrada nem é inferível sem ver esse padrão em outro exemplo do domínio.
- **Estrutura do documento incompleta**: o candidato emitiu `<infInut>` como elemento raiz
  solto, faltando `<xsl:template match="/">`, o elemento raiz `<inutNFe
  xmlns="http://www.portalfiscal.inf.br/nfe">` e o atributo `versao="2.00"`. É a causa
  principal do tagOverlap=0.577 (a métrica automática de Jaccard de nomes de tag pesa
  fortemente a ausência desses 3-4 nós estruturais).

**Taxa honesta:** lógica de campo correta em 10/11 (91%) dos campos folha; wrapper
estrutural do documento (root+namespace+versao+template) 0/1 — ausente por completo. Sem XSD
real disponível localmente (ver bloqueio abaixo), não dá pra confirmar se o candidato validaria
contra o schema SEFAZ — a ausência do namespace/root sozinha já reprovaria.

## Bloqueio 1 — XSD real não disponível neste checkout

`XsdValidation:BasePath` aponta pro caminho de produção; localmente só existe
`.claude/temp/servidor/layoutparser/xsd/PL_010b_NT2025_002_v1.30/` (leiaute NFe completo,
não o XSD específico de `inutNFe`). `XsdValido` do `OutputValidator` fica sempre `null` por
design (mesma limitação documentada em [[metrics-batch-mode-item1]]) — a validação real desta
rodada foi por diff estrutural manual (acima), não por schema formal.

## Bloqueio 2 — persistência em banco NÃO executada

A arquitetura pedida (persistir via `IMappingReleaseStore`/`SqlMappingReleaseStore`,
`dbo.tbMappingRelease`, `ArtifactsJson`, marcado como experimento — não release real) foi
confirmada como o padrão correto a seguir (mesmo mecanismo do resto do projeto, nenhum
handoff por arquivo novo). **Não implementei o código de persistência nem tentei escrever no
banco**: `IdentityDatabase:UserId`/`Password` estão vazios neste `appsettings.json` (segredo
via `dotnet user-secrets`, que eu não tenho nesta sessão — ver [[sql-172-31-249-51-somente-leitura]]
para o princípio geral de não escrever "às cegas" em banco sem confirmar acesso). Escrever
código de INSERT sem conseguir rodá-lo e confirmar contra o schema real seria exatamente o
tipo de "declarar sucesso sem validação real" que a tarefa pediu pra evitar.

**Próximo passo concreto** (não feito aqui, para não expandir escopo sem dono): adicionar um
valor experimental ao enum `MappingReleaseArtifactSource` (hoje só `Compiled`/`ManualEdit` —
conferir em `Models/Entities/Fiscal/`) e um método `CreateExperimentalReleaseAsync` (ou
reaproveitar `CreateOrGetCompiledReleaseAsync` com esse novo source) — dimensionar com
`@lp-architect` antes, é mudança de contrato de release, não só um insert.

## Artefatos desta sessão (NÃO commitados — geram dado grande, ficam fora do git)

- `.claude/tmp/scratch/build_neogrid_jsonl.py` — script que gera o JSONL a partir do corpus.
- `.claude/tmp/scratch/neogrid_corpus.jsonl` — 159 pares (regenerável pelo script acima).
- `.claude/tmp/scratch/run-inut/` — manifest.json + attempts-manifest.json + o `.xsl`
  candidato real gerado nesta rodada + o prompt exato enviado ao Ollama.

**Why:** medir de verdade em 1 caso primeiro, com corpus real completo no índice — não
simular. **How to apply:** ao repetir para outros casos do corpus (CTe/MDFe/NFSe), reusar
o mesmo script/JSONL (só reordenar o caso-alvo pro topo) e o mesmo comando
`--mode=metrics-batch --limit 1 --run-dir <algo>` para conseguir o `.xsl` candidato bruto
pra diff manual — sem `--run-dir` o candidato não fica em disco, só as métricas vão pro log.
