# Script de Deploy para LayoutParserApi
# Suporta deploy em Windows (IIS/Service) e Docker

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("docker", "windows", "iis")]
    [string]$Target = "docker",
    
    [Parameter(Mandatory=$false)]
    [string]$DockerImageName = "layoutparser-api",
    
    [Parameter(Mandatory=$false)]
    [string]$DockerTag = "latest",
    
    [Parameter(Mandatory=$false)]
    [string]$DeployPath = "",
    
    [Parameter(Mandatory=$false)]
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

Write-Host "================================================" -ForegroundColor Cyan
Write-Host "  Deploy LayoutParserApi" -ForegroundColor Cyan
Write-Host "================================================" -ForegroundColor Cyan
Write-Host ""

# Verificar se está no diretório correto
if (-not (Test-Path "LayoutParserApi.csproj")) {
    Write-Host "Erro: LayoutParserApi.csproj não encontrado!" -ForegroundColor Red
    exit 1
}

# Build se necessário
if (-not $SkipBuild) {
    Write-Host "Executando build..." -ForegroundColor Yellow
    if ($Target -eq "docker") {
        & .\build.ps1 -Docker -DockerImageName $DockerImageName -DockerTag $DockerTag
    } else {
        & .\build.ps1 -Configuration Release
    }
    
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Erro no build. Abortando deploy." -ForegroundColor Red
        exit 1
    }
}

Write-Host ""

switch ($Target) {
    "docker" {
        Write-Host "Deploy: Docker Container" -ForegroundColor Yellow
        Write-Host ""
        
        # Parar container existente se houver
        $existingContainer = docker ps -a --filter "name=layoutparser-api" --format "{{.Names}}"
        if ($existingContainer) {
            Write-Host "Parando container existente..." -ForegroundColor Yellow
            docker stop layoutparser-api 2>$null
            docker rm layoutparser-api 2>$null
        }
        
        # Executar container
        Write-Host "Iniciando container..." -ForegroundColor Yellow
        docker run -d `
            --name layoutparser-api `
            -p 8080:8080 `
            -p 8443:8443 `
            -v layoutparser-logs:/var/log/layoutparser `
            -v layoutparser-data:/app/data `
            --restart unless-stopped `
            "${DockerImageName}:${DockerTag}"
        
        if ($LASTEXITCODE -eq 0) {
            Write-Host ""
            Write-Host "✓ Container implantado com sucesso!" -ForegroundColor Green
            Write-Host ""
            Write-Host "Status:" -ForegroundColor Yellow
            docker ps --filter "name=layoutparser-api"
            Write-Host ""
            Write-Host "Logs:" -ForegroundColor Yellow
            Write-Host "  docker logs -f layoutparser-api" -ForegroundColor White
        } else {
            Write-Host ""
            Write-Host "✗ Erro ao implantar container!" -ForegroundColor Red
            exit 1
        }
    }
    
    "windows" {
        Write-Host "Deploy: Windows Service/Standalone" -ForegroundColor Yellow
        Write-Host ""
        
        if ([string]::IsNullOrWhiteSpace($DeployPath)) {
            $DeployPath = "C:\Deploy\LayoutParserApi"
        }
        
        Write-Host "Diretório de deploy: $DeployPath" -ForegroundColor Cyan
        Write-Host ""
        
        # Criar diretório se não existir
        if (-not (Test-Path $DeployPath)) {
            Write-Host "Criando diretório de deploy..." -ForegroundColor Yellow
            New-Item -ItemType Directory -Path $DeployPath -Force | Out-Null
        }
        
        # Copiar arquivos publicados
        Write-Host "Copiando arquivos..." -ForegroundColor Yellow
        $publishDir = ".\publish"
        if (-not (Test-Path $publishDir)) {
            Write-Host "Erro: Diretório publish não encontrado. Execute build primeiro." -ForegroundColor Red
            exit 1
        }
        
        Copy-Item -Path "$publishDir\*" -Destination $DeployPath -Recurse -Force
        
        Write-Host ""
        Write-Host "✓ Deploy concluído!" -ForegroundColor Green
        Write-Host ""
        Write-Host "Para executar:" -ForegroundColor Yellow
        Write-Host "  cd $DeployPath" -ForegroundColor White
        Write-Host "  dotnet LayoutParserApi.dll" -ForegroundColor White
    }
    
    "iis" {
        Write-Host "Deploy: IIS" -ForegroundColor Yellow
        Write-Host ""
        
        Write-Host "Nota: Deploy manual para IIS requer configuração adicional." -ForegroundColor Yellow
        Write-Host "Consulte DEPLOY.md para instruções detalhadas." -ForegroundColor Yellow
        Write-Host ""
        
        if ([string]::IsNullOrWhiteSpace($DeployPath)) {
            $DeployPath = "C:\inetpub\wwwroot\LayoutParserApi"
        }
        
        Write-Host "Diretório de deploy: $DeployPath" -ForegroundColor Cyan
        
        # Copiar arquivos
        $publishDir = ".\publish"
        if (-not (Test-Path $publishDir)) {
            Write-Host "Erro: Diretório publish não encontrado." -ForegroundColor Red
            exit 1
        }
        
        Write-Host "Copiando arquivos..." -ForegroundColor Yellow
        Copy-Item -Path "$publishDir\*" -Destination $DeployPath -Recurse -Force
        
        Write-Host ""
        Write-Host "✓ Arquivos copiados!" -ForegroundColor Green
        Write-Host ""
        Write-Host "Próximos passos:" -ForegroundColor Yellow
        Write-Host "  1. Configure o Application Pool no IIS (No Managed Code)" -ForegroundColor White
        Write-Host "  2. Configure o site apontando para: $DeployPath" -ForegroundColor White
        Write-Host "  3. Configure as permissões de acesso" -ForegroundColor White
    }
}

Write-Host ""
Write-Host "================================================" -ForegroundColor Cyan
Write-Host "  Deploy Concluído" -ForegroundColor Cyan
Write-Host "================================================" -ForegroundColor Cyan

