---
name: webapplicationfactory-composition-root-testing
description: Como testar a composição real do Program.cs (issue #90) e como dublar o runner x86 do pathway sysmiddle (issue #104), padrões validados em PR #298/#299.
metadata:
  type: project
---

Issues #90 e #104 resolvidas em 2026-09-03, PRs #298 (di-composition-root) e #299
(e2e-lowcode-runner-double), branches a partir de origin/develop, push feito pelo próprio
`@lp-backend-dev` (exceção pontual autorizada pelo dono — matriz de autoridade não mudou).

**Padrão para WebApplicationFactory<Program> nesta API:**
- `Program.cs` usa top-level statements → precisa de `public partial class Program;` no fim do
  arquivo para o projeto de teste (assembly diferente) enxergar o entry point. Zero mudança de
  comportamento — é só uma segunda declaração parcial do tipo gerado implicitamente.
- **Controllers não são resolvíveis via `GetRequiredService<TController>()`** mesmo com
  `AddControllers()` registrado — o MVC ativa controllers via `IControllerActivator`, não expõe
  eles como serviço registrado no container. Usar `ActivatorUtilities.CreateInstance<TController>
  (scope.ServiceProvider)` — reproduz a mesma forma de ativação do framework.
- `appsettings.Testing.json` (convenção de ambiente `Testing`, carregado via
  `builder.UseEnvironment("Testing")` na WebApplicationFactory customizada) é o jeito de destravar
  o host real sem infra: Redis/Database/IdentityDatabase apontando para endpoint+timeout curto que
  falha rápido (não precisa estar no ar — os serviços já degradam graciosamente por design do
  projeto), `Logging:File:Directory` redirecionado pra fora do path de produção.
- SQL/Redis indisponíveis NÃO travam o teste: `CachePermanentWarmupBackgroundService` roda em
  background com retry infinito mas nunca é aguardado pelo host startup; a exceção fica só como
  log de warning.

**Padrão de double para o runner x86 (Sysmiddle, pathway sysmiddle):**
- `LowCodeTransformationService.ExecuteRunnerProcessAsync` é `protected virtual` DE PROPÓSITO —
  é o ponto de substituição oficial (já usado por `LowCodeRunnerCancellationTests.RunnerBloqueado`
  antes desta sessão). Subclasse, sobrescreve só esse método, extrai `--outputFile` de
  `psi.ArgumentList` e escreve o XML gabarito lá — todo o resto (semáforo de concorrência,
  montagem de args, leitura do arquivo, timeout) roda como produção.
- `MapperDatabaseService.GetRankedMapperCandidatesForLayoutGuidAsync` também é `virtual` (mesma
  classe concreta, sem interface própria) — double por herança, construtor base recebe
  `NullLogger`/fake `IDecryptionService`/`IConfiguration` vazio (nunca usados, já que o método
  real é sobrescrito).
- `LowCodeAutoTransformationService` (Singleton em produção) resolve `MapperDatabaseService` via
  `IServiceScopeFactory.CreateScope()` — em teste, um `ServiceCollection` minúsculo só com
  `AddScoped<MapperDatabaseService>(_ => fakeInstance)` já basta para dar um `IServiceScopeFactory`
  válido, sem precisar montar o container inteiro do `Program.cs`.
- Ver [[webapplicationfactory-composition-root-testing]] para o padrão irmão de composição real.
