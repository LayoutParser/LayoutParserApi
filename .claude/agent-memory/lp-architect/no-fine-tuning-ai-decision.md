---
name: no-fine-tuning-ai-decision
description: DECISÃO REVERTIDA em 2026-09-02 — ver fine-tuning-nichado-ollama-2026-09-02. Histórico da decisão original de usar só RAG+Ollama sem fine-tuning.
metadata:
  type: project
---

**REVERTIDA em 2026-09-02** — ver [[fine-tuning-nichado-ollama-2026-09-02]] e o ADR
`docs/architecture/adr-fine-tuning-nichado-ollama-2026-09-02.md`. O dono decidiu explicitamente
aceitar fine-tuning local mesmo em hardware fraco (aceita 1-2 meses de treino), revertendo a
decisão abaixo. Mantido como registro histórico.

---

Decisão original (2026-07-21): usar apenas RAG + Ollama local para geração de
diagnóstico/transformação, sem fine-tuning. Tamanho de modelo ficava bloqueado até confirmar
hardware real (ver [[dev-machine-gpu-constraints]] e [[production-server-hardware]]). Motivo: sem
GPU dedicada, treino era considerado inviável; RAG com modelo pequeno (1-2B) era o caminho
recomendado.
