# Harness Claude Code — LayoutParser API

Este diretório configura o desenvolvimento assistido por IA do LayoutParser API.
É **enxuto e focado no domínio .NET**, inspirado no AIOX mas sem o peso do framework completo.

> **EN** · AI development harness for the LayoutParser API — a lean, .NET-focused setup
> inspired by AIOX. Agents, rules, slash commands and an optional hook.

## Estrutura

```
.claude/
├── CLAUDE.md                # Comportamento base, padrões e mapa de agentes
├── agents/                  # 6 personas enxutas (subagents)
│   ├── lp-architect.md      # Aria   — arquitetura, visão IA→XSLT (analisa, não coda)
│   ├── lp-backend-dev.md    # Dex    — implementação C#/.NET
│   ├── lp-parser-llm.md     # Lia    — parsing + Learning/RAG + geração XSLT/TCL
│   ├── lp-qa.md             # Quinn  — testes e quality gates
│   ├── lp-devops.md         # Gage   — git push (exclusivo), Docker, CI, MCP, segredos
│   └── lp-doc.md            # Duda   — documentação bilíngue
├── rules/                   # Regras carregadas por contexto
│   ├── agent-authority.md   # Quem pode o quê (push é só do devops)
│   ├── agent-handoff.md     # Compactação de contexto ao trocar de agente
│   ├── dotnet-standards.md  # Padrões .NET (carrega ao editar *.cs)
│   ├── security.md          # Pendência de segredos + regras de segurança
│   └── mcp-usage.md         # Uso/gestão do MCP Server
├── commands/                # Slash commands
│   ├── security-scan.md     # /security-scan — varre segredos
│   ├── new-endpoint.md      # /new-endpoint — cria endpoint no padrão
│   ├── learn-xslt.md        # /learn-xslt — evolui o loop de geração de XSLT
│   └── parse-trace.md       # /parse-trace — diagnostica o fluxo de parsing
├── hooks/
│   └── git-push-advisory.cjs # Lembrete não-bloqueante sobre push (Node)
├── settings.json.example    # Template de settings (idioma + permissões + hook)
└── README.md                # este arquivo
```

## Como usar

### Agentes

Invoque um agente com `@nome` ou via o tool `Task`:

```
@lp-architect  desenhe como indexar pares (layout→XSLT) num vector store
@lp-parser-llm implemente o retrieve do RAG usando esse desenho
@lp-qa         valide a transformação gerada contra o XML esperado
@lp-devops     faça push quando o build estiver verde
```

Fluxo típico:

```
@lp-architect → @lp-backend-dev / @lp-parser-llm → @lp-qa → @lp-doc → @lp-devops
```

### Slash commands

```
/security-scan            # varredura de segredos + plano de remediação
/new-endpoint GET layouts por tipo de documento
/learn-xslt melhorar o retrieve de exemplos por similaridade
/parse-trace MQSeries sendo detectado como txt
```

### Ativar o settings (opcional)

O `settings.json` ativo não é criado automaticamente (proteção contra auto-modificação de config).
Para ativar o template:

```bash
cp .claude/settings.json.example .claude/settings.json
# depois remova as chaves de comentário "//..." (JSON não aceita comentários)
```

Ele define: idioma português, uma allowlist de comandos seguros (reduz prompts) e o
hook `git-push-advisory`. **Para desativar o hook**, remova o bloco `hooks` do settings.

> Use `.claude/settings.local.json` para overrides por máquina (não versionar).

## Memória dos agentes

Os agentes usam `memory: project` — fatos persistentes do projeto acumulam na memória
do Claude Code e são recuperados por relevância. Registre aqui decisões não óbvias
(ex.: por que o cache é populado no startup, qual layout-GUID corresponde a qual tipo).

## Princípios do harness

1. **Enxuto > completo:** 6 agentes que cobrem o ciclo, não 15 genéricos.
2. **Autoridade clara:** só o `@lp-devops` publica e mexe em infra/MCP.
3. **Resiliência e segurança em primeiro lugar** — refletidas nas rules.
4. **A API é a fonte da verdade;** o MCP é um cliente fino sobre ela.
