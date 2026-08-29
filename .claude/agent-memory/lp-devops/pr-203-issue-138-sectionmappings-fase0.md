---
name: pr-203-issue-138-sectionmappings-fase0
description: PR #203 (issue #138/#126) sectionMappings Fase 0 — 4º caso do falso positivo SCS0018 por deslocamento de linha, mesmo padrão já visto em #198/#200
metadata:
  type: project
---

PR #203 (`feat/section-mappings-fase0-138` → `develop`) implementa Fase 0 de rastreabilidade
TXT↔XML (`sectionMappings`/`xmlNamespaces`, contrato aditivo, resolução só `authoritative`,
`tcl-xsl` retorna `null`). Build/test locais: 400/404 passando (4 falhas pré-existentes, paths
Windows-only em ambiente Linux — mesmo padrão de sempre, não bloqueante).

CI quebrou de primeira com SCS0018 NOVO em `Services/Transformation/LowCode/
LowCodeAutoTransformationService.cs:366` e `:410`. Diagnosticado: mesmo padrão de
[[pr-200-ci-scs0018-bloqueado]]/[[pr-198-ci-scs0018-bloqueado]] — achados já existiam no
baseline nas linhas 364/408, mas a própria PR adicionou `MapperDecryptedContent =
mapper.DecryptedContent` em dois pontos do arquivo antes desses `File.WriteAllTextAsync`,
deslocando-os +2 linhas. Corrigido atualizando `security-code-scan-baseline.json`
(364→366, 408→410), commit `4ba77aa`, push, CI ficou verde.

**Why:** este é o 4º caso do mesmo padrão nesta janela de trabalho — sempre que uma PR adiciona
linhas *antes* de um achado SCS0018 já catalogado no mesmo arquivo, o achado "novo" reportado
pelo gate é só o antigo com a linha absoluta deslocada. Confirmar sempre comparando: (a) o texto
da linha reportada é idêntico a um padrão de achado já conhecido no arquivo (aqui,
`File.WriteAllTextAsync`), (b) o diff da PR mostra inserção de linhas acima do offset de
deslocamento observado.

**How to apply:** ao ver SCS0018 "novo" no gate, primeiro `git diff` o arquivo específico e
contar quantas linhas foram inseridas antes do número reportado. Se bater exatamente com o
deslocamento, é falso positivo — autorizado a editar `security-code-scan-baseline.json`
diretamente (padrão já validado repetidas vezes nesta sessão). Achado genuinamente novo (padrão
de código diferente, sem correlação de deslocamento) continua exigindo reporte, não edição.
