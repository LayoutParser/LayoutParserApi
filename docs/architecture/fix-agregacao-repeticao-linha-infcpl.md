# Fix — Agregação de repetição posicional de linha (`IsPositionalGroupRepetition`)

> Design de correção para a issue #37. Escrito por `@lp-architect` (Aria) a partir da investigação
> de `@lp-parser-llm` (Lia) — ver `.claude/agent-memory/lp-parser-llm/line-repetition-position-bug-resolved.md`.
> **Não implementado.** Entregável executável por `@lp-parser-llm`.

## 1. O bug, em uma frase

`LINHA081` do layout `LAY_TXT_MQSERIES_ENVNFE_4.00_NFe.xml` tem `IsPositionalGroupRepetition=true` e
forma o campo `infCpl` da NF-e concatenando **N ocorrências físicas** da mesma linha no documento
posicional — mas o parser (`ParseLineFields`, `Services/Implementations/LayoutParserService .cs:808`)
nunca lê essa flag; cada ocorrência vira um `ParsedField` independente (`Occurrence=1,2,3,4...`),
nunca agregado num valor lógico único.

## 2. Onde agregar: no parser, não em pós-processamento

**Decisão: agregar dentro de `LayoutParserService`, no momento em que os `ParsedField`s da linha são
produzidos — não numa camada posterior.**

| Opção | Onde | Trade-off |
|-------|------|-----------|
| **A — no parser (recomendada)** | `ParseTextWithSequenceValidation` / `ParseLineFields`, logo após todas as ocorrências de uma `LineElement` marcada serem coletadas | `ParsedField` já sai do parser como fonte da verdade única — qualquer consumidor (controllers, futura camada de transformação XSLT/TCL, `Services/Testing`) recebe o valor correto sem precisar saber da regra de agregação. Acoplamento fica **dentro** do parser, que já é o dono da leitura posicional — coerente com "SQL/parser é fonte da verdade". |
| B — pós-processamento (camada nova antes do XML/JSON final) | Um passo separado depois de `ParseTextWithSequenceValidation` | Evita mexer no método já denso de `ParseLineFields`, mas **duplica o conhecimento de "qual linha agrega"** em dois lugares (parser decide ocorrência, pós-processamento decide concatenação) e cria uma janela onde `result.ParsedFields` existe em dois formatos possíveis dependendo de em que ponto do pipeline alguém olha. Isso é o tipo de acoplamento frágil que o projeto já sofreu com o Redis/cache — preferimos não repetir. |

Não existe hoje nenhum consumidor fora do parser (grep confirmou: nenhum controller ou serviço em
`Services/` monta XML/JSON final a partir de `ParsedFields` fazendo `GroupBy` por campo — ver §4).
Isso remove o principal argumento a favor da Opção B (evitar quebrar um consumidor que já espera
fragmentado) e reforça a Opção A: **não há consumidor confirmado dependendo do formato fragmentado
hoje**, mas ver §4 sobre o risco de consumidor externo ao repo.

## 3. Como identificar e ordenar

- **Trigger:** `LineElement.IsPositionalGroupRepetition == true` (já desserializado, `XmlLayoutLoader.cs:97`).
- **Ordenação:** pela ordem física de leitura do documento — ou seja, a ordem em que
  `ParseTextWithSequenceValidation` já processa as linhas do texto posicional (sequencial, de cima
  para baixo). Isso coincide com `occurrenceIndex` crescente (`ParseLineFields` é chamado com
  `currentOccurrence` incremental — `LayoutParserService .cs:340-347`). **Não** ordenar por
  `Occurrence` depois de coletado — usar a ordem de chegada evita reordenar caso o documento algum
  dia tenha ocorrências fora de sequência (não deveria, mas não custa não depender disso).
- **Limite:** `MaximumOccurrence` já é respeitado pelo loop existente (`currentOccurrence < maxOccurrences`,
  linha 345) — não precisa de lógica nova de limite, só reaproveitar o que já corta a leitura.
- **Concatenação por campo, não por linha inteira:** cada `FieldElement` dentro da `LineElement`
  repetida (ex.: os campos que compõem `infCpl`) deve ter seus valores concatenados
  posição-a-posição entre ocorrências — **não** concatenar a linha bruta. Ou seja: valor final do
  campo X = `valor(X, ocorrência 1) + valor(X, ocorrência 2) + ... + valor(X, ocorrência N)`.
- **Separador: nenhum (concatenação pura), com uma ressalva.** O padrão de campo posicional de
  largura fixa preenche com espaços à direita (`ApplyAlignment`, linha 893) — se cada ocorrência
  contém um fragmento de texto livre pré-preenchido/truncado no tamanho do campo, concatenar puro
  reproduz o texto original sem gap. **Confirmar contra o par gabarito real** (documento produção +
  XML esperado, mencionado na investigação da Lia) antes de assumir "sem separador" como definitivo —
  é a única forma de saber se o layout já embute espaços de continuação na própria ocorrência ou se
  espera um separador explícit (`\n`, espaço único) entre elas. Se não houver acesso ao gabarito no
  momento da implementação, **trate como concatenação pura e sinalize a suposição no PR**, não decida
  silenciosamente.
- **Trim:** aplicar o mesmo `ApplyAlignment`/trim que já existe por ocorrência antes de concatenar,
  não trim único no resultado final (evita perder espaços internos legítimos entre fragmentos, se
  eles existirem).

## 4. Escopo: genérico, não específico ao `infCpl`

**Recomendação: correção genérica.** `IsPositionalGroupRepetition` é um campo de `LineElement`, não
tem nenhuma referência a `infCpl` ou NF-e no schema — tratar como propriedade universal do modelo é
consistente com o resto do parser (que não tem nenhuma lógica hardcoded por nome de campo fiscal,
ver `ParseLineFields` — tudo é dirigido por config do XML de layout). Implementar restrito a
`LINHA081`/`infCpl` criaria uma exceção não documentada no meio do parser genérico, que é exatamente
o tipo de acoplamento implícito que dificultou a investigação desta issue (a flag existe desde
sempre, mas ninguém sabia o que ela fazia).

**Peço confirmação explícita de `@lp-parser-llm`** sobre um ponto: a investigação encontrou
`IsPositionalGroupRepetition=true` em exatamente 1 `LineElement` no layout de produção auditado.
Não há uma segunda instância real para validar que a regra de agregação (concatenação campo-a-campo,
sem separador) generaliza corretamente. Se a Lia, no domínio do parsing, tiver contexto de outro
layout com a flag ativa (ou souber que ela é usada em outros clientes/layouts fora do repo), isso
deve informar se a regra abaixo (§3) precisa de um parâmetro extra (ex.: separador configurável) já
na primeira implementação, ou se pode ficar hardcoded como "sem separador" até aparecer um segundo
caso real.

## 5. Risco de regressão

1. **Consumidor externo ao repo pode já compensar a fragmentação.** O BFF (`feat/identidade-do-bff`)
   ou qualquer consumidor downstream da NF-e pode já estar concatenando os 4 fragmentos de `infCpl`
   manualmente do lado dele, tratando `Occurrence=1..4` como já é hoje. **Não há como confirmar isso
   a partir deste repo** — sinalizo o risco, mas a ação é: `@lp-parser-llm`/`@lp-backend-dev` avisar
   quem mantém o consumidor (BFF ou outro) antes de mergear, para não duplicar a concatenação
   (resultado: `infCpl` dobrado/corrompido) ou quebrar silenciosamente um workaround já em produção.
2. **Quebra de contagem em `ValidateLineOccurrences`.** Hoje `actualOccurrences` é calculado via
   `GroupBy(f => f.Occurrence).Count()` (linha 441) — se a agregação colapsar as N ocorrências num
   único `ParsedField` por campo, essa validação de `MinimalOccurrence`/`MaximumOccurrence` da linha
   pode passar a contar "1" em vez de "N", disparando falso positivo de "abaixo do mínimo". A
   implementação precisa decidir: manter os `ParsedField`s de cada ocorrência intactos (para
   validação) e **adicionar** um campo agregado adicional, ou ajustar `ValidateLineOccurrences` para
   ler `MaximumOccurrence` como "occurrences físicas esperadas" independente da agregação. Recomendo
   a primeira opção (aditiva) — não remove nada que já funciona, só acrescenta o valor lógico
   correto.
3. **Teste de regressão obrigatório antes do merge:** rodar o parse contra o par gabarito real
   (documento MQSeries produção + XML esperado, citado na investigação) e comparar `infCpl` byte a
   byte. Se `Services/Testing` já tem suíte de regressão de layout, adicionar este caso como fixture
   permanente — protege contra reintrodução do bug.

## 6. Plano de implementação (para `@lp-parser-llm`)

1. Em `ParseTextWithSequenceValidation` (`LayoutParserService .cs:247`), ao processar uma
   `matchingLineConfig` com `IsPositionalGroupRepetition == true`: **não** alterar o loop de
   ocorrências existente (linhas 340-347, já correto) — deixar `ParseLineFields` continuar criando
   um `ParsedField` por campo por ocorrência, como hoje (necessário para §5.2).
2. Depois que o loop principal termina (ponto único, ~linha 388, antes ou logo após
   `ValidateLineOccurrences`), adicionar um passo de agregação:
   - Para cada `LineElement` com `IsPositionalGroupRepetition == true` presente em `parsedFields`:
     agrupar por `FieldName`, ordenar por `Occurrence` ascendente, concatenar `Value` (ver regra de
     separador em §3 — validar contra gabarito).
   - Anexar um novo `ParsedField` (ou marcar um existente) representando o valor agregado. Definir
     como o consumidor distingue "fragmento" de "valor agregado" — sugestão: `Occurrence = 0` para o
     agregado, mantendo 1..N para os fragmentos físicos (não quebra `ValidateLineOccurrences`, que
     já filtra por `Occurrence` em grupos).
3. Validar contra o par gabarito (documento real + XML esperado) — diff byte a byte do `infCpl`
   resultante.
4. Adicionar teste de regressão permanente em `Services/Testing` (ou no projeto de testes) cobrindo
   este layout/linha especificamente.
5. Confirmar com o mantenedor do BFF/consumidor da NF-e se ele já compensa a fragmentação, antes do
   merge (ver §5.1) — coordenar para não duplicar `infCpl`.
6. Atualizar a memória da Lia (`.claude/agent-memory/lp-parser-llm/`) com o resultado e fechar a
   issue #37 apontando o commit/PR.

## 7. Resumo para a issue

Onde: agregação dentro do parser (`LayoutParserService`), não em pós-processamento — sem consumidor
confirmado do formato fragmentado hoje. Trigger: `IsPositionalGroupRepetition`, ordem física de
leitura, concatenação campo-a-campo sem separador (a confirmar contra gabarito real). Escopo:
genérico para qualquer `LineElement` marcado, não hardcoded a `infCpl`. Risco principal: consumidor
externo (BFF?) pode já compensar a fragmentação — coordenar antes do merge. Ver documento completo em
`docs/architecture/fix-agregacao-repeticao-linha-infcpl.md`.
