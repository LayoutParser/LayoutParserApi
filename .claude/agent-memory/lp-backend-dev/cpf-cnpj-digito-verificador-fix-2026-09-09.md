---
name: cpf-cnpj-digito-verificador-fix-2026-09-09
description: SyntheticDataGeneratorService.GenerateCnpj/GenerateCpf mentiam "válido" no comentário mas não calculavam dígito verificador — fix aplicado
metadata:
  type: project
---

Bug encontrado pelo `@lp-architect` (ADR `docs/architecture/adr-geracao-documento-exemplo-2026-09-09.md`):
`Services/Generation/Implementations/SyntheticDataGeneratorService.cs` tinha `GenerateCnpj()`/
`GenerateCpf()` com comentário afirmando gerar CPF/CNPJ "válido", mas só preenchia dígitos
aleatórios com `PadLeft` — sem dígito verificador (módulo 11) nenhum. Fix aplicado em
`fix/cpf-cnpj-digito-verificador-real` (branch a partir de `develop`, não commitada em cima de
`master`): 9 (CPF) / 12 (CNPJ) primeiros dígitos continuam aleatórios, mas os 2 dígitos
verificadores agora são calculados de verdade (`CalculateCpfCheckDigit`/`CalculateCnpjCheckDigit`,
métodos privados estáticos no mesmo arquivo). Assinatura pública e retorno (string) inalterados.

**Why:** dado sintético gerado por essa classe alimenta o pipeline de teste/geração de documento
de exemplo — CPF/CNPJ com dígito verificador falso pode invalidar XSD/regras de negócio a jusante
de forma silenciosa, e o comentário mentindo no código é o tipo de coisa que engana o próximo dev
que confiar nele sem reler a implementação.

**How to apply:** se aparecer outro gerador de campo "validado" (ex.: chave de acesso de NFe,
IE) que só faz padding de dígitos aleatórios, checar se o comentário promete validação real —
o padrão aqui (métodos privados são private, então o teste precisa passar pela API pública
`GenerateFieldValueAsync(field, context, dataType)` com `dataType` explícito tipo `"cpf"`/`"cnpj"`,
já que `InferFieldType` prioriza o parâmetro `dataType` sobre o nome do campo) serve de modelo:
teste com validação independente (não reaproveitar a lógica testada) rodando N iterações pra não
ser coincidência de RNG.
