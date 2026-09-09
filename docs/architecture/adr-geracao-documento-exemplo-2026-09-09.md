# ADR — Geração de documento de exemplo a partir de layout Sysmiddle (2026-09-09)

## Status

Proposto. Sem implementação — este documento é o desenho, a implementação fica para
`@lp-backend-dev`/`@lp-parser-llm` em fases separadas (ver §7).

## Contexto

O dono do produto trouxe uma POC (Python, fora do repo) que anda a árvore de um `LayoutVO`
tipo `Xml` (`GroupTagElementVO`/`TagElementVO`/`AttributeElementVO`, respeitando `Sequence` e
`MinimalOccurrence`/`MaximumOccurrence`) e gera um XML de exemplo sintético a partir de um
layout real (`LAY_MIDAS_NFSE`, 197 elementos). A prova de conceito funcionou: 196 elementos
gerados com estrutura correta, arquivos em
`.claude/temp/layout/LAY_cfb9315d-1d1c-4b1e-b30d-912892c0bb0d.xml` (layout) e
`LAY_cfb9315d-1d1c-4b1e-b30d-912892c0bb0d_EXEMPLO_GERADO.xml` (saída).

Limitações já identificadas na POC:
1. Valores são placeholders (`EXEMPLO_NOME`, `COD1095`) sem validação de negócio — CPF/CNPJ
   sem dígito verificador (DV) confiável.
2. Sem coerência entre campos relacionados (datas, valores).
3. Só cobre layout `Xml` — layout `TextPositional` (TXT/MQSeries/IDOC) precisa de lógica
   diferente (preencher por posição/largura fixa, não por elemento em árvore).

A pergunta do dono: vale virar feature de produto — gerar documento de exemplo sob demanda a
partir de um layout, para qualquer um dos dois tipos.

## Achado central: já existe um serviço de geração no repo, mas ele NÃO cobre o caso da POC

`Services/Generation/Implementations/SyntheticDataGeneratorService.cs` já existe, registrado
via `ISyntheticDataGeneratorService`. Ele:

- Opera sobre `Layout.Elements` como lista de `LineElement`/`FieldElement` (JSON serializado por
  linha), preenchendo por largura fixa (`PadLeft`/`PadRight`) — **é o modelo do layout
  posicional** (`LineElement.Elements` = campos de uma linha de largura fixa), não o modelo em
  árvore de tags/atributos que a POC do dono percorre.
- Já tem um caminho de IA morto e deliberadamente desligado: `request.UseAI` é aceito no
  contrato mas ignorado — comentário no código explica que a geração via Gemini/OpenAI foi
  removida no decommission de 2026-08-10 (mando de nuvem para dado de layout/amostra). Hoje é
  100% geração por regra, coerente com [[gemini-openai-decommission-decision]] (Decisão 3: RAG-
  não-treino também vale aqui, mas por motivo de custo/hardware, não por vazamento de nuvem —
  esse caminho já não existe).
- `GenerateCnpj()`/`GenerateCpf()` têm o comentário `// Gerar CNPJ/CPF válido sinteticamente`,
  mas **isso é falso hoje** — são dígitos aleatórios com padding, sem cálculo real de DV
  (`_random.Next(...) + "0001" + _random.Next(10,99)`, sem módulo 11). É a mesma limitação que a
  POC do dono tem, só que o comentário do código esconde isso. Achado a corrigir independente
  da decisão sobre esta feature.

**Não existem, em nenhum lugar do repo C#, classes/DTOs para `GroupTagElementVO`,
`TagElementVO` ou `AttributeElementVO`** (`grep` vazio em `Models/`/`Services/`) — confirmado
nesta sessão. O layout tipo `Xml` chega à API como XML bruto e não tem um modelo tipado
equivalente ao que existe para `LineElement`/`FieldElement` do tipo posicional. Isso significa
que **cobrir layout `Xml` é capacidade genuinamente nova**, não extensão de código existente —
precisaria de um parser de árvore (deserializar o `LayoutVO` XML para uma estrutura navegável,
ou reaproveitar o parser de layout que a API já usa em runtime para outro fim, se existir um
caminho C# que já entende esse schema — a checar por `@lp-backend-dev` antes de escrever um
parser do zero).

## Decisão 1 — Vale a pena virar feature, com correção de escopo

Sim, mas o valor real está em dois pontos que a pergunta original não separava:

1. **CPF/CNPJ com DV matematicamente válido** é ganho barato e desacoplado de tudo — algoritmo
   público (módulo 11), não precisa de biblioteca pesada, corrige tanto o serviço já existente
   quanto qualquer geração nova. Isso muda o cálculo de "dado sintético rejeitado por Pollux"
   registrado em [[gemini-openai-decommission-decision]] (nota histórica: "Pollux rejeita por
   chave/DV/CNPJ inválido") — mas só parcialmente, ver ressalva abaixo.
2. **Cobertura de layout `Xml` em árvore** (o que a POC realmente entrega) é uma capacidade nova
   que o serviço existente não tem — vale a pena, mas é trabalho de parser novo, não um ajuste.

**Ressalva honesta sobre o ponto 1 e o backfill (#151/#352):** DV válido resolve rejeição por
"CNPJ/CPF matematicamente inválido", mas não resolve **coerência entre campos** (data de emissão
vs. data de prestação de serviço, série vs. número, CFOP vs. natureza da operação) nem
**existência real** (um CNPJ com DV válido pode não existir/não estar ativo perante SEFAZ, e
Pollux ou validações downstream fiscais podem checar isso). Não prometo que DV válido sozinho
destrava o backfill de #351/#352 — é necessário, não suficiente. O veredito "backfill em lote não
viável" do ADR anterior continua válido para os casos que dependiam de identidade fiscal real
verificável; passa a ficar mais plausível apenas para os casos onde o gate de rejeição era
puramente estrutural (DV), não semântico/fiscal.

## Decisão 2 — Risco de memorização/vazamento (checagem explícita pedida)

O risco registrado em [[gemini-openai-decommission-decision]] ("dado sintético pode decorar e
regurgitar valor real se treinado em cima de documento real") **não se aplica a esta feature**,
e a razão precisa ficar explícita, não assumida: tanto a POC quanto o serviço existente geram
valor por **regra determinística** (algoritmo/template), não por modelo treinado sobre
documentos reais. Não há passo de treino, não há corpus de documento real sendo consumido para
ajustar peso de nada. O único elemento potencialmente sensível seria usar valores reais de um
documento existente como *seed*/exemplo de formato (ex.: "olhar o range de CNPJs do cliente X
para gerar algo plausível") — **isso não está no desenho aqui e não deve entrar** sem nova
decisão explícita. Enquanto a geração for 100% de regra sobre a estrutura do layout (não sobre
conteúdo de instância real), o risco de memorização é inaplicável por construção.

## Decisão 3 — Onde a feature entra no produto

**Endpoint novo**, não reaproveitar os endpoints de `DataGenerationController` como estão hoje
(eles giram em torno de `ExcelDataContext` — planilha do analista como fonte, cenário diferente
do "gerar a partir só do layout"). Proposta:

```
POST /api/layouts/{layoutGuid}/generate-sample
  body: { numberOfRecords?: int (default 1), seed?: int }
  response: { generatedDocument: string, format: "xml" | "positional", warnings: string[] }
```

- `warnings` carrega avisos honestos no payload (ex.: "CPF gerado tem DV válido mas não
  corresponde a pessoa real", "campos sem correlação semântica entre si") — evita que o
  consumidor (humano ou processo de backfill) trate a saída como mais confiável do que é.
- **Quem dispara:** primariamente o usuário via UI (botão "gerar exemplo" na tela de
  layout/mapper, mesma área onde já existe visualização de layout no React) — não é uso
  interno-only. Uso interno para alimentar #351/#352 é possível **como consumidor do mesmo
  endpoint**, não como capacidade paralela — evita duas implementações divergentes do mesmo
  algoritmo de geração.
- **Reaproveitamento de `SyntheticDataGeneratorService`:** sim, para o caminho `TextPositional`
  — a lógica de `PadLeft`/`PadRight` por `FieldElement` já é o desenho certo, só precisa da
  correção de DV (§Decisão 1). Para o caminho `Xml`, um novo serviço
  (`IXmlSampleDocumentGeneratorService` ou equivalente) que percorre a árvore do `LayoutVO`
  (a lógica da POC), reaproveitando os mesmos geradores de valor por tipo de campo (CPF/CNPJ/
  data/decimal) já presentes no serviço existente — extrair esses geradores de valor para um
  componente compartilhado (`IFieldValueGenerator`) usado pelos dois caminhos, em vez de
  duplicar `GenerateCnpj`/`GenerateCpf`/`GenerateDate` num segundo arquivo.

## Decisão 4 — Cobertura dos dois tipos de layout

| Tipo | Estado hoje | O que falta |
|------|-------------|-------------|
| `Xml` (`GroupTagElementVO`/`TagElementVO`/`AttributeElementVO`) | Nenhum modelo C# tipado; só a POC Python fora do repo | Parser/deserializador da árvore do `LayoutVO` em C# (novo); percorrer respeitando `Sequence`/`MinimalOccurrence`/`MaximumOccurrence` (mesma lógica da POC); serializar como XML válido |
| `TextPositional` (`LineElement`/`FieldElement`) | `SyntheticDataGeneratorService` já cobre a estrutura (largura fixa, `PadLeft`/`PadRight`) | Só a correção de DV (§Decisão 1); resto já funciona |

O caminho `Xml` é o esforço maior e novo; o caminho posicional é essencialmente uma correção
pontual em código que já existe. Isso muda a ordem de prioridade recomendada (§7): entregar o
ganho barato (DV real) primeiro, sem esperar o parser de árvore XML.

## Decisão 5 — Conexão com backfill/#352: capacidade independente, integração opcional

Não acoplar o endpoint de geração de exemplo ao pipeline de backfill/#352 na primeira fase.
Motivos: (a) o gate real do backfill não é só "falta de CPF com DV válido", é presença de
instância real compatível com o mapper — ver Decisão 1; (b) acoplar cedo demais criaria
dependência de uma feature ainda não validada em produção (geração de exemplo) dentro de um
pipeline já crítico (métricas de convergência real, issue #352). Depois que a geração de exemplo
estiver estável e o dono validar a qualidade dos exemplos em uso real de UI, avaliar de novo se
vale conectar como fonte adicional de instância sintética rotulada para os casos onde
`RepairBatchRunner` hoje reporta "sem instância real compatível" — mas como decisão separada,
não pré-comprometida aqui.

## 7. Fases recomendadas

1. **Fase 1 (barata, isolada):** corrigir `GenerateCnpj`/`GenerateCpf` em
   `SyntheticDataGeneratorService.cs` para DV real (módulo 11, algoritmo público) — sem mudar
   contrato, sem endpoint novo. Corrige também o comentário enganoso no código.
2. **Fase 2:** endpoint novo `POST /api/layouts/{guid}/generate-sample` cobrindo só
   `TextPositional`, reaproveitando o serviço existente + a correção da Fase 1.
3. **Fase 3:** parser de árvore para layout `Xml` (novo serviço), estendendo o mesmo endpoint
   para o segundo formato — maior esforço, replica a lógica da POC em C#.
4. **Fase 4 (opcional, decisão separada):** avaliar integração com backfill/#151/#352 depois de
   a feature estar validada em uso real via UI — não pré-comprometer agora.

## Riscos e trade-offs

- **DV válido != documento fiscalmente válido.** Não superprometer ao dono que a Fase 1 resolve
  rejeição de Pollux/SEFAZ — resolve só a camada estrutural.
- **Duplicar geradores de valor entre os dois caminhos** é o principal risco técnico de design
  se a Fase 3 não reaproveitar a Fase 1/2 via componente compartilhado — sinalizado em
  Decisão 3, cabe ao `@lp-backend-dev` garantir isso na implementação.
- **Sem parser C# do schema `Xml` hoje** significa que a Fase 3 é o item de maior incerteza de
  esforço deste ADR — recomendo um spike curto antes de comprometer prazo.

## Próximos passos (fora do escopo deste agente)

- `@lp-pm`: avaliar abertura de issues de implementação para as Fases 1–3 (não criadas aqui).
- Comentar na issue #151 conectando esta linha de investigação (geração de exemplo como
  possível insumo futuro de corpus de instância) — recomendado, decisão de conectar de fato
  fica para depois da Fase 3 validada (Decisão 5).
