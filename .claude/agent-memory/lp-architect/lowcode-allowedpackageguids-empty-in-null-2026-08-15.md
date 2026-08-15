---
name: lowcode-allowedpackageguids-empty-in-null-2026-08-15
description: Causa raiz confirmada do "mapper não encontrado" em produção — AllowedPackageGuids vazio gera IN (NULL) no SQL, não ProjectId
metadata:
  type: project
---

Causa raiz CONFIRMADA (2026-08-15) com o `appsettings.json` real de produção: a seção `LowCode`
inteira está ausente do JSON do host. `IOptions<LowCodeRunnerOptions>` cai para os defaults do C#
— e por coincidência `ProjectId` default (`2`) bate com o banco, então **não é o campo culpado**.
O culpado é `AllowedPackageGuids` default (`new()`, lista vazia): a query em
`MapperDatabaseService.GetMappersByLayoutGuidForPackagesAsync` monta `IN (NULL)` quando a lista é
vazia, o que nunca bate com nenhuma linha — zero mappers encontrados sempre, para qualquer layout.

**Why:** o binding do `Options` pattern do ASP.NET Core não lança erro quando uma seção inteira
falta no `appsettings.json` — ele silenciosamente usa os defaults do construtor da classe. Isso é
perigoso quando um default (`ProjectId=2`) coincide por acaso com um valor válido do domínio,
mascarando que a seção inteira está ausente — só um campo (`AllowedPackageGuids`) expôs o problema
porque seu default vazio não tem correspondência válida possível no banco.

**How to apply:** ao investigar "config drift" nesta API, não presumir que todos os campos de uma
seção ausente falham igualmente — verificar caso a caso se o default do C# de cada campo poderia
coincidir com um valor real do domínio (mascarando o problema) ou não. Ver documento completo:
`docs/architecture/diagnostico-mapper-nao-encontrado-producao-2026-08-15.md`. Achados relacionados
no mesmo doc: `Ollama:Url` órfão (aponta para `localhost`, Ollama migrou para VM Linux separada —
serviço ativo no DI, degrada mas fica sempre indisponível) e `ElasticSearch:Password` em texto
claro (órfão inofensivo, sem consumidor no código atual).
