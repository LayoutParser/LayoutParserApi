---
name: project-owner-and-tcc-context
description: O usuário é o dono do produto/decisor final de arquitetura e domínio fiscal deste projeto, que também é material de TCC — afeta o padrão de rigor esperado (ex.: checagem de near-duplicate em dado sintético, não é "só fixture de teste").
metadata:
  type: user
---

O usuário (Elson) é o dono do projeto LayoutParserApi e a autoridade final para
decisões de arquitetura, negócio e domínio fiscal (CFOP, NF-e, hardware de
produção etc.) - confirmado repetidamente no roadmap de IA de 2026-07-21
("dono do projeto" respondendo perguntas fechadas sobre LinkMapping vs. Rule
do CHAVEACESSO, sobre CFOP nas 98 regras DSL, sobre visibilidade do
AppConnector). Ele também opera o Claude Code diretamente (git user = Elson
Vinicius de Souza Lopes).

O projeto é material de **TCC** (trabalho de conclusão de curso) - mencionado
explicitamente em `ia-fiscal-diagnosis-vision.md` ao justificar por que dado
"sintético" precisa de checagem de near-duplicate contra o corpus real (item
4.4 do roadmap): "este projeto é material de TCC, 'sintético' tende a
circular mais solto que dado real controlado".

**Why:** contextualiza por que certas exigências de rigor aparecem no roadmap
mesmo em partes que poderiam parecer "só fixture de teste" ou "detalhe menor"
num projeto puramente comercial.

**How to apply:** ao sugerir atalhos ou trade-offs de qualidade/rigor (ex.:
pular validação, aceitar dado gerado sem checagem, documentação menos
rigorosa), considerar que o padrão esperado aqui inclui as exigências
acadêmicas do TCC, não só as necessidades mínimas de um MVP comercial.
