---
name: gemini-openai-decommission-decision
description: Decisão 2026-07-21 — abandonar Gemini/OpenAI por completo (Ollama 100% pro diagnóstico XSD); sem fine-tuning pra essa tarefa. Geração sintética é caso à parte (owner quer fork open-source de verdade) — hardware é CPU-only (iGPU Intel, sem GPU discreta), recomendação de modelo revisada.
metadata:
  type: project
---

Em 2026-07-21, respondendo a perguntas do dono do projeto (decisão #2 do "passo 0" + pergunta sobre
"treinar uma IA de código aberto exclusivamente"), depois revisado no mesmo dia com dado real de hardware e
um pedido mais específico sobre geração sintética.

## Decisão 1 — Gemini/OpenAI: abandonar por completo

Ver [[gemini-cloud-xsd-diagnosis-gap]] pro levantamento completo dos call-sites, o escopo seguro de remoção
e o achado de que o subsistema inteiro está com DI quebrado hoje (risco atual baixo, mas landmine se alguém
consertar o registro sem revisitar a decisão). Ollama assume 100% do papel de LLM na API pro diagnóstico
XSD. Sinergia: simplifica a pendência de rotação em `security.md` — a chave do Gemini (já marcada como
comprometida, rotação pendente) passa de "gerar nova chave" pra "revogar/desprovisionar de vez, nunca mais
precisa de chave" — mas rotação de senha SQL é assunto **não relacionado**, continua pendente igual. Ação de
`@lp-devops`, não decidida por mim aqui.

## Decisão 2 — Diagnóstico XSD / síntese de XSLT: sem fine-tuning, RAG continua

Contraria o princípio já estabelecido (README §5, `ia-xslt-synthesis.md` §2: "não se treina um modelo...
síntese guiada por verificador, não ML pesada"). Razões específicas a este caso, não só repetição da
doutrina:
- **Dataset pequeno demais:** escopo documentado é ~5 mapeadores com layout de input; o mapeador de
  referência tem 237 LinkMappings + 98 regras DSL. O corpus multi-cliente da Trilha A (FIAT/CNHI/IVECCO/
  MARELLI) é mais rico mas ainda pequeno pra fine-tuning e concentrado em poucos mapeadores — risco de
  overfitting/decorar valores já está no próprio doc de arquitetura (`ia-xslt-synthesis.md` §10).
- **Fine-tuning não remove o verificador:** mesmo com modelo fine-tunado, ainda precisaria do loop
  gerar→validar(XSD+diff)→corrigir pra confiar no resultado (dado fiscal, não dá pra confiar em "95%
  certo"). Fine-tuning adicionaria custo sem remover nenhum componente existente.
- **Custo de manutenção:** SEFAZ publica NT (notas técnicas) novas periodicamente (`docs/architecture/
  nt-pipeline-design.md`) — cada uma exigiria re-fine-tuning; um índice RAG só precisa de exemplos novos.
- **Não há gap de capacidade que só fine-tuning resolveria** — os dois usos (diagnosticar erro em linguagem
  natural; traduzir DSL→XSLT) já são bem servidos por modelo de código genérico via prompt. O gap real é
  precisão de prompt (proveniência, item 2/4/A6) e escolha de modelo — mais baratos que fine-tuning.
- Essa razão **NÃO se aplica igual à geração sintética de dado de teste** — ver Decisão 3, é um caso
  diferente e a distinção é válida.

## ⚠️ Correção de hardware (2026-07-21, mesmo dia) — modelo de 16-32GB de VRAM NÃO cabe

Recomendação anterior desta memória (`qwen3-coder:30b`/`devstral:24b`) estava calibrada pra GPU discreta de
16-32GB. **Checado pelo próprio usuário na máquina WSL em uso:** só GPU integrada Intel Iris Xe, sem NVIDIA/
AMD discreta (`nvidia-smi` nem existe); `Get-CimInstance Win32_VideoController` reporta 1GB (métrica de iGPU
notoriamente pouco confiável, mas não muda a conclusão: sem GPU discreta). `libd3d12.so`/`libdxcore.so`
presentes confirmam path DirectX/DirectML no WSL, não CUDA. Pesquisa desta sessão: suporte de iGPU Intel
pra inferência séria de LLM em 2026 é fraco (NPU da Intel ajuda tarefas leves tipo cancelamento de ruído, não
inferência de modelo) — **tratar como CPU-only é a suposição mais segura, mesmo com a iGPU presente.**

**Perguntas fechadas em 2026-07-21 (rodada seguinte):** (a) confirmado — Ollama do diagnóstico roda no
servidor de produção `BRNDDAPPBLD01`, não na máquina de dev WSL. (b) hardware real: **Intel i7-4790
(Haswell, 2014, 4c/8t, AVX2 sem AVX-512), 32GB RAM** — sem upgrade previsto ("por enquanto não vamos
investir em nada"). Ver [[production-server-hardware]] pro detalhe completo e a suspeita (não confirmada)
de que é a mesma máquina do runner de CI/espelho de dev.

**Recomendação final, dado hardware real (mais conservadora que a versão anterior):**
- Mirar **1-2B params** como ponto de partida (não 2-4B) — este CPU é bem mais fraco que os benchmarks
  recentes de "CPU-only" (que provavelmente assumiram DDR4/DDR5 e mais núcleos). Inferência em CPU é
  memory-bandwidth-bound, e essa plataforma (DDR3, 2014) tem bandwidth bem inferior ao que os números
  citados antes (Phi-4-mini ~12 tok/s) provavelmente assumiram — **não tenho medição real desta CPU**,
  qualquer tok/s aqui é extrapolação, não promessa.
  **Antes de comprometer com qualquer modelo: medir de verdade no próprio servidor** (baixar candidato via
  Ollama, prompt fixo, medir tok/s real) — não travar UX em número não medido.
- Configurar/compilar mirando AVX2 especificamente (`LLAMA_NATIVE=ON` ou equivalente) — essa CPU tem AVX2,
  não tem AVX-512; ajuste barato que ajuda throughput real.
- Timeout/degradação graciosa no serviço Ollama fica **mais crítico**, não menos: se estourar um teto
  razoável de espera, mostrar o erro de validação cru (sem enriquecimento de IA) em vez de travar a UI —
  já era princípio do projeto, mas aqui deixa de ser hipotético.
- Cache por assinatura de erro (mitigação, não requisito) reduz quantas vezes o modelo lento precisa ser
  chamado de fato pra defeitos recorrentes.
- Pro loop OFFLINE/batch de síntese de XSLT (`ai/XslSynth`, Trilha A): continua CPU-tolerante por design
  (`ia-xslt-synthesis.md` §6) — essa parte não piora com o achado de hardware, já não tinha requisito de
  latência.

## Decisão 3 — Geração sintética de dado posicional/EDI: caso à parte, fork open-source é razoável (com ressalvas)

Pedido do dono do projeto: não só trocar Gemini por Ollama-via-API pra isso — quer trazer código aberto de
verdade (clonar, ler, modificar, manter como fork da equipe) especificamente pra geração sintética.

**A distinção que ele fez é válida, não é capricho.** O motivo de rejeitar fine-tuning na Decisão 2 (dado
fiscal, precisa de verificador determinístico, "95% certo" não serve) não se aplica aqui: geração sintética
de dado de TESTE não é validada contra XSD/SEFAZ, "decorar padrão" só é problema quando o dado sai pra nuvem
(que era o problema real do Gemini) — rodando 100% on-prem, mimetizar estrutura sem replicar conteúdo real é
comportamento desejado, não risco. Está coerente treinar/ajustar algo aqui mesmo com o RAG-não-fine-tuning
valendo pro caso fiscal.

**Ressalva que precisa entrar no design mesmo assim (não é sobre nuvem, é sobre memorização):** treinar
qualquer coisa em cima de documento real cria risco de o gerador "decorar" e regurgitar um CNPJ/valor/nome
real verbatim num output rotulado "sintético". Isso não vaza pra nuvem, mas pode vazar de outro jeito: dado
"sintético" tende a circular mais solto que dado real controlado (compartilhar com avaliador de TCC, usar em
ambiente de demo, anexar num apêndice de trabalho acadêmico) — o rótulo "sintético" cria falsa sensação de
segurança se o gerador só copiou o real. Recomendo: qualquer pipeline novo aqui incluir uma checagem de
near-duplicate entre saída gerada e o corpus real de treino, não só confiar que "é sintético logo é seguro".

**Reformulação que vale considerar antes de escolher ferramenta:** o domínio aqui (campo posicional de
largura fixa) já tem o schema 100% declarado no Layout XML (`StartValue`/`LengthField`/`AlignmentType`/
`IsRequired`) — não é o problema genérico de "tabela relacional com schema desconhecido" que SDV/CTGAN/GAN
resolvem. Faz sentido separar dois sub-problemas antes de comprometer com uma ferramenta pesada:
1. **Valor por campo realista** (CNPJ/CPF/CEP/data/moeda brasileiros com formato correto) — biblioteca tipo
   **Faker** (MIT, tem provider `pt_BR` com CPF/CNPJ) ou **Mimesis** já resolve isso, é pequena, madura,
   fácil de fork/adaptar, risco de licença/manutenção baixo. Pode já cobrir boa parte do que os prompts do
   Gemini pediam ("CNPJ 14 dígitos", "nomes reais de empresa", formato de data).
2. **Coerência entre campos/linhas** (o que o loop de geração incremental do Gemini tentava aproximar) — só
   vale a pena buscar ferramenta pesada AQUI se o nível 1 sozinho não bastar na prática. Pra isso decidir,
   preciso saber: **pra que serve esse dado sintético no fim — fixture de teste/QA automatizado, ou
   enriquecimento do corpus RAG da síntese de XSLT?** Não sei essa resposta e ela muda o quanto vale
   investir em fidelidade estatística. Pelo que vi em `DataGenerationController`/`SyntheticDataGeneratorService`
   (recebe `ExcelDataContext`, sugestão de mapeamento), parece ser geração de fixture a partir de uma
   planilha do analista — isso pede menos fidelidade estatística profunda que enriquecer corpus de RAG.

**Candidatos pesquisados nesta sessão pro nível 2 (se de fato precisar) — com ressalva de licença/saúde do
projeto, não é escolha fechada:**
- **SDV (Synthetic Data Vault)** — o mais citado/usado historicamente, mas **a licença migrou de MIT pra
  Business Source License** — não bate mais com o critério "Apache/MIT" que o dono do projeto pediu. Não
  usar o pacote principal sem reler os termos da BSL com atenção.
- **CTGAN** (repo próprio `sdv-dev/CTGAN`) — preciso confirmar se manteve licença própria separada da
  migração do SDV principal antes de considerar — não confirmei isso nesta busca.
- **SynthCity** — aparece como alternativa comparável em estudo acadêmico recente; não confirmei licença.
- **Gretel/gretel-synthetics** — **Gretel foi adquirida pela NVIDIA em 2025** — sinal de risco de abandono
  pro open-source deles (empresa adquirida tende a redirecionar o projeto pro roadmap comercial do
  comprador) — não é o tipo de "saúde de projeto" que eu recomendaria apostar pra manter fork de longo prazo.
- **GReaT / REaLTabFormer** (LLM pequeno tipo GPT-2 fine-tunado sobre linha tabular serializada como texto) —
  conheço de antes do meu corte de conhecimento (não confirmado nesta busca, procurar status/licença atual
  antes de comprometer) — é o que mais se pareceria com "trazer o código e treinar nossa versão" que o dono
  do projeto está pedindo, por ser um modelo pequeno o bastante pra treinar de verdade.

## ⚠️ Treinar/fine-tunar (mesmo modelo pequeno) também precisa de GPU — não só inferência

Achado de hardware acima vale ainda mais forte aqui: TREINAR (mesmo um modelo pequeno tipo GReaT-style) é
mais pesado que INFERIR o mesmo modelo. Fazer isso CPU-only é impraticável pra qualquer coisa além de um
brinquedo. Isso reforça a pergunta (a) acima: sem GPU em algum lugar do pipeline (nem que seja modesta,
8-12GB), a ambição de "treinar nossa versão" do item de geração sintética também fica bloqueada, não só o
diagnóstico XSD. Dentro da própria família SDV, vale notar: `GaussianCopulaSynthesizer` é estatística
clássica (não rede neural), roda bem em CPU — se optar por algo desse estilo (respeitada a ressalva de
licença acima) em vez de CTGAN/TVAE (redes neurais, querem GPU), o custo de treino em CPU deixa de ser
bloqueio.

## ⚠️ Reconciliação 2026-07-21 (rodada seguinte) — "IA fiscal especializada" não exige treinar nada

O dono do projeto respondeu ao propósito do dado sintético: não é fixture-de-teste OU corpus-RAG — é
"criar uma IA específica em documentos fiscais" (a visão de `ia-fiscal-diagnosis-vision.md`). Isso pareceria
empurrar pra nível 2 (modelagem generativa mais pesada) — mas colide de frente com o hardware real
confirmado acima (Haswell 2014, sem GPU): treinar qualquer coisa aí é ainda mais inviável que só inferir.

**Reconciliação:** dá pra perseguir "IA fiscal especializada" só com RAG bem alimentado, sem treinar nada —
e isso não é meio-termo forçado, é o mesmo princípio arquitetural que o projeto inteiro já adota (RAG + loop
de auto-correção, não fine-tuning). O papel do dado sintético muda: não é "exemplo de treino pra ajustar
peso de modelo", é "entrada nova pro índice de recuperação (RAG) que um modelo pequeno genérico consulta em
tempo de inferência". Concretamente: gerar cenários fiscais rotulados (ex.: "devolução com CFOP de venda,
incorreto" / "venda com CFOP de venda, correto") usando Faker/Mimesis (nível 1) + variação deliberada de
cenário, indexar isso, e deixar o modelo pequeno (o mesmo do diagnóstico XSD) recuperar exemplos relevantes
na hora de explicar uma divergência de CFOP. Não precisa de GPU, não precisa de treino.

**Trade-off honesto a comunicar de volta, não esconder:** RAG-sem-treino tem teto diferente de fine-tuning
de verdade. Pra CFOP especificamente isso não importa (é lookup de tabela, RAG resolve bem, não falta
"generalização"). Mas se a visão de "IA fiscal especializada" um dia crescer além de validação tipo-lookup
pra julgamento mais fuzzy sobre documentos fiscais em geral, RAG-sem-treino tem teto de qualidade mais baixo
que fine-tuning — e fine-tuning vai continuar precisando de GPU que hoje não existe. Não é bloqueio agora
(o escopo confirmado — CFOP — não precisa disso), mas é limitação real, não hipotética, a sinalizar se a
ambição crescer.

## How to apply

- Ao especificar o novo serviço Ollama de diagnóstico (itens 3-5): citar a Decisão 2 como porquê de não
  propor fine-tuning; usar a recomendação de hardware confirmada acima (1-2B, medir de verdade, não
  extrapolar).
- Ao especificar a geração sintética (Decisão 3): não tratar como "mesma decisão" do diagnóstico XSD — é
  caso à parte; mas com o hardware real confirmado, tratar como RAG-enrichment (ver reconciliação acima),
  não como treino de modelo. Verificar licença atual de qualquer biblioteca antes de clonar (SDV mudou pra
  BSL recentemente, não confiar em conhecimento antigo de que era MIT).
