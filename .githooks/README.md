# Git hooks locais — LayoutParser API

Este diretório contém hooks versionados (o `.git/hooks/` padrão não é versionável).
Hoje existe um hook: **`pre-commit`**, que roda o [gitleaks](https://github.com/gitleaks/gitleaks)
contra os arquivos staged e **bloqueia o commit** se encontrar padrão de segredo
(connection string com senha, API key, etc.).

Contexto: a senha do SQL Server já vazou uma vez para o `appsettings.json` comitado
(regressão de 2026-07-18) porque alguém testou local com a senha no arquivo e o
commit foi junto. Esse hook existe para pegar isso **antes** do commit, não depois.
Ver [`.claude/rules/security.md`](../.claude/rules/security.md) para o histórico completo.

## Instalação (uma vez por clone)

```bash
git config core.hooksPath .githooks
```

Isso faz o git usar `.githooks/` em vez de `.git/hooks/` — cada dev configura
uma vez no seu clone; não é algo que o repositório força automaticamente.

## Instalar o gitleaks

O hook precisa do binário `gitleaks` no `PATH`. É um binário único, sem
dependência de Python/Node.

**Windows (via winget, recomendado neste time):**
```powershell
winget install gitleaks
```

**Windows (download manual):** baixe o `.zip` da [página de releases](https://github.com/gitleaks/gitleaks/releases)
(`gitleaks_<versão>_windows_x64.zip`), extraia `gitleaks.exe` para uma pasta já
no `PATH` (ex.: `C:\Users\<user>\bin\`, adicionando-a ao `PATH` se necessário).

**Via Go (se já tiver o toolchain):**
```bash
go install github.com/gitleaks/gitleaks/v8@latest
```

Confirme a instalação:
```bash
gitleaks version
```

## Comportamento sem gitleaks instalado

O hook é **fail-open**: se `gitleaks` não estiver no `PATH`, ele avisa no
console mas **não bloqueia** o commit — para não travar quem ainda não instalou
o binário. Isso é uma rede de segurança adicional ao step equivalente no CI
(gerido pelo `@lp-devops`), que é quem efetivamente impede merge de segredo
mesmo se o hook local não rodar.

## Config do projeto

As regras customizadas (além do conjunto padrão do gitleaks) ficam em
[`.gitleaks.toml`](../.gitleaks.toml) na raiz do repo — inclui uma regra
específica para connection string de SQL Server com senha embutida.

## Testar manualmente

```bash
git add .
gitleaks protect --staged --config=.gitleaks.toml
```
