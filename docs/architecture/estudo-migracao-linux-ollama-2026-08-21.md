# Estudo de viabilidade — migração LayoutParserApi para Linux + Ollama (2026-08-21)

> PT-BR. Autoria: `@lp-architect`. Estudo de viabilidade — nenhuma implementação feita.

## 1. Pergunta central

**O LowCodeRunner precisa continuar em Windows Server?** Sim, e a dependência é mais forte
do que "Framework 4.8" sozinho sugere.

Evidência em `tools/LowCodeRunner/LayoutParserLowCodeRunner.csproj`:

```xml
<TargetFramework>net481</TargetFramework>
<PlatformTarget>x86</PlatformTarget>
```

- **`net481` (.NET Framework 4.8.1):** não roda em Linux. É um runtime Windows-only; a
  Microsoft nunca portou o Framework clássico (distinto do .NET 5+/Core) para Linux. Mono
  existiria como alternativa não-oficial, mas não é suportado pela Microsoft nem pelo
  fornecedor Sysmiddle, e historicamente tem gaps de compatibilidade justamente em interop
  Win32/COM — que é exatamente o que esse runner faz (ver abaixo). **Não testei isso de
  verdade nesta sessão** — é avaliação de risco, não medição.
- **`PlatformTarget=x86` é o achado mais grave.** O comentário no próprio `.csproj` (2026-07-15)
  documenta que isso não é acidental: o stack Sysmiddle v4.4.1 é **32-bit nativo**, e o host de
  produção FiatMQ (`FiatMQ_Instance_FiatMQ.exe`) também é `32BITREQ`. Rodar como `AnyCPU`/x64
  causa `BadImageFormatException` ao carregar `SysMiddle.ConnectUs.Functions.dll`. Isso é
  bitness de binário nativo/interop, um nível de acoplamento mais profundo que "só" a versão
  do Framework — reforça que não é um caso de "recompilar visando .NET 10" sem cooperação do
  fornecedor Sysmiddle (DLLs proprietárias, sem código-fonte disponível, ver
  `docs/architecture/decisao-dsl-mapper-sysmiddle-2026-08-21.md`).
- Referências adicionais (`SysMiddle.Base.dll`, `SysMiddle.ConnectUs.Core.dll`) vêm de um
  `InstanceBin` copiado do servidor de produção real — o runner depende de um gate de licença
  do host Sysmiddle, não é um binário autocontido.

**Conclusão da pergunta central: o LowCodeRunner não migra para Linux.** Não é uma questão de
esforço de port — é uma DLL proprietária x86 de terceiro, sem fonte, com gate de licença
atrelado a uma instância de host Windows específica. Enquanto o pathway "sysmiddle" (mapper
DSL via `RuleInterpretor`) for a via de transformação em produção, existe uma dependência dura
de Windows Server em algum ponto da topologia.

## 2. Opções avaliadas

| Opção | Resumo | Viabilidade |
|---|---|---|
| **A — Windows como sidecar remoto** | API principal (.NET 10) em Linux; LowCodeRunner isolado numa VM/host Windows Server dedicado, chamado via contrato HTTP interno | **Viável, recomendada como alvo de curto/médio prazo** |
| **B — Container Windows via Docker** | Rodar o `.exe` num Windows container | **Não resolve nada** — Windows containers exigem um host Windows por baixo (kernel Windows compartilhado); só reorganiza empacotamento, não remove a dependência de Windows Server |
| **C — Reescrever o pathway sysmiddle nativamente** | Usar o `RuleInterpretor` decifrado (decisão de 2026-08-21) para reimplementar o interpretador em .NET puro, cross-platform | **Viável a médio/longo prazo, mas caro e arriscado** — elimina a dependência de vez, mas exige reproduzir fielmente um motor proprietário line-based sem divergir do `.exe` original; risco de regressão silenciosa em produção fiscal |
| **D — Não migrar o pathway sysmiddle agora** | Migrar tudo (API, TCL/XSL pathway, Ollama, Redis, SQL client) para Linux; manter só uma VM Windows mínima para o LowCodeRunner | **Viável, sobrepõe com A** — é essencially a mesma coisa que A, com A descrevendo *como* a API fala com essa VM |

**A e D não são mutuamente exclusivos — A é o mecanismo, D é o escopo.** Recomendo tratá-los
como uma única opção combinada: **migrar tudo que não depende do Sysmiddle x86 para Linux,
isolar o LowCodeRunner numa VM Windows Server mínima e dedicada, e expor essa VM por um
contrato interno HTTP** (a API já fala com o runner via `Process.Start` local hoje —
trocar para uma chamada de rede para um serviço fino que hospeda o mesmo `Process.Start`
remotamente é o menor salto de arquitetura). C fica como aposta de longo prazo, condicionada
ao sucesso e maturidade do `RealMapperParser` que a Lia já está construindo para o
diagnóstico/síntese — se esse parser algum dia atingir paridade comportamental confiável com
o `RuleInterpretor` original, a dependência de Windows desaparece por completo. Não recomendo
apostar nisso como pré-requisito da migração — trate como bônus se a Trilha A evoluir bem.

## 3. Outras dependências Windows-specific (além do runner)

- **`builder.Host.UseWindowsService()`** em `Program.cs` — hoje a API é hospedada como serviço
  Windows nativo. Em Linux isso vira um serviço `systemd` (ou container) — mudança mecânica,
  sem bloqueio técnico, mas o `deploy.yml` atual assume Windows Service e precisa ser reescrito.
- **DPAPI / hardening de segredo em repouso** — o runbook de hardening da senha SQL
  (`docs/architecture/runbook-hardening-senha-sql-em-repouso.md`) recomenda
  `ProtectedData`/DPAPI, que é Windows-only. Em Linux, o equivalente seria variável de
  ambiente + secret manager do orquestrador (ex.: Docker secret, ou um KMS), ou
  `dotnet user-secrets` com permissão de arquivo restrita — precisa de nova decisão se a
  migração avançar.
- **SQL Server** — não é bloqueio. O client (`Microsoft.Data.SqlClient`) roda em Linux; o
  próprio SQL Server tem imagem Linux oficial (mas aqui é conexão a um SQL já existente,
  hospedado em outro lugar — irrelevante para o SO da API).
- **Ollama** — já roda nativamente em Linux; conforme `production-server-hardware.md`, a VM
  Ubuntu de produção já hospeda Ollama hoje. Não é um obstáculo à migração — é, na verdade, um
  argumento a favor: já existe precedente de Linux funcionando nesse ecossistema.
- **LayoutParserDecrypt.exe** (repo separado) — não investiguei o target framework dele nesta
  sessão; se também for Framework clássico, é outra dependência a mapear antes de fechar a
  arquitetura da VM Windows sidecar (pode compartilhar a mesma VM do LowCodeRunner).

## 4. Front-end / BFF

React (Vite) já é agnóstico de SO — sem impacto. O ponto real de atenção é o **BFF Fastify**
(`LayoutParserReact/server/`) e o `TrustedIdentityMiddleware`: hoje a API só confia nos headers
de identidade (`x-iis-user`/`x-iis-roles`) se a origem for **loopback**
(`TrustIdentityFromLoopbackOnly`). Em topologia containerizada/Linux (API e BFF em containers
separados, possivelmente em hosts diferentes), "loopback" deixa de significar a mesma coisa —
essa guarda de segurança precisa ser redesenhada (ex.: mTLS entre BFF e API, ou rede interna
isolada sem rota pública) antes de sair de "BFF e API sempre no mesmo host Windows". Superficial
por ora — não é o foco deste estudo, mas é um bloqueio real de segurança se ignorado.

## 5. Recomendação e fases

**Recomendação: Opção A+D combinada (API principal em Linux, LowCodeRunner isolado em Windows
Server sidecar via contrato de rede interno), com C como aposta de longo prazo não bloqueante.**

Fases sugeridas (nenhuma implementação feita — plano para o dono priorizar):
1. Mapear o `LayoutParserDecrypt.exe` (mesma pergunta: Framework clássico? x86?) — fecha o
   inventário completo do que precisa ficar na VM Windows sidecar.
2. Desenhar o contrato de rede interno API↔runner (HTTP interno mínimo, autenticado por rede
   isolada, não por segredo compartilhado — ver histórico do `ApiKeyGateFilter` removido).
3. Portar o hosting da API (`UseWindowsService` → `systemd`/container) e o pipeline de deploy
   (`deploy.yml`) — trabalho mecânico, sem dependência do ponto 1-2.
4. Redesenhar `TrustIdentityFromLoopbackOnly` para a nova topologia de rede antes de expor a
   API fora de um host único.
5. Migrar segredo em repouso de DPAPI para o equivalente Linux escolhido.
6. Só depois de tudo isso estável: avaliar se vale investir em C (reescrever o pathway
   sysmiddle) para eliminar de vez a VM Windows — decisão futura, não bloqueia as fases 1-5.

**Incerteza aberta, sem teste real:** não validei se o Framework 4.8.1 x86 roda sob Wine —
teoricamente Wine tem melhor suporte a Win32 nativo que Mono tem a .NET Framework gerenciado,
mas a combinação "runtime .NET Framework + DLL nativa proprietária x86 com gate de licença"
é exatamente o tipo de caso que tende a falhar de formas imprevisíveis sob Wine. Não
recomendo essa rota sem um teste isolado dedicado, e mesmo com sucesso seria um ambiente
não suportado por nenhum fornecedor — risco operacional alto para um pathway fiscal em
produção.
