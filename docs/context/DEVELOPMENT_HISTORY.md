# Histórico de desenvolvimento

Marcos consolidados, em ordem cronológica. Registra **o que foi entregue e quando**, não
o estado atual (ver [CURRENT_STATE](CURRENT_STATE.md)) nem as justificativas (ver
[ARCHITECTURE_DECISIONS](ARCHITECTURE_DECISIONS.md)).

Entradas são acrescentadas ao concluir um marco; não se registra sessão, turno nem
handoff aqui — esses vivem nas Issues e PRs.

## MVP 1 — Telegram → D.A.N.T.E. → Claude/Codex → Telegram

Epic #1 · concluída em 2026-09-27

| Issue | Entrega | PR |
| --- | --- | --- |
| #2 | Estrutura inicial em .NET 10 (Worker + testes) | #10 |
| #3 | Infraestrutura segura de execução de processos | #11 |
| #4 | Runner do Codex CLI | #12 |
| #5 | Runner do Claude CLI | #13 |
| #6 | Bot Telegram por long polling e `/ping` | #14 |
| #7 | Allowlist de usuários do Telegram | #15 |
| #8 | Comandos `/codex` e `/claude` | #16 |
| #9 | Jobs em memória, `/status` e `/cancel` | #17 |

Resultado: um usuário autorizado executa Claude ou Codex localmente pelo Telegram e
recebe o resultado no chat.

## MVP 2 — Context-aware orchestration

Epic #18 · concluída em 2026-09-28

| Issue | Entrega | PR |
| --- | --- | --- |
| #19 | Catálogo persistente de repositórios | #25 |
| #20 | Comandos `/repos` e `/repo` | #26 |
| #24 | General Mode com workspace isolado | #27 |
| #21 | Roteamento opcional por `@alias` | #28 |
| #22 | Ambiente por repositório com referências seguras | #29 |
| #23 | Contexto de execução nos jobs e no `/status` | #30 |

Os PRs do MVP 2 foram empilhados na ordem acima e integrados por squash merge.

## Documentação do MVP 2

2026-09-28 · PR #31

README reescrito com visão geral, modos de execução, comandos, segurança, persistência,
arquitetura e limitações.

## Agent Harness v1

Epic #40 · em andamento desde 2026-09-28

| Issue | Entrega | PR |
| --- | --- | --- |
| #41 | Contrato neutro e adaptadores `CLAUDE.md`/`AGENTS.md` | #48 |
| #42 | Contexto persistente (`docs/context/`) | #49 |
| #43 | Protocolo de turno, handoff, Decision Locks e RECOVERY MODE | #50 |
| #45 | Backlog, labels `status:*` e templates | #51 |
| #44 | Skills `continuar-turno`/`encerrar-turno` para Claude e Codex | #52 |
| #46 | Protocolo implementador → revisor → correção | #53 |

PRs empilhados na ordem acima. Pendente: validação zero-chat em cenário real (#47).

## Interactive Agent Sessions

Epic #60 · Issues entregues em 2026-10-01; fechamento da Epic por decisão humana

| Issue | Entrega | PR |
| --- | --- | --- |
| #61 | Contrato de sessões interativas e spikes de protocolo | #69 |
| #62 | Infraestrutura de processo bidirecional | #70 |
| #63 | Driver interativo do Claude Code com `stream-json` | #71 |
| #64 | Driver interativo do Codex com `app-server` | #72 |
| #65 | `SessionRegistry` por usuário e contexto | #73 |
| #66 | Sessões no Telegram e entrega recuperável | #74 |
| #67 | Approvals, input e perfis de permissão no Telegram | #75 |
| #68 | Conversa session-first, validação end-to-end e documentação | #79 |

Resultado: mensagens comuns no Telegram são uma conversa com uma sessão interativa de Claude
ou Codex, com streaming, fila e steer, aprovação e input humanos, perfis `manual`/`auto`/`plan`,
interrupção e encerramento previsíveis; o one-shot continua disponível por `/claude` e
`/codex`.

## MVP 3 — Conversational Context

Epic #32 · Issues entregues em 2026-10-02; fechamento da Epic por decisão humana

| Issue | Entrega | PR |
| --- | --- | --- |
| #33 | Configurações persistentes do assistente | #55 |
| #34 | `/agent` para consultar e alterar o agente padrão | #56 |
| #35 | Mensagens sem slash command com o agente padrão | #57 |
| #36 | Repositório ativo por usuário com `/use` | #58 |
| #37 | Resolvedor unificado de agente e contexto | #90 |
| #38 | UX de contexto e jobs: sessão ativa em `/agent`/`/use` e aviso de override | #102 |

Resultado: o usuário conversa com o agente padrão no repositório ativo sem repetir
comandos, troca de agente ou repositório pontualmente com `/claude`, `/codex` e `@alias`
sem alterar as preferências, e consulta a qualquer momento o que está em uso.
