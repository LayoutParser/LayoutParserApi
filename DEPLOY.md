# Guia de Deploy - LayoutParserApi

## ⚠️ IMPORTANTE: Limitação da Descriptografia

**A descriptografia só funciona no .NET Framework 4.8.1** devido à tecnologia de criptografia utilizada. Isso significa que:

- ✅ **Windows**: Funciona nativamente com `LayoutParserDecrypt.exe`
- ⚠️ **Linux**: Requer soluções alternativas (Windows Container, Wine, ou serviço externo)

## Estrutura do Projeto

Este projeto consiste em:
- **LayoutParserApi** (.NET Core 9.0) - API REST principal
- **LayoutParserDecrypt** (.NET Framework 4.8.1) - **OBRIGATÓRIO** para descriptografia
- **LayoutParserLib** (.NET Framework 4.8.1) - Biblioteca de criptografia (usada pelo LayoutParserDecrypt)

## Estratégia de Deploy

A API usa `LayoutParserDecrypt.exe` como processo externo para descriptografar conteúdo. O executável **DEVE** estar disponível no ambiente de deploy.

### Configuração

**appsettings.json:**
```json
{
  "LayoutParserDecrypt": {
    "Path": ""
  }
}
```

Se `Path` estiver vazio, o sistema tentará encontrar o executável em:
1. `{BaseDirectory}/LayoutParserDecrypt.exe`
2. `{BaseDirectory}/tools/LayoutParserDecrypt.exe`
3. `{BaseDirectory}/../LayoutParserDecrypt/bin/Release/LayoutParserDecrypt.exe`
4. `{BaseDirectory}/../LayoutParserDecrypt/bin/Debug/LayoutParserDecrypt.exe`

Para caminho customizado:
```json
{
  "LayoutParserDecrypt": {
    "Path": "C:\\Deploy\\LayoutParserDecrypt.exe"
  }
}
```

## Deploy em Windows

### Opção 1: Deploy Tradicional (Recomendado)

#### 1. Build LayoutParserDecrypt

```powershell
# No diretório LayoutParserDecrypt
msbuild LayoutParserDecrypt.csproj /p:Configuration=Release /t:Restore,Build
```

#### 2. Build LayoutParserApi

```powershell
# No diretório LayoutParserApi
.\build.ps1
```

O script automaticamente copia `LayoutParserDecrypt.exe` para a pasta `publish` se encontrado.

#### 3. Estrutura de Deploy

```
deploy/
├── LayoutParserApi.dll
├── LayoutParserDecrypt.exe      # OBRIGATÓRIO
├── LayoutParserLib.dll          # OBRIGATÓRIO (dependência)
├── appsettings.json
└── (outras DLLs da API)
```

#### 4. Configurar appsettings.json

```json
{
  "LayoutParserDecrypt": {
    "Path": "C:\\Deploy\\LayoutParserDecrypt.exe"
  }
}
```

Ou deixe vazio se o executável estiver na mesma pasta da API.

### Opção 2: Windows Container

#### Dockerfile.windows

```dockerfile
# Dockerfile para Windows Container
FROM mcr.microsoft.com/dotnet/aspnet:9.0-nanoserver-1809 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["LayoutParserApi.csproj", "."]
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish

# Build LayoutParserDecrypt (requer MSBuild)
FROM mcr.microsoft.com/dotnet/framework/sdk:4.8 AS decrypt-build
WORKDIR /src
COPY ["../LayoutParserDecrypt", "."]
RUN msbuild LayoutParserDecrypt.csproj /p:Configuration=Release

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
COPY --from=decrypt-build /src/bin/Release/LayoutParserDecrypt.exe .
COPY --from=decrypt-build /src/LayoutParserLib.dll .
ENTRYPOINT ["dotnet", "LayoutParserApi.dll"]
```

#### Build e Deploy

```powershell
docker build -f Dockerfile.windows -t layoutparser-api:windows .
docker run -d -p 8080:8080 --name layoutparser-api layoutparser-api:windows
```

## Deploy em Linux

### ⚠️ Limitação: Linux não suporta .NET Framework nativamente

Opções disponíveis:

### Opção 1: Windows Container no Linux (Recomendado)

Use Docker com Windows Container (requer Windows Server ou Windows 10/11 Pro com Hyper-V).

```powershell
docker build -f Dockerfile.windows -t layoutparser-api:windows .
```

### Opção 2: Wine (Não Recomendado - Instável)

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
RUN apt-get update && apt-get install -y wine
WORKDIR /app
COPY --from=publish /app/publish .
COPY LayoutParserDecrypt.exe .
ENV WINEPREFIX=/app/.wine
RUN winecfg
ENTRYPOINT ["dotnet", "LayoutParserApi.dll"]
```

**Nota**: Wine pode não funcionar corretamente com .NET Framework 4.8.1.

### Opção 3: Serviço de Descriptografia Externo

Crie um serviço separado em Windows que expõe a descriptografia via API/HTTP:

```csharp
// Serviço de Descriptografia em Windows
[ApiController]
public class DecryptController : ControllerBase
{
    [HttpPost("decrypt")]
    public IActionResult Decrypt([FromBody] string content)
    {
        // Usar LayoutParserDecrypt.exe
        var result = DecryptContent(content);
        return Ok(result);
    }
}
```

A API Linux chama esse serviço via HTTP.

### Opção 4: Docker Multi-Stage com Windows Build

```dockerfile
# Build LayoutParserDecrypt em Windows
FROM mcr.microsoft.com/dotnet/framework/sdk:4.8 AS decrypt-build
WORKDIR /src
COPY LayoutParserDecrypt .
RUN msbuild /p:Configuration=Release

# API em Linux
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app

# Copiar executável (não funcionará, apenas exemplo)
# COPY --from=decrypt-build /src/bin/Release/LayoutParserDecrypt.exe ./tools/
```

**Nota**: Executáveis Windows não funcionam em containers Linux.

## Scripts de Build

### build.ps1 (Windows)

```powershell
# Build completo (API + LayoutParserDecrypt)
.\build.ps1

# Build apenas API
.\build.ps1 -SkipDecrypt
```

O script automaticamente:
1. Builda a API
2. Procura `LayoutParserDecrypt.exe` em `../LayoutParserDecrypt/bin/Release/`
3. Copia para `publish/` se encontrado

### build.sh (Linux)

```bash
# Build apenas API (LayoutParserDecrypt deve ser buildado separadamente em Windows)
./build.sh
```

## Deploy em IIS

### 1. Build e Publicar

```powershell
.\build.ps1
```

### 2. Estrutura no IIS

```
C:\inetpub\wwwroot\LayoutParserApi\
├── LayoutParserApi.dll
├── LayoutParserDecrypt.exe
├── LayoutParserLib.dll
├── appsettings.json
└── (outras DLLs)
```

### 3. Configurar Application Pool

- **.NET CLR Version**: No Managed Code
- **Managed Pipeline Mode**: Integrated

### 4. Configurar appsettings.json

```json
{
  "LayoutParserDecrypt": {
    "Path": "C:\\inetpub\\wwwroot\\LayoutParserApi\\LayoutParserDecrypt.exe"
  }
}
```

## Git - Estrutura do Repositório

Ambos os projetos estão no mesmo repositório:

```
repos/
├── LayoutParserApi/          # .NET Core 9.0
│   ├── LayoutParserApi.csproj
│   ├── Program.cs
│   └── ...
├── LayoutParserDecrypt/      # .NET Framework 4.8.1 (OBRIGATÓRIO)
│   ├── LayoutParserDecrypt.csproj
│   ├── Program.cs
│   └── ...
└── LayoutParserLib/          # .NET Framework 4.8.1
    └── CryptographySysMiddle.cs
```

## Troubleshooting

### Erro: "LayoutParserDecrypt.exe não encontrado"

**Solução:**
1. Verifique se o executável foi buildado: `msbuild LayoutParserDecrypt.csproj /p:Configuration=Release`
2. Configure o caminho em `appsettings.json`
3. Verifique permissões de acesso ao arquivo

### Erro: "Legacy decryptor failed (Exit code: X)"

**Possíveis causas:**
1. LayoutParserLib.dll não está no mesmo diretório do executável
2. .NET Framework 4.8.1 não está instalado
3. Conteúdo criptografado inválido

**Solução:**
1. Copie `LayoutParserLib.dll` para o mesmo diretório de `LayoutParserDecrypt.exe`
2. Verifique se .NET Framework 4.8.1 está instalado
3. Teste o executável manualmente: `LayoutParserDecrypt.exe input.txt output.txt`

### Erro em Linux: "Executável não encontrado" ou falha ao executar

**Causa**: Executáveis Windows não funcionam em Linux.

**Soluções:**
1. Use Windows Container
2. Use serviço de descriptografia externo em Windows
3. Considere migrar para .NET Core (requer reimplementação da criptografia)

## Recomendações

1. **Produção Windows**: Use deploy tradicional com ambos os executáveis
2. **Produção Linux**: Use Windows Container ou serviço externo
3. **Desenvolvimento**: Mantenha ambos os projetos no mesmo repositório
4. **Build Automatizado**: Use scripts para copiar executáveis automaticamente

## Próximos Passos (Opcional)

Para eliminar a dependência do .NET Framework:
1. Analisar a implementação de criptografia em `LayoutParserLib`
2. Reimplementar em .NET Core/Standard
3. Testar compatibilidade com dados existentes
4. Migrar gradualmente

**Nota**: Isso requer análise detalhada da implementação de criptografia original.
