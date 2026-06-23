---
description: Regras de uso e gestão do MCP Server do LayoutParser API.
---

# MCP Usage — LayoutParser API

## Resumo

- Prefira **ferramentas nativas** do Claude Code (Read/Edit/Grep/Glob/Bash) para operações de arquivo e busca.
- O **MCP Server (C#)** em `mcp/LayoutParserMcp/` existe para expor **operações de domínio da API** (parse, catálogo de layouts/mappers, transformação) como *tools* para agentes — não para substituir as tools nativas.
- **Gestão de MCP é exclusiva do `@lp-devops`** (registro em `.mcp.json`, build, configuração).

## Arquitetura do MCP

O MCP Server é um **cliente fino sobre a API HTTP** — a API continua sendo a fonte da verdade:

```
Agente/LLM  ──(MCP stdio)──►  LayoutParserMcp (C#)  ──(HTTP)──►  LayoutParserApi
```

- Não duplique lógica de negócio no MCP; ele apenas chama os endpoints.
- Configure a base URL da API via env var `LAYOUTPARSER_API_URL` (default `http://localhost:5000`).

## Quando usar

| Situação | Ferramenta |
|----------|-----------|
| Ler/editar código do repo | Tools nativas (Read/Edit/Grep) |
| Parsear um documento via API | Tool MCP `parse_document` |
| Listar layouts/mappers do catálogo | Tool MCP `list_layouts` / `get_layout` |
| Gerar/validar transformação | Tool MCP `generate_transformation` |

## Regras

- Não exponha via MCP nenhuma operação que vaze segredos ou dados sensíveis sem necessidade.
- Toda tool nova precisa de `Description` clara (em PT/EN) e schema tipado.
- Detalhe de setup: [`mcp/LayoutParserMcp/README.md`](../../mcp/LayoutParserMcp/README.md).
