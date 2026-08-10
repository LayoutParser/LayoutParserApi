# Decisão — remover a dependência de `appConnector.Client.Core.Util` do runner low-code

> `@lp-architect` (Aria), 2026-08-10. Origem: determinação do dono do projeto — *"não podemos ter a
> dependência de `appConnector.Client.Core.Util`"*.
>
> Base factual: descompilação do `appConnector.Client.Core.dll` (via `ilspycmd`) fornecido pelo dono
> do projeto em `.claude/tmp/10082026/`. Tudo abaixo sai do código real, não de suposição.

---

## 1. A descoberta que torna isso barato

Eu tinha recomendado **contra** trazer a lógica para dentro, com receio de reimplementar o motor de
transformação e destruir o oráculo. **Estava mirando no alvo errado.** A dependência real de
`appConnector` é minúscula.

Rastreando o que o runner de fato usa de `MappersHelper`:

| Chamada | Vem de | Precisa de `appConnector`? |
|---|---|---|
| `APIManager.Instance.GetApiExecutorByIdentifier(...)` | `SysMiddle.Base` | **Não** |
| `_apiManager.GetMapperByIdentifier(mapperId)` | `SysMiddle.Base` | **Não** |
| `_apiManager.GetMappers()` | `SysMiddle.Base` | **Não** |
| `_apiManager.ExecuteMapper(...)` | `SysMiddle.Base` | **Não** |
| `_apiManager.ExecuteParser(...)` | `SysMiddle.Base` | **Não** |
| `ConnectorApplicationManager.Instance.GetServerPackage()` | **`appConnector.Client.Core`** | **Sim** |

**Uma única chamada.** E o que ela faz (decompilado, `ConnectorApplicationManager:259-265`):

```csharp
public string GetServerPackage()
{
    lock (_lock) { return _configuration.PackageMappers; }
}
```

Devolve **uma string**. Todo o `Bootstrap()` — o replay de `Service1.OnStart` via
`EDocsClientConnectorManager().Start()`, com ~9s de init e as threads de transporte que falham sem
VPN — existe para popular `_configuration` e ler esse campo.

**Valor real, encontrado em `Instance_FiatMQ/AppConnector.DIR/Conf/config.xml:8`:**

```xml
<PackageMappers>938f9978-836f-48c1-9c0f-c2898caf4b20</PackageMappers>
```

Configurando esse GUID explicitamente, `appConnector` sai inteiro do runner.

---

## 2. O que isso destrava além do pedido

O pedido era remover um acoplamento. O efeito colateral é maior que o pedido:

- **Custo de execução.** Medido pelo `@lp-backend-dev`: ~58s por transformação, dos quais ~9s são o
  init da `InstanceFactory` e 0,6s o bootstrap. Sai boa parte disso.
- **Threads penduradas.** O `Main` chama `Environment.Exit` **porque** o bootstrap deixa threads de
  transporte vivas. Sem bootstrap, provavelmente não precisa — e isso **remove o risco de travar o
  host de teste**, que era a objeção que eu tinha levantado contra o projeto de teste in-process.
- **Debugabilidade.** Sem o bootstrap, `SysmiddleRuntime.Create` + executor viram um caminho reto,
  com breakpoint em tudo que não é o motor.

Ou seja: o projeto de teste que você pediu fica **viável** por causa desta mudança.

---

## 3. ⚠️ As duas funções NÃO são equivalentes — este é o ponto de maior risco

`SysmiddleMapperExecutor` foi portado de `MappersHelper.ExecuteMappingDocument`. Mas o caminho vivo
hoje — o que produziu o gabarito byte a byte — é `ExecuteMappingDocumentById`. **São diferentes em
três pontos**, todos verificados no decompilado:

| | `ExecuteMappingDocumentById` (**viva**, bate com o gabarito) | `ExecuteMappingDocument` (origem do port) |
|---|---|---|
| `InsertDeclaration` | só se `<…>` **e não** começar com `<?xml` | se `<…>` (**sem** a exclusão) |
| `ExecuteParser("", document)` | **chama** (resultado descartado) | não chama |
| Pós-processamento NF-e (`ChangeInfCpl`/`InfIdFisco`/`InfAdProd`) | **não aplica** | **aplica os três** |

**Ligar o `SysmiddleMapperExecutor` como está mudaria a saída.** Não é drop-in. Isto invalida a
sugestão que eu dei na conversa ("é só ligar, 90% pronto") — o port replica a função errada.

### Decisão

**Preservar a semântica da `ExecuteMappingDocumentById`**, porque é a única com equivalência provada
contra o gabarito real. Concretamente:

1. `InsertDeclaration` **com** a exclusão de `<?xml`.
2. **Manter** a chamada a `ExecuteParser("", document)` mesmo com o resultado descartado. Ela pode ter
   efeito colateral de estado no executor; remover agora é risco sem ganho. Investigar depois, à parte.
3. Os três pós-processamentos NF-e ficam no código, **desligados por padrão**, atrás de flag. Hoje
   eles não são aplicados; ligá-los sem evidência quebraria o gabarito.

---

## 4. O que fazer

| # | Item |
|---|---|
| 1 | `LowCode:Package` passa a carregar `938f9978-836f-48c1-9c0f-c2898caf4b20`. Vazio → **erro explícito** com exit code próprio, nunca silêncio |
| 2 | Resolver mapper por `APIExecutor.GetMapperByIdentifier` / `GetMapperByName` — sai `MappersHelper` |
| 3 | Executor único no runner, com a semântica da `ById` (§3) |
| 4 | Remover `using appConnector.Client.Core.*` e as referências do `.csproj` |
| 5 | Remover `Bootstrap()` e avaliar se `Environment.Exit` ainda é necessário |
| 6 | Preservar as **duas** partes do gate de licença: registro do `ILicenseController` no `InstanceFactory` **e** `APIManager.GlobalConfigurationFileName` |

### Critério de aceite — não é "compila e roda"

O gabarito de `.claude/tmp/exemplos/`: **4246 chars, byte a byte** (tolerado apenas o espaço duplo em
`<?xml  version=`, que vem do produtor do gabarito). Qualquer divergência é regressão.

Se remover o `Bootstrap()` quebrar a equivalência, **ele volta** — a hipótese do §2 é plausível, não
comprovada.

---

## 5. Sobre as DLLs em `.claude/tmp/10082026/`

São **4 assemblies compilados, não código-fonte**: `appConnector.Client.Core.dll`,
`appConnector.Client.Interface.dll`, `appConnector.Client.ProcessInterface.dll`, `NDDigital.Core2.dll`.

Comparadas com as da Bin da instância: **mesma versão (4.3.3.0), bytes diferentes** — são rebuilds
distintos. `Core` tem 3.072 bytes a mais, `Interface` 12.288 a mais.

**Não devem ser introduzidas nesta leva**, por dois motivos:

1. Misturar dois builds da mesma versão é fonte clássica de bug sutil — o binder resolve por versão,
   não por conteúdo, e a diferença passa despercebida.
2. **Se o plano acima funcionar, elas deixam de ser necessárias** — a dependência de `appConnector`
   desaparece do runner.

Elas foram, ainda assim, decisivas: serviram de **fonte para a descompilação** que produziu esta
decisão. Pergunta em aberto para o dono do projeto: de onde vieram? Se forem build oficial mais novo
da NDD, vale trocar a Bin inteira — mas isso é decisão separada, com regressão contra o gabarito.
