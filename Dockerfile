# Dockerfile para LayoutParserApi - Servidor de Produção 172.25.32.42
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
USER $APP_UID
WORKDIR /app

# Criar diretórios para logs e dados
RUN mkdir -p /var/log/layoutparser /app/data/examples /app/data/rules

# Expor portas para produção
EXPOSE 8080
EXPOSE 8443

# Esta fase é usada para compilar o projeto de serviço
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["LayoutParserApi.csproj", "."]
RUN dotnet restore "./LayoutParserApi.csproj"
COPY . .
WORKDIR "/src/."
RUN dotnet build "./LayoutParserApi.csproj" -c $BUILD_CONFIGURATION -o /app/build

# Esta fase é usada para publicar o projeto de serviço a ser copiado para a fase final
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./LayoutParserApi.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# Esta fase é usada na produção
FROM base AS final
WORKDIR /app

# Copiar arquivos publicados
COPY --from=publish /app/publish .

# Configurar variáveis de ambiente para produção
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://0.0.0.0:8080;https://0.0.0.0:8443

# Configurar Ollama para servidor remoto
ENV OLLAMA_URL=http://172.25.32.42:11434
ENV OLLAMA_MODEL=llama3.1:latest

ENTRYPOINT ["dotnet", "LayoutParserApi.dll"]