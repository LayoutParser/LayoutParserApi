#!/bin/bash
# Script de Build para LayoutParserApi (Linux/Mac)
# Suporta build para Linux e Docker

set -e

CONFIGURATION="${1:-Release}"
BUILD_DOCKER="${2:-false}"

echo "================================================"
echo "  Build LayoutParserApi"
echo "================================================"
echo ""

# Verificar se está no diretório correto
if [ ! -f "LayoutParserApi.csproj" ]; then
    echo "Erro: LayoutParserApi.csproj não encontrado!"
    echo "Execute este script na pasta do projeto LayoutParserApi."
    exit 1
fi

if [ "$BUILD_DOCKER" = "true" ] || [ "$BUILD_DOCKER" = "docker" ]; then
    echo "Modo: Docker Build"
    echo ""
    
    # Verificar se Docker está instalado
    if ! command -v docker &> /dev/null; then
        echo "Erro: Docker não está instalado!"
        exit 1
    fi
    
    echo "Construindo imagem Docker..."
    docker build -t layoutparser-api:latest .
    
    if [ $? -eq 0 ]; then
        echo ""
        echo "✓ Imagem Docker construída com sucesso!"
        echo "  Imagem: layoutparser-api:latest"
        echo ""
        echo "Para executar:"
        echo "  docker run -d -p 8080:8080 -p 8443:8443 --name layoutparser-api layoutparser-api:latest"
    else
        echo ""
        echo "✗ Erro ao construir imagem Docker!"
        exit 1
    fi
else
    echo "Modo: Build Local (.NET)"
    echo "Configuração: $CONFIGURATION"
    echo ""
    
    # Verificar se .NET SDK está instalado
    if ! command -v dotnet &> /dev/null; then
        echo "Erro: .NET SDK não encontrado!"
        exit 1
    fi
    
    DOTNET_VERSION=$(dotnet --version)
    echo "✓ .NET SDK encontrado: $DOTNET_VERSION"
    
    # Limpar builds anteriores
    echo ""
    echo "Limpando builds anteriores..."
    dotnet clean -c $CONFIGURATION
    
    # Restaurar dependências
    echo ""
    echo "Restaurando dependências..."
    dotnet restore
    
    # Build
    echo ""
    echo "Compilando projeto..."
    dotnet build -c $CONFIGURATION --no-restore
    
    if [ $? -eq 0 ]; then
        echo ""
        echo "✓ Build concluído com sucesso!"
        echo ""
        
        # Publicar
        echo "Publicando para produção..."
        PUBLISH_DIR="./publish"
        if [ -d "$PUBLISH_DIR" ]; then
            rm -rf "$PUBLISH_DIR"
        fi
        
        dotnet publish -c $CONFIGURATION -o $PUBLISH_DIR --no-build
        
        if [ $? -eq 0 ]; then
            echo ""
            echo "✓ Publicação concluída!"
            echo "  Diretório: $PUBLISH_DIR"
            echo ""
            echo "Arquivos prontos para deploy em:"
            echo "  $(realpath $PUBLISH_DIR)"
        else
            echo ""
            echo "✗ Erro ao publicar!"
            exit 1
        fi
    else
        echo ""
        echo "✗ Erro no build!"
        exit 1
    fi
fi

echo ""
echo "================================================"
echo "  Build Concluído"
echo "================================================"

