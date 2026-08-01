# Handoff 3/3 — Fechar o pipeline Job 1 → Job 2 (Cypress/Pollux)

> Para uma sessão nova de Claude Code. Escrito por `@lp-architect` (Aria), 2026-07-31. Este é o
> maior e mais arquitetural dos 3 handoffs — não é dívida pequena, é trabalho novo de várias
> partes coordenadas entre repos. **Não é bloqueio de nenhuma apresentação já feita** — a decisão
> tomada nesta semana foi rodar o Job 1 (métricas de geração) sozinho para a apresentação de
> sábado 2026-08-01, e desacoplar o Job 2 (validação Pollux) para depois. Este handoff é esse
> "depois".

## Leia primeiro, na íntegra

[`docs/architecture/handoff-job2-cypress-batch.md`](handoff-job2-cypress-batch.md) (480 linhas) —
escrito por `@lp-architect` em 2026-07-30, já tem o desenho completo: achados, contrato de
entrada (Job 1→2), contrato de saída (Job 2→API), como a spec descobre candidatos, isolamento de
falha, runbook de VM, e sequenciamento. **Não redesenhe do zero** — esse documento é a fonte da
verdade. Este handoff aqui é só: (a) o resumo do que falta, (b) o que mudou desde que foi escrito,
(c) por onde começar.

## O que mudou desde 2026-07-30 (importante, o doc original não sabe disso)

1. **O endpoint de ingestão que a §1/A4 recomendava (Opção A) já foi implementado** por outra
   sessão em paralelo — `POST /api/ai-metrics/generations/ingest`,
   `Services/Logging/AiMetricsIngestService.cs`, `Services/Interfaces/IAiMetricsIngestService.cs`.
   Passou por QA gate (veredito CONCERNS, não FAIL — 3 achados menores, ver Handoff 1/3). **Não
   recrie isso.** Mas ele está deployado e **sem produtor** — nada no `Scripts/vm/` chama esse
   endpoint ainda. Ligar o Job 2 a produzir para ele é exatamente o tipo de trabalho deste
   handoff.
2. **Uma ponte alternativa por cópia de arquivo também existe** (`layoutparserai.log`, 4ª fonte em
   `UnifiedLogReaderService`) — foi a escolha tática para sábado, por não exigir mudança no job da
   VM. **As duas pontes não podem estar ativas ao mesmo tempo** — o QA mediu que isso duplica a
   contagem de gerações no painel (confirmado: 108 gerações onde deveriam ser 54, taxa de
   aprovação caindo de 100% para 50% no card). Antes de ligar o produtor do endpoint `/ingest`,
   **desligue a tarefa agendada de cópia de arquivo** (`LayoutParser-PonteAiMetrics` no servidor
   de produção, se já tiver sido registrada) ou vice-versa. Escolha uma.
3. **O IP da VM (`172.25.32.31` no doc original) mudou para `172.25.32.3`** desde então, e já
   mudou 3 vezes no total por DHCP. Confirme o IP atual antes de qualquer runbook — não confie no
   número escrito em nenhum documento, nem neste.
4. **A rede entre a VM e o servidor de produção (`172.25.32.42`) está quebrada** (achado desta
   semana, causa raiz: adaptador Hyper-V usado como bridge pelo VirtualBox preso em IP
   link-local/APIPA, sem DHCP real — investigação parou no nível de "precisa de acesso
   físico/console à máquina host", não é algo resolvível por SSH). Isso bloqueia qualquer solução
   que exija a VM falar HTTP diretamente com a API de produção (o que inclui o Job 2 postando no
   endpoint `/ingest` **se a VM for a origem direta da chamada**). Verifique se esse bloqueio
   ainda existe antes de desenhar a topologia de rede do Job 2 — se persistir, o padrão terá que
   ser "workstation como relay" ou a chamada precisa sair de uma máquina que a rede realmente
   alcança.

## Os 3 achados estruturais do doc original (resumo — detalhe na íntegra)

- **A1 — Job 1 não persiste candidato nenhum**, só loga e descarta (`ai/XslSynth/Metrics/
  MetricsBatchRunner.cs`). Contrato de artefato já definido no doc original §2 (diretório
  `runs/<runId>/candidates/*.xml` + `manifest.json` com commit atômico). Trabalho de
  `@lp-parser-llm` (Lia).
- **A2 — o artefato do Job 1 é XSLT, o Pollux consome XML de NF-e pronto.** Falta o elo TXT de
  instância → `ROOT.xml` → aplicar XSLT → XML final. As peças já existem no `XslSynth`
  (`Core/RootTreeBuilder.cs`, `Core/XsltApplier.cs`) — é orquestração nova, não código do zero.
  **Decisão já tomada e registrada**: a API não ganha um endpoint de "aplicar XSLT arbitrário"
  (risco de XXE/SSRF sem autenticação) — o Job 1 aplica o XSLT ele mesmo e grava o XML pronto.
- **A3 — só 4 dos 54 pares do dataset são elegíveis** ao fluxo real do Pollux hoje (o resto é
  fora de escopo por direção do fluxo — SEFAZ→ERP não faz sentido submeter — ou por falta de
  fixture/TXT de instância compatível). **Isso é escopo correto, não defeito**: o valor do Job 2 é
  fechar o loop gerar→validar de ponta a ponta com o oráculo real, não cobrir volume. Não tente
  "consertar" isso sintetizando dados de instância — o doc original já avaliou e rejeitou essa
  ideia (dados sintéticos são rejeitados pela SEFAZ-fake por chave/DV/CNPJ inválidos, o resultado
  mediria qualidade do gerador de dados fake, não do XSLT gerado pela IA — ruído, não sinal).

## Por onde começar (sequenciamento, do doc original §8, ainda válido)

1. `@lp-parser-llm` (Lia) implementa a persistência de candidato no `MetricsBatchRunner` (A1) —
   sem isso não há nada para o resto da cadeia consumir. Contrato exato: doc original §2.
2. Em paralelo ou logo depois, o elo TXT→ROOT→XSLT→XML (A2) — também `ai/XslSynth`, mesma área de
   código de Lia.
3. Só depois disso faz sentido `@qa-cypress` (Cass, repo `LayoutParserCypress`) trabalhar na spec
   parametrizada que itera sobre os candidatos — contrato de descoberta no doc original §4,
   isolamento de falha e critério PASS/FAIL agregado no §5. Rejeição do Pollux é **dado**, não
   falha do job — não trate `cStat` de rejeição como erro de execução.
4. `@lp-devops` (Gage) provisiona o que a §6 pede na VM (Node/Cypress/Chrome-headless ou
   equivalente) — só depois dos itens 1-3 existirem, senão está provisionando para nada.
5. Ligar o produtor ao endpoint `/ingest` (já existe, ver "O que mudou" acima) — e desligar
   qualquer ponte por cópia de arquivo que ainda esteja ativa, para não duplicar contagem.

## Fora de escopo deste handoff

Hardening do endpoint `/ingest` em si (autenticação, teto de campo, idempotência) — isso é
Handoff 1/3, de `@lp-backend-dev`. Se você chegar no item 5 acima e o endpoint ainda não tiver
sido endurecido, verifique com quem estiver nesse handoff antes de apontar tráfego real de
produção para ele.

## Antes de terminar

```bash
dotnet build
dotnet test    # se o projeto de testes do Handoff 1/3 já existir
```

Commits Conventional, PT-BR. **Não faça `git push`** (nem neste repo nem no `LayoutParserCypress`)
— autoridade exclusiva de `@lp-devops`. Este é o handoff com mais partes móveis entre repos —
registre decisões de arquitetura que você tomar no mesmo padrão do doc original (`docs/
architecture/`), não só em memória de agente, para a próxima sessão fria conseguir continuar.
