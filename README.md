# LayoutParser API

> **PT-BR** · API .NET 10 que faz o *parsing* de documentos posicionais (TXT / MQSeries / IDOC) contra um **layout XML** (gerado no low-code Sysmiddle), com uma camada de IA/ML que aprende a gerar transformações (**XSLT/TCL**) automaticamente — caminho para eliminar o XML low-code.
>
> **EN** · .NET 10 API that *parses* positional documents (TXT / MQSeries / IDOC) against an **XML layout** (authored in the Sysmiddle low-code platform), with an AI/ML layer that learns to generate transformations (**XSLT/TCL**) automatically — the path to retiring the low-code XML.

<p>
  <img alt=".NET" src="https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square" />
  <img alt="ASP.NET Core" src="https://img.shields.io/badge/ASP.NET%20Core-Web%20API-512BD4?style=flat-square" />
  <img alt="Redis" src="https://img.shields.io/badge/Redis-cache-DC382D?style=flat-square" />
  <img alt="SQL Server" src="https://img.shields.io/badge/SQL%20Server-source%20of%20truth-CC2927?style=flat-square" />
  <img alt="Serilog" src="https://img.shields.io/badge/Serilog%20%2B%20Elastic-observability-005571?style=flat-square" />
  <img alt="LLM" src="https://img.shields.io/badge/LLM-Ollama%20%7C%20Gemini%20%7C%20OpenAI-7E57C2?style=flat-square" />
</p>

---

## 📑 Índice / Table of Contents

1. [Visão geral / Overview](#1-visão-geral--overview)
2. [Ecossistema de projetos / Project ecosystem](#2-ecossistema-de-projetos--project-ecosystem)
3. [Arquitetura / Architecture](#3-arquitetura--architecture)
4. [Como o parse funciona / How parsing works](#4-como-o-parse-funciona--how-parsing-works)
5. [A visão de IA / The AI vision](#5-a-visão-de-ia--the-ai-vision)
6. [Stack tecnológica / Tech stack](#6-stack-tecnológica--tech-stack)
7. [API & Endpoints](#7-api--endpoints)
8. [Configuração / Configuration](#8-configuração--configuration)
9. [Como rodar / Getting started](#9-como-rodar--getting-started)
10. [Segurança / Security](#10-segurança--security-)
11. [Observabilidade / Observability](#11-observabilidade--observability)
12. [Estrutura de pastas / Project structure](#12-estrutura-de-pastas--project-structure)
13. [Harness Claude Code & MCP](#13-harness-claude-code--mcp)
14. [Roadmap](#14-roadmap)

---

## 1. Visão geral / Overview

**🇧🇷** O LayoutParser API é o back-end de uma plataforma de **leitura, validação e transformação de documentos de integração** (notas fiscais eletrônicas e mensagens corporativas). O usuário, pelo front-end ([LayoutParserReact](#2-ecossistema-de-projetos--project-ecosystem)), anexa **dois arquivos**:

- um **layout XML** — a "planta" que descreve as linhas, campos, posições e tamanhos do documento (modelado no low-code **Sysmiddle**);
- um **documento** posicional (`.txt`, `.mq_series`, `.idoc`) — o dado bruto a ser interpretado.

A API casa os dois, devolve a **estrutura parseada** (linhas → campos → valores) para o front renderizar, e — em background — **aprende** com cada arquivo processado para evoluir até gerar as transformações sozinha.

**🇺🇸** LayoutParser API is the back-end of a platform for **reading, validating and transforming integration documents** (electronic fiscal notes and corporate messages). Through the front-end, the user uploads **two files**: an **XML layout** (the blueprint describing rows, fields, positions and sizes — authored in the **Sysmiddle** low-code tool) and a **positional document** (`.txt`, `.mq_series`, `.idoc`). The API matches them, returns the **parsed structure** for the front-end to render, and — in the background — **learns** from every processed file to eventually generate the transformations on its own.

> **Contexto acadêmico / Academic note:** este repositório é a base de back-end de um projeto de faculdade (TCC). A documentação é mantida bilíngue propositadamente. / This repository is the back-end base of a college project; documentation is intentionally bilingual.

---

## 2. Ecossistema de projetos / Project ecosystem

**🇧🇷** Esta API é o **ponto de conexão** de quatro repositórios. **🇺🇸** This API is the **connection hub** of four repositories.

| Repositório | Tipo | Papel / Role |
|-------------|------|--------------|
| **LayoutParserApi** *(este)* | ASP.NET Core 10 Web API | Orquestra parse, cache, IA/ML, transformação e logging. **Source of truth do runtime.** |
| **LayoutParserLib** | .NET Class Library (DLL) | Criptografia Sysmiddle (`CryptographySysMiddle`) e utilitários compartilhados. Referenciada pela API via `HintPath`. |
| **LayoutParserDecrypt** | .NET Console (`.exe`) | Descriptografa os layouts/pacotes Sysmiddle. Invocado pela API como processo externo. |
| **LayoutParserReact** | Vite + React + TypeScript | Front-end: upload de arquivos, render da estrutura parseada, edição de layouts. |

```
                         ┌───────────────────────────┐
                         │     LayoutParserReact      │  (front-end / Vite + React)
                         │  upload .xml + documento   │
                         └─────────────┬──────────────┘
                                       │  HTTP (CORS)
                                       ▼
        ┌──────────────────────────────────────────────────────────┐
        │                    LayoutParserApi (.NET 10)               │
        │                                                            │
        │  Parse ── Cache(Redis) ── Learning/RAG ── Transformation   │
        │     │           │              │                │          │
        └─────┼───────────┼──────────────┼────────────────┼─────────┘
              │           │              │                │
   ┌──────────┘   ┌───────┘        ┌─────┘          ┌─────┘
   ▼              ▼                ▼                ▼
LayoutParserLib  Redis        SQL Server        LLM (Ollama /
(crypto .dll)   (layouts/   (ConnectUS_Macgyver  Gemini / OpenAI)
                 mappers)     — source of truth)
   │
   ▼
LayoutParserDecrypt.exe  (descriptografia Sysmiddle)
```

> **🔌 MCP** · Um **MCP Server em C#** (ver [§13](#13-harness-claude-code--mcp)) expõe as operações da API como *tools* para agentes de IA, transformando este ecossistema num conjunto de ferramentas operáveis por LLMs.

---

## 3. Arquitetura / Architecture

**🇧🇷** A API segue uma arquitetura em camadas com **injeção de dependência** (registrada em [`Program.cs`](Program.cs)). **🇺🇸** Layered architecture with **dependency injection** wired in [`Program.cs`](Program.cs).

| Camada / Layer | Pasta / Folder | Responsabilidade / Responsibility |
|----------------|----------------|-----------------------------------|
| **API / Controllers** | `Controllers/` | Endpoints HTTP, validação de request, orquestração. |
| **Parsing** | `Services/Parsing/` | Detecção de tipo, *split* de linhas, normalização e validação do layout. |
| **Cache** | `Services/Cache/` + `Services/Database/Cached*` | Camada Redis sobre os dados do SQL (layouts e mappers). |
| **Database** | `Services/Database/` | Acesso ao SQL Server, descriptografia (`DecryptionService`). |
| **Learning / RAG** | `Services/Learning/`, `Services/Generation/` | Aprende padrões de cada documento; RAG sobre exemplos. |
| **Transformation** | `Services/Transformation/`, `Services/XmlAnalysis/` | Geração de **XSLT/TCL**, pipeline low-code, validação por XSD. |
| **Testing** | `Services/Testing/` | Testes automatizados de transformação (aplica XSLT e compara). |
| **Logging / Audit** | `Services/Logging/` | Serilog → arquivo + Elasticsearch, `CorrelationId`, auditoria. |

### Princípios de design / Design principles

- **Resiliência primeiro:** a aplicação **sobe mesmo sem Redis** (cache degrada graciosamente) — ver `Program.cs:171`.
- **SQL é a fonte da verdade; Redis é cache.** O cache é populado no startup via `RefreshCacheFromDatabaseAsync()`.
- **Background learning:** o parse responde rápido ao usuário e dispara aprendizado/transformação em *fire-and-forget* (`Task.Run` / `RunInBackgroundAsync`).
- **CorrelationId por request:** header `X-Correlation-ID` propagado para todos os logs.

---

## 4. Como o parse funciona / How parsing works

**🇧🇷** Fluxo do endpoint principal `POST /api/parse/upload` ([`ParseController`](Controllers/ParseController.cs)):

**🇺🇸** Flow of the main endpoint `POST /api/parse/upload`:

```
1. Recebe layoutFile (.xml) + txtFile (documento)
2. DetectType(sample)  ──► xml | mqseries | idoc | txt
   └─ override por extensão (.mq_series, .idoc) ou nome do layout (contém "MQ")
3. Se for XML puro ► devolve conteúdo para o front processar (xmltools.js)
4. Senão:
   a. Salva o arquivo p/ aprendizado (SaveFileForLearningAsync) — assíncrono
   b. ParseAsync(layoutStream, txtStream)  ──► Layout + ParsedFields + RawText
   c. ReestruturarLayout ► ReordenarSequences ► BuildDocumentStructure
   d. CalculateLineValidations (se o layout tem tamanho de linha configurado)
   e. Dispara LowCodeAuto.RunInBackgroundAsync (aprendizado contínuo, MQSeries)
5. Retorna { success, detectedType, layout, fields, text, summary,
             documentStructure, lineValidations, validationErrors }
```

**🇧🇷** Tipos de documento suportados: **XML**, **MQSeries**, **IDOC** e **TXT** posicional. A detecção combina conteúdo + extensão + layout selecionado (o conteúdo sozinho pode falhar em MQSeries com 601 chars/linha — daí os *overrides*).

**🇺🇸** Supported document types: **XML**, **MQSeries**, **IDOC** and positional **TXT**. Detection combines content + extension + selected layout (content alone can misfire on 601-char MQSeries lines — hence the overrides).

---

## 5. A visão de IA / The AI vision

**🇧🇷** O objetivo de longo prazo é **eliminar o XML low-code do Sysmiddle**: hoje um analista desenha o mapeamento no low-code, que produz um XML intermediário; queremos que o back-end **gere sozinho o XSLT** que transforma o documento original no XML final.

**🇺🇸** The long-term goal is to **retire the Sysmiddle low-code XML**: today an analyst designs the mapping in the low-code tool, producing an intermediate XML; we want the back-end to **generate the XSLT itself** that transforms the original document into the final XML.

### O "trio de ouro" / The golden triple

```
   TXT (original)  ──►  XML low-code (intermediário)  ──►  XML final (esperado)
   ▲                                                              ▲
   └──────────────  aprender a ponte direta via XSLT  ────────────┘
                    learn the direct bridge via XSLT
```

**🇧🇷** Cada documento processado gera um triplo **(TXT, XML low-code, XML final)** — ou seja, um **dataset de tradução supervisionada já rotulado**. A abordagem recomendada **não é fine-tuning** de um modelo Llama, e sim **RAG + few-shot com loop de auto-correção**:

**🇺🇸** Every processed document yields a triple **(TXT, low-code XML, final XML)** — i.e. a **pre-labeled supervised translation dataset**. The recommended approach is **not fine-tuning** a Llama model, but **RAG + few-shot with a self-correction loop**:

```
┌─ 1. INDEX ─────────────────────────────────────────────────────────┐
│  Indexa pares (layout → XSLT) num vector store (embeddings).        │
├─ 2. RETRIEVE ──────────────────────────────────────────────────────┤
│  Para um novo layout, recupera os k exemplos mais similares.        │
├─ 3. GENERATE ──────────────────────────────────────────────────────┤
│  LLM local (Ollama / Llama) gera um XSLT candidato (few-shot).      │
├─ 4. VALIDATE ──────────────────────────────────────────────────────┤
│  Aplica o XSLT ► compara com o XML final esperado (XSD + diff).     │
│  (XsdValidationService + AutomatedTransformationTestService)        │
├─ 5. CORRECT ───────────────────────────────────────────────────────┤
│  Realimenta os erros no prompt e repete 3-4 até convergir.          │
└────────────────────────────────────────────────────────────────────┘
```

**🇧🇷** **Por que não fine-tuning?** Você já tem validadores determinísticos (XSD, comparação com o XML final). Um loop *gerar → validar → corrigir* é mais barato, auditável e confiável que treinar um modelo, e melhora sozinho conforme a base de exemplos cresce. O **Llama via Ollama** roda no seu servidor (config `Ollama` em [`appsettings.json`](appsettings.json)), mantendo os dados on-premise.

**🇺🇸** **Why not fine-tuning?** You already have deterministic validators (XSD, comparison against the final XML). A *generate → validate → correct* loop is cheaper, auditable and more reliable than training a model, and improves on its own as the example base grows. **Llama via Ollama** runs on your server (`Ollama` config), keeping data on-premise.

> Os serviços que já materializam essa visão: `TransformationLearningService`, `ImprovedXslGeneratorService`, `ImprovedTclGeneratorService`, `RAGService`, `AutomatedTransformationTestService`, `XsdValidationService`.

---

## 6. Stack tecnológica / Tech stack

| Categoria | Tecnologia | Uso |
|-----------|-----------|-----|
| Runtime | **.NET 10** / ASP.NET Core Web API | `LangVersion: preview`, nullable + implicit usings |
| Cache | **Redis** (`StackExchange.Redis`) | Cache de layouts e mappers |
| Banco | **SQL Server** (`Microsoft.Data.SqlClient`) | Fonte da verdade (layouts, mappers) |
| Logging | **Serilog** + Sinks (File, Async, Elasticsearch) | Logs estruturados + correlação |
| Serialização | `System.Text.Json` + **Newtonsoft.Json** | JSON com XML preservado (`UnsafeRelaxedJsonEscaping`) |
| Docs | **Swashbuckle / Swagger** | OpenAPI em Development |
| LLM | **Ollama** (deepseek-coder/Llama), **Gemini**, **OpenAI** | Geração e aprendizado |
| Container | **Docker** (`Dockerfile`, target Linux) | Deploy |
| Crypto | **LayoutParserLib.dll** | Criptografia Sysmiddle |

---

## 7. API & Endpoints

**🇧🇷** Todos os controllers seguem a convenção `/api/[controller]`. Swagger UI disponível em Development (`/swagger`). Abaixo, os grupos por capacidade:

**🇺🇸** All controllers follow the `/api/[controller]` convention. Swagger UI is available in Development (`/swagger`). Grouped by capability:

| Grupo / Group | Controllers | O que faz / What it does |
|---------------|-------------|--------------------------|
| **Parse** | `Parse`, `Document` | Parseia documento contra layout; valida estrutura. Ex.: `POST /api/parse/upload`. |
| **Catálogo / Catalog** | `LayoutDatabase`, `MapperDatabase` | Lista/busca layouts e mappers (com cache Redis). |
| **Transformação / Transformation** | `Transformation`, `TransformationExecution`, `AutoTransformation` | Gera e executa XSLT/TCL; pipeline low-code. |
| **Análise XML / XML analysis** | `XmlAnalysis` | Analisa estrutura/tipo de documentos XML (NFe, CTe, MDFe, NFCom). |
| **IA/ML** | `Learning`, `RAG`, `DataGeneration` | Aprende padrões; RAG; gera dados sintéticos. |
| **Qualidade / Quality** | `Test`, `Testing` | Testes automatizados de transformação. |
| **Observabilidade / Observability** | `Metrics`, `Monitoring` | Métricas e healthchecks. |

> Detalhe completo de rotas em runtime via Swagger. / Full route detail at runtime via Swagger.

---

## 8. Configuração / Configuration

**🇧🇷** Configuração em [`appsettings.json`](appsettings.json). Chaves principais:

| Seção | Descrição |
|-------|-----------|
| `Redis:ConnectionString` | Endpoint do Redis (default `localhost:6379`). |
| `Database` | SQL Server (`Server`, `Database`, `UserId`, `Password`). **Use secrets!** |
| `Ollama:Url` / `Ollama:Model` | LLM local (`http://localhost:11434`, `deepseek-coder:6.7b`). |
| `Gemini` / `OpenAI` | Provedores de LLM em nuvem. **Use secrets!** |
| `ElasticSearch` | Sink de logs (`Url`, `Username`, `Password`). |
| `LowCode` | Runner Sysmiddle (`RunnerPath`, `SysmiddleDir`, `AllowedPackageGuids`). |
| `LayoutParserDecrypt:Path` | Caminho do `.exe` de descriptografia. |
| `TransformationPipeline` | Caminhos de TCL/XSL/exemplos/modelos aprendidos. |
| `XsdValidation` | XSDs por tipo de documento fiscal (NFe, CTe, NFCom, MDFe). |
| `Kestrel:Endpoints:Http:Url` | Porta de escuta (default `http://0.0.0.0:5000`). |

> ⚠️ **Nunca** comite credenciais. Ver [§10 Segurança](#10-segurança--security-).

---

## 9. Como rodar / Getting started

### Pré-requisitos / Prerequisites

- **.NET 10 SDK**
- **Redis** (opcional — a API sobe sem ele, sem cache)
- **SQL Server** acessível (string em `Database`)
- **Ollama** rodando (opcional, para features de IA local)
- **LayoutParserLib** buildada (a API referencia `..\LayoutParserLib\bin\Debug\LayoutParserLib.dll`)

### Local

```bash
# 1. Restaurar e buildar a lib referenciada primeiro
dotnet build ../LayoutParserLib/LayoutParserLib.sln

# 2. Configurar segredos (OBRIGATÓRIO — o appsettings.json tem placeholders vazios, ver §10)
#    O UserSecretsId já está no .csproj; basta setar os valores:
dotnet user-secrets set "Database:Password" "<senha-do-sql>"
dotnet user-secrets set "Gemini:ApiKey" "<key-do-gemini>"
dotnet user-secrets set "ElasticSearch:Username" "<usuario-elastic>"   # opcional (só se usar Elastic)
dotnet user-secrets set "ElasticSearch:Password" "<senha-elastic>"     # opcional
dotnet user-secrets list                                               # conferir

# 3. Restaurar, buildar e rodar a API
dotnet restore
dotnet build
dotnet run                       # http://0.0.0.0:5000  (Swagger em /swagger)
```

> **🔑 Como os segredos são lidos / How secrets are resolved.** A API usa `IConfiguration`,
> então qualquer chave do `appsettings.json` pode ser sobrescrita (precedência crescente):
> **`appsettings.json` → `dotnet user-secrets` (Development) → variáveis de ambiente → args**.
> Em ambiente/produção, use **variáveis de ambiente** no formato `Section__Key` (duplo underscore):
>
> ```bash
> export Database__Password="<senha-do-sql>"
> export Gemini__ApiKey="<key-do-gemini>"
> export ElasticSearch__Password="<senha-elastic>"
> ```
>
> Os valores secretos foram **removidos do código e do `appsettings.json`** (placeholders vazios);
> se nenhum segredo for fornecido, o recurso correspondente apenas degrada (ex.: Gemini/Elastic ficam inativos).

### Docker

```bash
docker build -t layoutparser-api .
docker run -p 5000:5000 \
  -e Redis__ConnectionString=host.docker.internal:6379 \
  layoutparser-api
```

> Em ambiente, o CORS já libera as origens do front (`localhost:81`, `172.25.32.42:*` etc.) — ver `Program.cs:149`.

---

## 10. Segurança / Security ⚠️

**🇧🇷 Remediação no código — FEITO ✅.** Os segredos foram **removidos** do [`appsettings.json`](appsettings.json) (placeholders vazios) **e dos fallbacks hardcoded no código** (`GeminiAIService`, `LayoutDatabaseService`, `ElasticSearchLogger`). O `.gitignore` ignora `appsettings.*.local.json`. Os segredos agora vêm de `dotnet user-secrets` (dev) / variáveis de ambiente `Section__Key` (produção) — ver [§9](#9-como-rodar--getting-started).

**🇺🇸 Code-side remediation — DONE ✅.** Secrets were **removed** from [`appsettings.json`](appsettings.json) (empty placeholders) **and from the hardcoded code fallbacks**. Secrets now come from `dotnet user-secrets` (dev) / `Section__Key` environment variables (prod) — see [§9](#9-como-rodar--getting-started).

**🔴 Ainda pendente (ação do operador / @lp-devops):**

1. **ROTACIONAR** as chaves expostas — a **key do Gemini** e a **senha do SQL** devem ser tratadas como **comprometidas** (estiveram em texto plano no repo e persistem no histórico). Gere novas no provedor/banco.
2. **Limpar o histórico do git** (BFG / `git filter-repo`), pois os segredos antigos continuam em commits passados mesmo após este commit. Rewrite de história exige force-push e coordenação com clones/forks — ver plano em [`.claude/rules/security.md`](.claude/rules/security.md).

> **⚠️ Rotacionar é obrigatório mesmo após limpar a história:** qualquer clone feito antes da limpeza ainda contém os segredos. A limpeza reduz exposição futura; só a rotação invalida o que vazou.

---

## 11. Observabilidade / Observability

- **Serilog** escreve para console + arquivo (`Logging:File:Directory`) com *rolling* por tamanho, e opcionalmente para **Elasticsearch**.
- Todo log carrega **`CorrelationId`** (`X-Correlation-ID`), permitindo rastrear um arquivo do upload ao parse.
- **Auditoria** via `AuditActionFilter` + `AuditLogger` em endpoints sensíveis (`[ServiceFilter(typeof(AuditActionFilter))]`).
- Controllers `Metrics` e `Monitoring` expõem métricas e estado.

---

## 12. Estrutura de pastas / Project structure

```
LayoutParserApi/
├── Controllers/            # Endpoints HTTP (Parse, Transformation, Learning, RAG, ...)
├── Services/
│   ├── Parsing/            # Detecção, split, normalização, validação de layout
│   ├── Cache/              # LayoutCacheService, MapperCacheService (Redis)
│   ├── Database/           # SQL Server + DecryptionService + Cached*
│   ├── Learning/           # Aprendizado de padrões a partir dos arquivos
│   ├── Generation/         # IA (Gemini/Ollama), RAG, geração de dados sintéticos
│   ├── Transformation/     # XSLT/TCL, pipeline low-code, validação
│   ├── XmlAnalysis/        # Análise de estrutura XML + XSD
│   ├── Testing/            # Testes automatizados de transformação
│   └── Logging/            # Serilog, Elastic, correlação, auditoria
├── Models/                 # Entities, DTOs, ML, RAG, Validation, ...
├── Enum/ · Scripts/ · Properties/
├── Program.cs              # Bootstrap + DI + pipeline + cache warmup
├── appsettings.json        # Configuração (⚠️ ver §10)
├── Dockerfile
├── .claude/                # Harness Claude Code (agents, rules, commands) — ver §13
└── README.md               # este arquivo
```

---

## 13. Harness Claude Code & MCP

**🇧🇷** Este projeto vem equipado com um **harness de IA** (pasta [`.claude/`](.claude)) para potencializar o desenvolvimento assistido por LLM, e um **MCP Server em C#** que expõe as operações da API como *tools* para agentes.

**🇺🇸** This project ships with an **AI harness** ([`.claude/`](.claude)) to boost LLM-assisted development, plus a **C# MCP Server** that exposes the API operations as agent *tools*.

| Componente | Local | Função |
|------------|-------|--------|
| **Agents** | `.claude/agents/` | Personas enxutas focadas em .NET (arquiteto, dev, parser/LLM, QA, devops, doc). |
| **Rules** | `.claude/rules/` | Handoff, autoridade, padrões .NET, segurança, MCP. |
| **Commands** | `.claude/commands/` | Slash commands (`/security-scan`, `/new-endpoint`, `/learn-xslt`...). |
| **Hooks** | `.claude/hooks/` | Autoridade de `git push`, varredura de segredos. |
| **MCP Server** | `mcp/LayoutParserMcp/` | Servidor MCP (C#) — *tools* de parse, catálogo e transformação. |

> Setup e detalhes em [`.claude/README.md`](.claude/README.md) e [`mcp/LayoutParserMcp/README.md`](mcp/LayoutParserMcp/README.md).

---

## 14. Roadmap

- [ ] **Segurança:** remover segredos do `appsettings.json`, rotacionar chaves, migrar para secrets/env.
- [ ] **RAG vetorial:** indexar pares (layout → XSLT) num vector store (Redis Stack / RediSearch).
- [ ] **Loop de auto-correção XSLT:** fechar o ciclo gerar → validar (XSD) → corrigir com o Llama local.
- [ ] **Eliminar o XML low-code:** validar a geração autônoma de XSLT contra os XMLs finais esperados.
- [ ] **Testes automatizados:** ampliar cobertura de `Services/Testing`.
- [ ] **MCP Server:** expandir o conjunto de *tools* e publicar o registro em `.mcp.json`.

---

<p align="center"><sub>LayoutParser API · .NET 10 · Documentação bilíngue mantida para fins acadêmicos e operacionais.</sub></p>
