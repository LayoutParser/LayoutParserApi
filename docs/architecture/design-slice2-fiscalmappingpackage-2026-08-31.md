# Design — Slice 2: `FiscalMappingPackage` (issue #229)

> Autora: `@lp-architect` (Aria) · 2026-08-31 · Só design, sem código. Segue a sequência vertical
> do Slice 1 (PR #234). Releia issue #229 (aceite literal) e a auditoria do Slice 1
> (`auditoria-slice1-identidade-workspaces-2026-08-31.md`, branch `feat/slice1-identidade-workspaces`)
> antes de implementar.

## 1. Investigação prévia (achados)

- **Não há precedente de upload multipart na API.** `ParseController`/`TestController` recebem
  texto/JSON, nunca `IFormFile`. Slice 2 é greenfield para validação de MIME/upload — não adaptar
  um padrão que não existe, criar um novo e documentá-lo (será o precedente para Slice 4/5).
- **Hash de conteúdo já é um padrão do projeto**: `LowCodeTransformationStore.ComputeSha256`
  (SHA256 hex lowercase) + índice em disco por `{sha256}.{layoutGuid}`. Reaproveitar a mesma função
  utilitária/convenção para o hash de revisão do pacote.
- **Armazenamento físico**: projeto já usa filesystem local para artefatos versionados
  (`tcl/`, `xsl/`, `Examples/`, `MLData/LowCodeTransformations/`) — não há blob storage distribuído
  hoje, host único Windows. Seguir o mesmo padrão: **filesystem + metadado em SQL**, não introduzir
  Azure Blob/S3 (infra nova fora de escopo, mesmo racional já usado para descartar Vault/Consul).
- **Antivírus**: não há mecanismo instalado além do Windows Defender do host. `Start-MpScan` via
  PowerShell é gratuito mas síncrono/lento para um upload request-response — não bloquear a resposta
  HTTP nele.

## 2. Modelo de domínio

```
FiscalMappingPackage (1) ──< FiscalMappingPackageRevision (N, imutável) ──< PackageArtifact (N)
        │                                                                        │
   WorkspaceId (FK)                                                    Kind: Sample|Layout|Spec|Xsd|
   ProjectId (FK, novo — mínimo:                                             ExpectedXml|FiscalContext
   Id+WorkspaceId, sem mais campos                                     Sha256, SizeBytes, MimeDeclared,
   além do que Slice 2 exige)                                          MimeSniffed, OriginalFileName,
                                                                        UploadedByUserId, UploadedAt,
                                                                        Classification, RetentionPolicy,
                                                                        InspectionStatus (Pending/Clean/
                                                                        Rejected), StoragePath
```

- **Imutabilidade**: alterar qualquer artefato cria `FiscalMappingPackageRevision` nova (número
  sequencial por pacote). Revisão anterior nunca é sobrescrita — é o que o Slice 3 (`MappingDraft`)
  vai referenciar por `RevisionId` exato.
- **`FiscalProject` mínimo**: Slice 1 já previu como filho de `Workspace` (auditoria §2 item 5) mas
  não implementou. Slice 2 precisa da tabela mínima (`Id`, `WorkspaceId`, `Name`, `CreatedAt`) só
  para o pacote pendurar em algo — não expandir escopo além disso (CRUD completo de projeto fica
  fora, é decisão de produto separada).
- **`InspectionStatus`**: `Pending` no upload → job assíncrono roda scan → `Clean`/`Rejected`. Draft
  (Slice 3) só pode referenciar revisão com todos os artefatos `Clean`.

## 3. Endpoints propostos

```
POST /api/workspaces/{workspaceId}/projects/{projectId}/mapping-packages
  multipart/form-data, idempotente por (workspaceId, projectId, IdempotencyKey header OU
  hash-do-conjunto-de-artefatos se header ausente)
  → 201 { packageId, revisionId, artifacts: [{ artifactId, kind, sha256, inspectionStatus }] }
  → 404 se sem membership no workspace (mesmo padrão fail-closed do Slice 1)
  → 422 se MIME sniffado diverge do declarado, tamanho excede limite, ou tipo de artefato
    obrigatório ausente — SEM inferência silenciosa (aceite explícito da issue)

POST /api/workspaces/{workspaceId}/mapping-packages/{packageId}/revisions
  multipart/form-data — cria nova revisão imutável (mesmo pacote, novos artefatos)

GET /api/workspaces/{workspaceId}/mapping-packages/{packageId}
  → 200 { packageId, revisions: [{ revisionId, createdAt, artifacts: [...] }] } (nunca conteúdo bruto)
  → 404 fail-closed (existe/não é seu = mesmo 404, igual Slice 1)

GET /api/workspaces/{workspaceId}/mapping-packages/{packageId}/revisions/{revisionId}/artifacts/{artifactId}/content
  → download controlado, só se InspectionStatus=Clean, audita acesso (AuditActionFilter)
```

Todos sob `WorkspaceMembershipFilter` (já proposto na auditoria do Slice 1 — reaproveitar, não
recriar o check em cada controller).

## 4. Validação de segurança (ordem de execução no upload)

1. **Tamanho** — rejeita antes de bufferizar tudo (`Request.Body` com limite de `Content-Length` +
   `MultipartReader` com corte por stream, nunca `IFormFile` carregado inteiro em memória sem cap).
2. **Extensão declarada** — allowlist por `Kind` (ex.: Xsd só aceita `.xsd`).
3. **MIME real (magic bytes/sniffing)** — nunca confiar em `IFormFile.ContentType`. Usar
   assinatura binária (biblioteca leve tipo `MimeDetective` ou verificação manual de magic number
   para os poucos formatos aceitos: TXT/CSV, XML, XLSX (é ZIP — ver item 5), XSD (é XML)). Divergência
   declarado×sniffado = 422, log estruturado sem conteúdo do arquivo.
4. **Zip bomb / XLSX e XML aninhado** — `XLSX` é OOXML (ZIP): aplicar limite de razão de descompressão
   e profundidade de entradas antes de abrir com `OpenXml` SDK. Para XML (XSD/XML esperado): parser
   com `XmlResolver = null` e `DtdProcessing = Prohibit` (mesma defesa que TCC/XXE clássica) —
   idêntico ao que já deveria valer em qualquer parser XML do projeto, reforçar aqui explicitamente.
5. **Antivírus** — scan assíncrono pós-upload via `Start-MpScan -ScanType CustomScan -ScanPath`
   (Windows Defender, gratuito, já no host). Roda em background (`Task.Run`/fila), não bloqueia a
   resposta HTTP do upload — `InspectionStatus=Pending` até o job concluir. **Documentar como
   pendência de infra para `@lp-devops` avaliar viabilidade do scan síncrono/assíncrono real no
   host de produção** — não bloqueia o desenho do Slice 2, mas bloqueia liberar tráfego real sem essa
   confirmação.
6. **Hash SHA256** calculado depois da validação passar, vira o identificador de conteúdo do
   artefato (mesma função `ComputeSha256` do `LowCodeTransformationStore`).

## 5. Armazenamento físico

- Path proposto: `MLData/FiscalMappingPackages/{workspaceId}/{packageId}/{revisionId}/{artifactId}_{originalFileName}`
  — segue a convenção já usada (`MLData/AiTransformationCandidates/{Name}/`, `MLData/LowCodeTransformations/`).
- Metadado (hash, autor, tipo, status) em SQL (grupo Database do `Program.cs`, mesmo padrão de
  `MapperDatabaseService`/`SqlIdentityWorkspaceStore`) — **SQL é fonte da verdade de metadado**,
  filesystem é blob store sem lógica.
- **Nunca logar conteúdo bruto**: `LogError`/`LogInformation` só citam `artifactId`/`sha256`/`kind`,
  nunca `OriginalFileName` bruto do usuário sem sanitização de log injection, nunca payload.

## 6. Plano de execução — `@lp-backend-dev`

1. Entidades: `FiscalMappingPackage`, `FiscalMappingPackageRevision`, `PackageArtifact`,
   `FiscalProject` (mínimo) em `Models/Entities/Fiscal/`, seguindo o padrão de constantes-string do
   Slice 1 (`Kind`, `InspectionStatus` como `static class`, não enum).
2. `IFiscalPackageStore` (SQL, ADO.NET cru como `SqlIdentityWorkspaceStore`) + `IFiscalPackageService`
   (orquestração: valida → sniffa MIME → grava filesystem → grava metadado → dispara scan).
3. `MultipartUploadValidator` novo em `Services/Validation/` (extensão/tamanho/MIME sniffing/XXE-safe
   XML settings/zip-bomb guard) — unit-testável isolado do controller.
4. `FiscalMappingPackagesController` sob `WorkspaceMembershipFilter` (criar o filter agora se ainda
   não existir do Slice 1 — checar antes de duplicar).
5. Handoff `@lp-qa`: testes de isolamento por workspace, MIME spoofing (arquivo `.xsd` com magic
   bytes de outro tipo), zip bomb sintético pequeno, upload idempotente (dois POSTs idênticos não
   duplicam artefato).

## 7. Decisões-chave (resumo)

- **Armazenamento**: filesystem local + SQL de metadado — não blob storage (sem infra nova, host único).
- **Validação**: MIME real via magic bytes (nunca `ContentType` do form), XXE bloqueado por
  `XmlResolver=null`/`DtdProcessing=Prohibit`, zip bomb guard em XLSX, antivírus assíncrono via
  Windows Defender (pendência de confirmação de infra com `@lp-devops`, não bloqueante ao design).
- **Imutabilidade**: revisão nova a cada alteração, nunca sobrescreve — é o contrato que Slice 3 exige.
