# Script de Build para LayoutParserApi
# Suporta build para Windows e Docker

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    
    [Parameter(Mandatory=$false)]
    [switch]$Docker,
    
    [Parameter(Mandatory=$false)]
    [string]$DockerImageName = "layoutparser-api",
    
    [Parameter(Mandatory=$false)]
    [string]$DockerTag = "latest"
)

$ErrorActionPreference = "Stop"

Write-Host "================================================" -ForegroundColor Cyan
Write-Host "  Build LayoutParserApi" -ForegroundColor Cyan
Write-Host "================================================" -ForegroundColor Cyan
Write-Host ""

# Verificar se está no diretório correto
if (-not (Test-Path "LayoutParserApi.csproj")) {
    Write-Host "Erro: LayoutParserApi.csproj não encontrado!" -ForegroundColor Red
    Write-Host "Execute este script na pasta do projeto LayoutParserApi." -ForegroundColor Red
    exit 1
}

if ($Docker) {
    Write-Host "Modo: Docker Build" -ForegroundColor Yellow
    Write-Host ""
    
    # Verificar se Docker está instalado
    try {
        docker --version | Out-Null
    } catch {
        Write-Host "Erro: Docker não está instalado ou não está no PATH!" -ForegroundColor Red
        exit 1
    }
    
    Write-Host "Construindo imagem Docker..." -ForegroundColor Green
    docker build -t "${DockerImageName}:${DockerTag}" .
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host ""
        Write-Host "✓ Imagem Docker construída com sucesso!" -ForegroundColor Green
        Write-Host "  Imagem: ${DockerImageName}:${DockerTag}" -ForegroundColor Cyan
        Write-Host ""
        Write-Host "Para executar:" -ForegroundColor Yellow
        Write-Host "  docker run -d -p 8080:8080 -p 8443:8443 --name layoutparser-api ${DockerImageName}:${DockerTag}" -ForegroundColor White
    } else {
        Write-Host ""
        Write-Host "✗ Erro ao construir imagem Docker!" -ForegroundColor Red
        exit 1
    }
} else {
    Write-Host "Modo: Build Local (.NET)" -ForegroundColor Yellow
    Write-Host "Configuração: $Configuration" -ForegroundColor Yellow
    Write-Host ""
    
    # Verificar se .NET SDK está instalado
    try {
        $dotnetVersion = dotnet --version
        Write-Host "✓ .NET SDK encontrado: $dotnetVersion" -ForegroundColor Green
    } catch {
        Write-Host "Erro: .NET SDK não encontrado!" -ForegroundColor Red
        exit 1
    }
    
    # Limpar builds anteriores
    Write-Host "Limpando builds anteriores..." -ForegroundColor Yellow
    dotnet clean -c $Configuration
    
    # Restaurar dependências
    Write-Host ""
    Write-Host "Restaurando dependências..." -ForegroundColor Yellow
    dotnet restore
    
    # Build
    Write-Host ""
    Write-Host "Compilando projeto..." -ForegroundColor Yellow
    dotnet build -c $Configuration --no-restore
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host ""
        Write-Host "✓ Build concluído com sucesso!" -ForegroundColor Green
        Write-Host ""
        
        # Publicar
        Write-Host "Publicando para produção..." -ForegroundColor Yellow
        $publishDir = ".\publish"
        if (Test-Path $publishDir) {
            Remove-Item $publishDir -Recurse -Force
        }
        
        dotnet publish -c $Configuration -o $publishDir --no-build
        
        if ($LASTEXITCODE -eq 0) {
            Write-Host ""
            Write-Host "✓ Publicação concluída!" -ForegroundColor Green
            Write-Host "  Diretório: $publishDir" -ForegroundColor Cyan
            
            # Copiar LayoutParserDecrypt.exe (OBRIGATÓRIO para descriptografia)
            Write-Host ""
            Write-Host "Copiando LayoutParserDecrypt.exe..." -ForegroundColor Yellow
            $decryptReleasePath = "..\LayoutParserDecrypt\bin\Release\LayoutParserDecrypt.exe"
            $decryptDebugPath = "..\LayoutParserDecrypt\bin\Debug\LayoutParserDecrypt.exe"
            
            $decryptPath = $null
            if (Test-Path $decryptReleasePath) {
                $decryptPath = $decryptReleasePath
            } elseif (Test-Path $decryptDebugPath) {
                $decryptPath = $decryptDebugPath
            }
            
            if ($decryptPath) {
                Copy-Item $decryptPath -Destination $publishDir -Force
                Write-Host "✓ LayoutParserDecrypt.exe copiado" -ForegroundColor Green
                
                # Copiar LayoutParserLib.dll
                $libPath = "..\LayoutParserDecrypt\LayoutParserLib.dll"
                if (Test-Path $libPath) {
                    Copy-Item $libPath -Destination $publishDir -Force
                    Write-Host "✓ LayoutParserLib.dll copiado" -ForegroundColor Green
                } else {
                    # Tentar copiar da pasta bin do LayoutParserDecrypt
                    $libReleasePath = "..\LayoutParserDecrypt\bin\Release\LayoutParserLib.dll"
                    $libDebugPath = "..\LayoutParserDecrypt\bin\Debug\LayoutParserLib.dll"
                    if (Test-Path $libReleasePath) {
                        Copy-Item $libReleasePath -Destination $publishDir -Force
                        Write-Host "✓ LayoutParserLib.dll copiado (Release)" -ForegroundColor Green
                    } elseif (Test-Path $libDebugPath) {
                        Copy-Item $libDebugPath -Destination $publishDir -Force
                        Write-Host "✓ LayoutParserLib.dll copiado (Debug)" -ForegroundColor Green
                    }
                }
            } else {
                Write-Host "⚠ LayoutParserDecrypt.exe NÃO encontrado!" -ForegroundColor Yellow
                Write-Host "  Build do LayoutParserDecrypt necessário antes do deploy." -ForegroundColor Yellow
                Write-Host "  Execute: msbuild ..\LayoutParserDecrypt\LayoutParserDecrypt.csproj /p:Configuration=Release" -ForegroundColor Yellow
            }
            
            Write-Host ""
            Write-Host "Arquivos prontos para deploy em:" -ForegroundColor Yellow
            Write-Host "  $((Resolve-Path $publishDir).Path)" -ForegroundColor White
        } else {
            Write-Host ""
            Write-Host "✗ Erro ao publicar!" -ForegroundColor Red
            exit 1
        }
    } else {
        Write-Host ""
        Write-Host "✗ Erro no build!" -ForegroundColor Red
        exit 1
    }
}

Write-Host ""
Write-Host "================================================" -ForegroundColor Cyan
Write-Host "  Build Concluído" -ForegroundColor Cyan
Write-Host "================================================" -ForegroundColor Cyan

