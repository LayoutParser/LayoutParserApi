---
name: nuget-private-feed-401
description: Restore de pacote NuGet novo falha (NU1301 401) pelo feed privado da org APENAS no lado WSL; pelo lado Windows (powershell.exe) o TFS autentica e o restore passa
metadata:
  type: project
---

Adicionar um `PackageReference` novo e rodar `dotnet restore`/`build` falha com
`NU1301 ... 401 (Unauthorized)` na fonte **CENTRAL TFS**
(`https://tfs.ndd.tech/NDD-DECollection/.../nddCentralSolucoesPackages`). Uma única
fonte com 401 aborta o restore inteiro, mesmo que o pacote exista no nuget.org e já
esteja no cache global (`~/.nuget/packages`).

**Why:** o ambiente não tem credenciais para o feed privado da organização (registrado
globalmente em `dotnet nuget list source`). O restore consulta todas as fontes e a que
retorna 401 derruba tudo.

**How to apply:** ao introduzir uma dependência NuGet em qualquer projeto do repo, crie
um `nuget.config` **escopado na pasta do projeto** com `<clear/>` + só o nuget.org
(feito em `ai/XslSynth/nuget.config`). Isso torna o `dotnet build` reprodutível sem o
flag `-s`. Alternativa pontual: `dotnet restore -s https://api.nuget.org/v3/index.json`.
`nuget.config` não é segredo, mas é config — avise `@lp-devops` que foi adicionado.
Confirmado em 2026-07-10 ao adicionar `DocumentFormat.OpenXml` (v3.3.0, já no cache).

**Atualização 2026-07-18:** o 401 é específico do lado **WSL** (sem credencial de domínio).
Rodando `dotnet` pelo lado **Windows** (`powershell.exe -NoProfile -Command "dotnet ..."`),
o CENTRAL TFS autentica (NTLM/credencial do usuário logado) e o `dotnet add package` +
restore passam sem workaround — confirmado ao adicionar
`Microsoft.Extensions.Hosting.WindowsServices` 10.0.10 na API. Regra prática: operação
NuGet neste repo → preferir o lado Windows; workaround do nuget.config só se precisar
restaurar pelo WSL.

**Atualização 2026-07-27:** na Bash tool deste harness (git-bash restrito), nem `dotnet`
nem `powershell.exe` estão resolvíveis via `PATH` (erro "command not found", apesar de
`echo $PATH` listar `C:\Program Files\dotnet\`) — não é o 401 do TFS, é o binário mesmo
não sendo achado pelo lookup do shell. `ls`/`rm`/`cat`/`head`/`tail`/`which` também dão
"command not found" (coerente com a diretriz do harness de usar Read/Grep/Glob em vez
de coreutils). Workaround que funcionou: invocar o executável pelo **caminho absoluto
entre aspas**, ex. `"/c/Program Files/dotnet/dotnet.exe" build` — restore e build
passaram normalmente (sem 401, então não conflita com a nota acima sobre o TFS). Se
`dotnet build` "sumir" nesta Bash tool, tentar o caminho absoluto antes de suspeitar de
problema de rede/autenticação.
