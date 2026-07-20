---
name: runner-tls-cert-incident
description: Runner GitHub Actions (NDD-NOT-10910) em loop de falha desde 2026-07-20 por certificado TLS expirado do lado do GitHub em pipelines*.actions.githubusercontent.com — não é problema da máquina dev
metadata:
  type: project
---

Diagnosticado em 2026-07-20: o serviço `actions.runner.LayoutParser.NDD-NOT-10910` (LocalSystem, ver [[dev-machine-infra]]) entrou em loop infinito de retry (30s) a partir de 2026-07-20T00:19:49Z, 100% das tentativas falhando com `AuthenticationException: ... NotTimeValid`.

**Causa raiz confirmada — não é a máquina dev:** o certificado TLS real servido por `pipelinesghubeus26.actions.githubusercontent.com` (e pelo host genérico `pipelines.actions.githubusercontent.com`) expirou em 2026-07-19T23:05:54Z (`CN=*.actions.githubusercontent.com`, emissor `Let's Encrypt R12` → `ISRG Root X1`, serial `05E5F127DCD8CC9F060D56DDEB33E55B42C8`). Confirmado idêntico em três stacks TLS independentes: WSL/openssl, .NET no contexto do usuário interativo `elson.lopes` (não LocalSystem), e `Invoke-WebRequest` direto no endpoint `_apis/connectionData` que o runner chama. `broker.actions.githubusercontent.com` (outro host do mesmo runner) tem certificado válido até outubro/2026 — a falha de rotação ficou isolada ao(s) edge(s) que atende(m) os hosts `pipelines*`.

**Why:** isso descarta de vez, com evidência, as hipóteses de "problema local" que pareciam plausíveis dado o timing (1 dia após a migração do runner para serviço LocalSystem em 2026-07-18): cache de certificado do SYSTEM, política de auto-update de root do Windows, EDR/Kaspersky Endpoint Security fazendo inspeção TLS (está instalado nessa máquina, mas o issuer observado é o Let's Encrypt R12 real — sem sinal de MITM). O `LocalMachine\Root`/`CA` dessa máquina tem dezenas de certificados expirados, mas são todos resíduo de PKI antigo da NDDigital (AD/`nddigital.local`, expirados entre 1999-2021) e nenhum tem qualquer relação com a cadeia Let's Encrypt/ISRG em uso real.

**How to apply:** se o runner voltar a falhar assim no futuro, checar PRIMEIRO se é isso — rápido de confirmar com `openssl s_client -connect <host>:443 -servername <host> | openssl x509 -noout -dates` comparando com `date -u`. Não sugerir mexer em cert store local, proxy ou reverter para `run.cmd` interativo — nenhum dos dois é a causa (o `Invoke-WebRequest` do usuário interativo falhou igual). Não há remediação local possível: é preciso aguardar o GitHub rotacionar o certificado nesse edge (o retry loop de 30s do runner reconecta sozinho assim que corrigido) ou abrir ticket em support.github.com com subject/issuer/serial/datas como evidência se a paralisação for urgente. NUNCA sugerir desabilitar validação de certificado como mitigação.
