---
name: gemini-decommission-secrets
description: Gemini/OpenAI decomissionados (2026-07-21) — chave do Gemini vira "revogar", não "rotacionar"; revogação é ação manual do dono do projeto (console do Google), fora do alcance do agente; rotação SQL segue à parte, bloqueada/DBA.
metadata:
  type: project
---

Em 2026-07-21, decisão de arquitetura do `@lp-architect` (Aria): abandonar Gemini e OpenAI por completo
como provedores de LLM — Ollama local assume 100% do papel (loop RAG gerar→validar→corrigir, sem
fine-tuning). Motivo de fundo: dado fiscal sensível não deve sair pra nuvem sem autorização explícita.
Detalhe completo na memória da Aria (`.claude/agent-memory/lp-architect/gemini-openai-decommission-decision.md`)
e no dispatch cross-agente `docs/architecture/ai-roadmap-dispatch.md` (Grupo 1 — item 1.6 era meu; os
demais itens do Grupo 1 são do Dex/Duda).

**O que fiz em `.claude/rules/security.md`:** status da chave do Gemini na tabela mudou de "rotacionar
(comprometida)" pra "Gemini decomissionado — revogar/desprovisionar, não rotacionar"; separei o checklist
de remediação (SQL continua "rotacionar", Gemini agora "revogar"); adicionei runbook de revogação manual
(Google AI Studio/Cloud Console) nos mesmos moldes do runbook de rotação da senha SQL já existente ali.

**Confirmado nesta sessão (vale pra próxima vez que alguém perguntar sem precisar re-investigar):**
- Nenhum serviço que consome a chave do Gemini está registrado no DI em `Program.cs` hoje (`GeminiAIService`,
  `SemanticAIGenerator` etc.) — risco atual de vazamento é baixo (bug de DI quebrado, não remediação
  deliberada), mas isso não reduz a urgência de revogar. Remoção do código morto é tarefa do
  `@lp-backend-dev` (Dex), não minha.
- **OpenAI nunca teve chave real versionada** neste repo: `OpenAI:ApiKey` está vazio (`""`) desde o commit
  que introduziu a seção; não há (e nunca houve) linha do OpenAI na tabela de segredos de `security.md`.
- Não há secret `GEMINI_API_KEY` (ou equivalente) em `.github/workflows/ci-dev.yml`/`deploy.yml` — nada a
  limpar do lado do GitHub Actions quando a chave for revogada.

**Why:** revogar a chave do Gemini exige acesso interativo ao console do Google (AI Studio/Cloud Console)
com a conta que a gerou — **fora do alcance do agente**, que não tem esse acesso. Não fingir que revoguei;
o runbook em `security.md` documenta os passos manuais pro dono do projeto executar.

**How to apply:** se o usuário perguntar "a chave do Gemini já foi revogada?" — a resposta mora na tabela
de status de `security.md` (fica 🔴 até o operador marcar ✅ manualmente ali; não há como eu verificar do
lado do provedor). **Não confundir com a rotação da senha SQL** — [[runner-isolation-rollout]] documenta
esse bloqueio separado (escalado ao DBA), que esta tarefa NÃO tocou nem resolveu.
