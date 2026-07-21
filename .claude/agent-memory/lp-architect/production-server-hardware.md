---
name: production-server-hardware
description: Specs reais do servidor de produção (BRNDDAPPBLD01) onde o Ollama do diagnóstico XSD vai rodar — CPU Haswell de 2014, sem GPU confirmada, mais fraco que os benchmarks de "CPU-only" recentes assumem
metadata:
  type: project
---

Confirmado pelo usuário em 2026-07-21 (specs reais, não estimativa): servidor de produção
`BRNDDAPPBLD01` — Intel Core i7-4790 @ 3.60GHz, 32GB RAM (31,8GB usable), Windows 64-bit x64. Relatório é
do estilo "Configurações > Sistema > Sobre" do Windows, que não lista GPU nesse conjunto de campos —
ausência de linha de GPU não é prova definitiva de "sem GPU discreta", mas dado que o nome sugere servidor
de build/deploy, é a leitura mais provável (raramente se põe GPU discreta em servidor headless de build).

**Avaliação do hardware:** i7-4790 é **Haswell, lançado em 2014** — 4 núcleos / 8 threads (Hyper-Threading),
suporta AVX2 mas **não tem AVX-512**. Plataforma (LGA1150) é DDR3, provavelmente DDR3-1600, bandwidth de
memória bem inferior a qualquer coisa DDR4/DDR5 moderna. Isso importa mais que clock ou núcleos: pesquisa
desta sessão confirma que inferência de LLM em CPU é **memory-bandwidth-bound** — logo, mesmo modelo
pequeno deve rodar sensivelmente mais devagar do que os benchmarks recentes citados antes (~12 tok/s
Phi-4-mini), que provavelmente foram medidos em hardware bem mais novo (DDR4/DDR5, mais núcleos). **Não
existe número medido pra esta CPU específica** — não encontrei benchmark de Haswell/pré-AVX-512 na pesquisa
desta sessão; qualquer estimativa é extrapolação, não medição.

**Achado não confirmado, mas plausível — vale perguntar antes de finalizar:** o hostname `BRNDDAPPBLD01`
(prefixo `NDD`) bate com a nomenclatura já trackeada em [[dev-machine-infra]]/memória global do usuário
sobre o runner GitHub Actions + espelho inetpub porta 5100 (ticket NDD-NOT-10910). Pode ser a MESMA máquina
que já roda o runner de CI e o espelho de dev — se for, o Ollama vai competir por CPU/RAM com jobs de
build/CI e com a própria API, não é recurso dedicado e ocioso. **Não presumir — perguntar ao usuário antes
de finalizar qualquer recomendação que assuma capacidade dedicada.**

**Recomendações concretas dado este hardware:**
1. Mirar o menor tamanho de modelo prático (1-2B, não 2-4B) como ponto de partida — não 7B+.
2. **Medir de verdade antes de prometer qualquer UX** — baixar um candidato via Ollama nesse servidor
   específico, rodar prompt fixo, medir tok/s real. Não travar expectativa de latência em número
   extrapolado.
3. Build/config mirando AVX2 especificamente (não AVX-512, que essa CPU não tem) — ex. `LLAMA_NATIVE=ON`
   ou equivalente — melhora throughput real, ajuste barato.
4. Timeout/degradação graciosa no serviço Ollama (já era princípio do projeto) fica MAIS importante: se
   estourar um teto razoável, mostrar o erro de validação cru sem enriquecimento de IA em vez de travar a UI.
5. Cache por assinatura de erro (mesmo tipo de defeito XSD repetido entre documentos) reduz quantas vezes
   o modelo lento precisa ser chamado — mitigação de custo, não requisito obrigatório.

**Why importa:** essa CPU é a limitação real que qualquer plano de IA local (diagnóstico XSD, síntese
fiscal) precisa respeitar — o usuário confirmou "por enquanto não vamos investir em nada" (sem upgrade
previsto).

**How to apply:** nunca recomendar modelo/tamanho pro diagnóstico XSD ou geração sintética sem citar este
hardware real; nunca prometer tok/s sem medir. Ver [[gemini-openai-decommission-decision]] pra como isso
se conecta à decisão de não treinar nada localmente (treino é ainda mais inviável que inferência aqui).
