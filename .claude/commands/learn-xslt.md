---
description: Investiga/evolui o loop de geração autônoma de XSLT (RAG → gerar → validar → corrigir).
allowed-tools: Read, Grep, Glob, Edit, Bash, WebSearch
---

# /learn-xslt

Trabalhe na **visão central do projeto**: gerar XSLT sozinho a partir do trio
**TXT → XML low-code → XML final** (ver [`README.md`](../../README.md) §5).

## Mapa do código relevante

- Geração: `Services/Transformation/ImprovedXslGeneratorService.cs`, `ImprovedTclGeneratorService.cs`
- Aprendizado: `Services/Transformation/TransformationLearningService.cs`, `Services/Learning/`
- RAG: `Services/Generation/Implementations/RAGService.cs`
- Validação: `Services/XmlAnalysis/XsdValidationService.cs`, `Services/Testing/AutomatedTransformationTestService.cs`
- LLM: config `Ollama`/`Gemini` em `appsettings.json`

## O loop alvo

```
1. RETRIEVE   recuperar exemplos (layout→XSLT) mais similares
2. GENERATE   Ollama/Llama gera XSLT candidato (few-shot)
3. VALIDATE   aplicar XSLT → comparar com XML final esperado (XSD + diff)
4. CORRECT    realimentar erros no prompt → repetir até convergir
```

## Tarefa

A partir do pedido do usuário (**$ARGUMENTS**), identifique em qual etapa do loop
mexer, proponha a mudança mínima, implemente e **valide a saída contra o XML esperado**.

- Prefira **Ollama local** para dados sensíveis; nuvem só com autorização.
- Reporte métricas honestas (taxa de campos corretos, erros de XSD restantes).
- **Não** declare sucesso sem validação automática passando.
