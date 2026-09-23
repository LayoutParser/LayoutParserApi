# ADR — Fine-tuning nichado do Ollama para geração TCL/XSLT Sysmiddle (2026-09-02)

> **PT-BR.** Autoria: `@lp-architect`. Formaliza uma decisão do dono do projeto que **reverte**
> uma decisão de arquitetura anterior deste mesmo repositório. Não implementa nada — desenha o
> pipeline e delega execução a `@lp-parser-llm` (dataset + loop de treino) e `@lp-devops`
> (ambiente/infra do servidor de treino). Continuação prática de
> [`adr-artefatos-gerados-redis-workspace-funcoes-2026-09-02.md`](adr-artefatos-gerados-redis-workspace-funcoes-2026-09-02.md)
> (ADR #258, ainda aberto) — não o substitui.

## 1. Contexto

Nesta sessão, dono e coordenador finalizaram manualmente `Rule_gIBSCBSMono` (regra de mapeamento
Sysmiddle para IBS/CBS monofásico, Reforma Tributária, NT2025.002) como exercício de referência:
detectar mudança de campos no layout de input → validar a estrutura de destino contra o XSD
oficial (`TMonofasia` em `DFeTiposBasicos_v1.00.xsd`, `.claude/temp/treino/PL_010f_v1.04/`) →
gerar a regra final na DSL do Sysmiddle. Esse exercício deixou explícito o que o modelo precisa
aprender a fazer sozinho, e motivou o dono a mudar a estratégia de IA do projeto.

## 2. Decisão — reverter `no-fine-tuning-ai-decision`

**Decisão anterior (revertida por este ADR):** o roadmap de IA usaria **só RAG + Ollama, sem
fine-tuning**, com tamanho de modelo bloqueado até confirmar hardware (memória de
`@lp-architect`, `no-fine-tuning-ai-decision`, e reforçada por `production-server-hardware`:
servidor de produção `BRNDDAPPBLD01` é i7-4790 Haswell/DDR3 de 2014, sem GPU, recomendação era
mirar 1-2B e medir na prática).

**Decisão nova (dono, textual nesta sessão):** *"Agora o seu foco é treinar o Ollama com tudo
isso que já comentamos, finalize o treino com o fine-tuning, aumente a capacidade do modelo se
precisar, esse servidor do Ollama é pra nós justamente treinar e ir entendendo o que o modelo é
capaz, não interessa se não tem hardware, que demore 1,2 meses pra terminar um treino, mas vamo
deixar refinado e nichado pra exatamente isso que precisamos."*

**Leitura arquitetural da mudança:**

| Antes | Agora |
|---|---|
| Hardware fraco bloqueava tamanho de modelo e descartava treino local | Hardware deixa de ser bloqueador — tempo de treino (semanas/meses) é aceito explicitamente pelo dono |
| RAG puro: contexto injetado em prompt a cada chamada, modelo genérico | RAG **+** fine-tuning: modelo passa a carregar conhecimento nichado (DSL Sysmiddle, functions, XSDs fiscais) nos próprios pesos/adapter, RAG continua como complemento para contexto específico do documento em runtime |
| Servidor Ollama (`172.25.32.5`) tratado só como serving de inferência | Servidor Ollama vira também **ambiente de treino** — muda o perfil de uso (janelas de treino podem competir com inferência de produção) |

**Escopo do "nicho":** o modelo não deve virar um LLM genérico melhor — deve ficar
especializado em: (1) ler layout de input + XSD/schema de destino e inferir a estrutura
correspondente; (2) gerar regras na DSL Sysmiddle (`if/begin/end`, prefixos `I.`/`T.`, chamadas
de function como `FormaterDecimal(...)`) e o TCL/XSLT correspondente; (3) usar corretamente as
functions Sysmiddle e NDD catalogadas (seção 4).

Esta decisão **não revoga** os princípios de resiliência/dado sensível do projeto: o servidor
Ollama continua sendo infraestrutura própria (não nuvem), o treino usa dado fiscal já presente
internamente (mappers/regras já em produção), e a regra de "nunca mandar dado de cliente para
LLM em nuvem" (`rules/security.md`) permanece intacta — o fine-tuning é 100% local.

## 3. Pipeline de fine-tuning nichado

### 3.1 Dataset de treino — fontes e curadoria

| Fonte | Formato de saída proposto | Observação |
|---|---|---|
| (a) Mappers/regras Sysmiddle reais já catalogados (Redis/SQL) | par `(layout_input, xsd_destino, regra_dsl_gerada)` — JSONL, um exemplo por regra | `Rule_gIBSCBSMono` desta sessão é o exemplar de referência (padrão-ouro) para o formato; priorizar os mappers com maior reuso de campo/function primeiro |
| (b) Pares TCL↔XSLT existentes (259 pares já mapeados em `visao-migracao-sysmiddle-para-tcl-xslt-2026-08-30.md`) | par `(tcl, xslt)` alinhado por `MapperGuid` | Já é o material "padrão-ouro" citado no ADR #258 — reaproveitar sem re-trabalho |
| (c) XSDs oficiais NT2025.002 (`.claude/temp/treino/PL_010f_v1.04/`, `NT_2025.002_v1.50...pdf`) | schema normalizado (tipo, campo, cardinalidade, enum) extraído do XSD + trecho do texto normativo do PDF quando disponível | Serve de "grounding" de schema — ensina o modelo a validar contra a estrutura oficial, não só imitar sintaxe |

**Curadoria:** dataset inicial é pequeno (dezenas a poucas centenas de exemplos reais) — não dá
para treinar do zero, só para fine-tuning/LoRA sobre um modelo-base já competente em código.
Ampliação incremental: cada regra nova finalizada manualmente (como este exercício) deve ser
capturada automaticamente como exemplo de treino adicional — decisão prática para
`@lp-parser-llm`: instrumentar o fluxo de edição manual/aprovação de regra para also-gravar o
tripla `(input, schema, output)` num dataset append-only, sem esforço extra do usuário.

### 3.2 Estratégia de fine-tuning viável no hardware existente

Pesquisa desta sessão sobre o que é tecnicamente viável hoje para fine-tuning local de modelos
servidos via Ollama:

- **LoRA/QLoRA é o caminho, não fine-tuning completo.** Full fine-tuning de um modelo 6.7B+
  exige GPU com VRAM suficiente para os gradientes de todos os pesos — inviável em CPU-only
  mesmo aceitando semanas de prazo (é limitação de RAM/throughput, não só de tempo). LoRA treina
  só um adapter de baixo rank (tipicamente <1% dos parâmetros), o que já foi demonstrado rodando
  em CPU (mais lento, mas não impossível) usando ferramentas como `llama.cpp` (`finetune`/
  `export-lora`) ou frameworks Python como `axolotl`/`unsloth` com backend CPU. **Confirmar
  qual delas roda de fato no servidor Ubuntu `172.25.32.5` é tarefa de `@lp-devops`** — depende
  de RAM disponível, presença de Python/CUDA-less toolchain, e da versão exata do `deepseek-
  coder:6.7b` (GGUF servido pelo Ollama não é o formato de treino nativo; para LoRA via
  `axolotl`/`unsloth` normalmente se precisa do checkpoint HuggingFace equivalente, não do
  `.gguf` — outro ponto a verificar antes de estimar prazo).
- **Depois do LoRA treinado:** empacotar como adapter e servir via Ollama com `ollama create`
  usando um `Modelfile` que referencia o adapter (`ADAPTER ./meu-lora.gguf`, formato suportado
  pelo Ollama desde as versões recentes) — isso mantém a operação de *serving* igual à atual
  (mesma API HTTP, mesmo `Ollama:Url` em `appsettings.json`), só troca o modelo nomeado.
- **Prazo:** dado o aceite explícito do dono ("1-2 meses"), tratar o treino como job de
  background de longa duração no servidor Ubuntu, com checkpoints intermediários avaliáveis
  (não esperar o fim do treino para primeira validação) — reduz risco de descobrir só no fim que
  a estratégia não converge.

### 3.3 Aumento de capacidade do modelo — condicional, não automático

O dono autorizou "aumentar a capacidade do modelo se precisar" — isto **não é uma decisão a
tomar antecipadamente**, é uma escada condicional:

1. Primeiro, medir se `deepseek-coder:6.7b` com fine-tuning nichado já resolve o caso de uso
   (gerar regra DSL + TCL/XSLT correto para os casos do dataset de curadoria). Critério de
   sucesso: taxa de acerto estrutural (valida contra XSD) em um conjunto de holdout dos próprios
   259 pares/mappers catalogados.
2. Só se o 6.7B for insuficiente estruturalmente (não só "lento"), avaliar subir para 13B/34B —
   e **antes de comprometer semanas de treino nesse tamanho**, `@lp-devops` deve medir no
   servidor real: RAM disponível vs. requisito do checkpoint (regra geral, ~2GB de RAM por
   bilhão de parâmetros em fp16, mais para o processo de treino que também guarda otimizador/
   gradientes do LoRA), e rodar um teste de inferência pura nesse tamanho para confirmar que o
   tok/s não inviabiliza uso em produção depois de pronto (não adianta treinar um modelo que
   depois não serve request em tempo aceitável).
3. Este ADR **não recomenda um tamanho específico agora** — a decisão é adiada para depois da
   medição do passo 1, propositalmente, para não repetir o erro já documentado em
   `production-server-hardware` de estimar sem medir.

## 4. Plano de decodificação de functions (Sysmiddle + NDD)

O dono autorizou decompilar as DLLs Sysmiddle e NDD para extrair o comportamento das functions
usadas nas regras, com uma ressalva de risco tratada na seção 5.

**Escopo priorizado — não todas as functions, as mais usadas primeiro.** Antes de decompilar
qualquer coisa, `@lp-parser-llm` deve varrer os mappers/regras já catalogados (Redis/SQL) e
contar frequência de uso de cada function (`FormaterDecimal`, e equivalentes). Decodificar na
ordem de frequência decrescente até cobrir a cauda que efetivamente aparece em produção — não
"todas as centenas de functions existentes nas DLLs", a maioria nunca é usada nos mappers reais.

**Formato de saída por function (JSON, um arquivo por function ou um JSONL consolidado):**

```json
{
  "nome": "FormaterDecimal",
  "categoria": "sysmiddle | ndd",
  "assinatura": "FormaterDecimal(valor: string, casasDecimais: int) -> string",
  "comportamento_inferido": "descrição textual do que a function faz, inferida da decompilação",
  "exemplo_uso_real": "trecho da regra real onde a function aparece (ex.: Rule_gIBSCBSMono)",
  "mapper_origem": "MapperGuid ou nome do mapper de onde o exemplo foi extraído",
  "confianca_inferencia": "alta | media | baixa"
}
```

O campo `confianca_inferencia` existe porque decompilação de bytecode/IL nem sempre produz
comportamento 100% legível (otimizações do compilador, nomes de variável perdidos) — sinalizar
isso evita que o dataset de treino ensine o modelo com uma inferência errada como se fosse fato.

**Uso no treino:** este catálogo de functions vira parte do dataset de fine-tuning (seção 3.1)
como contexto estrutural — o modelo aprende "quando o campo é decimal monetário, a regra
Sysmiddle chama `FormaterDecimal`" a partir de múltiplos exemplos reais, reforçado pela descrição
JSON da function. Não é para o modelo decorar a lista de functions solta, é para ele associar
padrão de campo → function correta.

## 5. Nota de risco residual — engenharia reversa de DLL comercial (rastreável, não bloqueante)

O coordenador levantou, via `AskUserQuestion`, o risco jurídico de fazer engenharia reversa da
DLL comercial da Sysmiddle (código de terceiro, sob licença comercial — diferente das functions
NDD, que são código próprio e não têm essa restrição). A opção apresentada ao dono foi
explicitamente rotulada *"Decompilar ambas (Recomendado só se já há aval jurídico/contratual)"*.
O dono escolheu essa opção mesmo com o texto deixando claro a condição.

**Registro para rastreabilidade (não bloqueia a execução):** este ADR assume que o dono já
possui, ou assumiu o risco de não possuir, aval jurídico/contratual explícito para engenharia
reversa da DLL Sysmiddle. Essa confirmação é responsabilidade do dono do projeto, não de nenhum
agente — nenhum agente (`@lp-parser-llm` incluso) tem visibilidade sobre os termos de licença
reais firmados com a Sysmiddle. Se este ADR for revisitado no futuro (auditoria, troca de
fornecedor, disputa contratual), este parágrafo documenta que a decisão de decompilar foi tomada
cientemente pelo dono nesta data (2026-09-02), com o risco sinalizado antes da escolha.

**Mitigação prática sugerida (não jurídica, técnica):** decompilar apenas o necessário para
inferir *comportamento de entrada/saída* das functions (o que o dataset de treino precisa) — não
redistribuir, publicar, ou vazar código-fonte reconstruído da Sysmiddle em nenhum artefato público
ou repositório fora do escopo interno já protegido (repos da org já são privados, ver
`rules/security.md`). O JSON de saída da seção 4 registra *comportamento inferido*, não código
decompilado literal — reduz superfície de exposição mesmo dentro do risco já aceito.

## 6. Relação com o ADR #258 (Redis/workspace/functions)

Este ADR é a continuação prática de
[`adr-artefatos-gerados-redis-workspace-funcoes-2026-09-02.md`](adr-artefatos-gerados-redis-workspace-funcoes-2026-09-02.md):
aquele ADR já havia estabelecido a distinção entre functions NDD (customizadas) e Sysmiddle
(padrão) como algo que a IA precisa "aprender a fazer" — este ADR detalha *como* extrair esse
conhecimento (seção 4) e *como* ele entra no modelo via fine-tuning (seção 3), em vez de só RAG
em tempo de execução. Os dois ADRs devem ser lidos em conjunto por quem for implementar: #258
define onde os artefatos gerados (TCL/XSLT) vivem em produção (Redis, por `MapperGuid`,
workspace por usuário); este ADR define como o modelo que gera esses artefatos é treinado.

## 7. Divisão de responsabilidade (nenhum código implementado por este ADR)

| Responsável | Escopo |
|---|---|
| `@lp-devops` | Ambiente de treino no servidor Ubuntu `172.25.32.5`: instalar/validar toolchain Python (`axolotl`/`unsloth`/`llama.cpp finetune`), confirmar RAM/CPU disponível, converter checkpoint HuggingFace equivalente ao `deepseek-coder:6.7b` se necessário, medir viabilidade de subir para 13B/34B antes de qualquer treino nesse porte, configurar `Modelfile`/`ADAPTER` para servir o resultado via Ollama |
| `@lp-parser-llm` | Extração e curadoria do dataset (seção 3.1), instrumentação para capturar novas regras aprovadas como exemplos de treino, execução do loop de fine-tuning/LoRA propriamente dito, decompilação e catalogação das functions (seção 4), avaliação de holdout/critério de sucesso (seção 3.3 passo 1) |
| `@lp-architect` (este documento) | Só desenho e recomendação — nenhuma implementação |
| Dono do projeto | Aval jurídico/contratual sobre engenharia reversa da Sysmiddle (seção 5, já assumido) — qualquer reversão futura desta decisão |

## 8. Memórias afetadas

- `no-fine-tuning-ai-decision` (memória de `@lp-architect`) — **revertida por este ADR**; manter
  o arquivo antigo como histórico, mas linkar para cá como decisão vigente.
- `production-server-hardware` — segue válida como *dado factual* de hardware, mas sua conclusão
  prática ("não investir em treino") foi sobrescrita pela decisão explícita do dono nesta sessão.
