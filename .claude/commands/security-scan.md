---
description: Varre o repositório por segredos versionados e gera plano de remediação.
allowed-tools: Grep, Read, Glob, Bash
---

# /security-scan

Faça uma varredura de segurança focada em **segredos versionados** neste repositório.

## Passos

1. Use `Grep` para procurar padrões sensíveis em arquivos de configuração e código:
   - `ApiKey`, `Password`, `ConnectionString`, `Secret`, `Token`, `Bearer`
   - chaves de provedores: `AIza` (Google/Gemini), `sk-` (OpenAI), `eyJ` (JWT)
   - foque em `appsettings*.json`, `*.config`, `*.cs`, `*.env*`, `Dockerfile`
2. Para cada achado, classifique: **comprometido / placeholder / falso-positivo**.
3. Cheque se o arquivo está versionado (`git ls-files`) e se há ocorrência no histórico
   (`git log -p -S "<trecho>" -- <arquivo>`), reportando se o segredo persiste em commits antigos.
4. Produza um **plano de remediação** seguindo [`.claude/rules/security.md`](../rules/security.md):
   rotacionar, migrar para user-secrets/env, placeholders, `.gitignore`, limpeza de histórico.

## Saída

- Tabela: `segredo | arquivo:linha | severidade | versionado? | no histórico?`
- Plano de ação numerado, marcando o que exige `@lp-devops`.

**Não** edite arquivos nesta varredura — apenas reporte. Não exiba os valores completos dos segredos (mascare).

Alvo opcional (pasta/arquivo): $ARGUMENTS
