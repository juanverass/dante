# Módulos e responsabilidades

Priorizados pela Epic #296. Arquivos em `packages/engine/src` salvo indicação. Tamanhos em linhas **[C]**.

## 1. Chat

| Responsabilidade | Onde | Notas |
| --- | --- | --- |
| Postar mensagem, resolver menções, criar runs na mesma instrução SQL | `messages.ts` (1 992), `insertMessageWithRuns` | caminho quente: um `INSERT` que já inicia runs de menção/DM **[C]** |
| Canais, DMs, threads, categorias, permissões por canal | `channels.ts`, `dms.ts`, `threads.ts`, `channel-*.ts` | DM entre dois agentes tem chave `a:<id>\|a:<id>` (ADR 0034) **[D]** |
| Eventos de canal e de usuário | `events.ts` (865), `apps/server/src/realtime/ws.ts` | **[C]** |
| Recibos ("visto por", "escolhido por", "decidido por") | campo `receipts` das mensagens | `seenBy`, `pickedBy`, `handled`, `decidedBy` **[O]** |
| Notificações | `notifications.ts`, `notification-producers.ts` | caixa única, agrupamento, push **[C]** |

## 2. Registro de agentes

| Responsabilidade | Onde |
| --- | --- |
| CRUD, handle e handles anteriores, arquivamento | `agents.ts` (899), `agent-deletion.ts` |
| Grupos (setores) | `agent-groups.ts` (263), tabela `agent_groups`, `agents.group_id` |
| Audiência (quem fala com o agente) e papéis | `audience.ts`, `roles.ts`, `permissions.ts` |
| Configuração de cérebro (modelo, conta, esforço) | `brain-settings.ts`, `brains/select.ts` |
| Checklist de configuração e templates de time | `setup-checklist.ts`, `team-templates.ts` |
| Agente embutido ("braço direito") | `right-hand*.ts` |

Tabela `agents` **[C]** `packages/db/src/schema/agents.ts`: `name`, `handle`, `previous_handles`, `role`,
`instructions`, `model`, `credential_id`, `owner_user_id`, `monthly_budget_nano_usd`, `audience_*`, `consultable`,
`autonomy_brakes`, `merge_repos`, `approvals_level`, `brain`, `brain_model`, `brain_account_id`, `brain_effort`,
`machine_id`, `group_id`, `builtin`, `setup`, `appearance`.

## 3. Roteador

| Responsabilidade | Onde |
| --- | --- |
| Cadeia e regras, política `selectAgents`, gravação em `routing_decisions` | `router.ts` (1 801), `routeMessage` |
| Estratégia Jev (classificador externo) | `router-jev.ts` |
| Estratégia "plano do autor" (CLI do autor decide) | `router-plan.ts`, runner `brain-route.ts` |
| Juiz de destinatário (várias menções) | `judge.ts` (`personMentionAddressees`) |

## 4. Runtime do agente

| Responsabilidade | Onde |
| --- | --- |
| Criação de runs (menção, roteador, DM) | `dispatch.ts` (642), `createAgentRuns` |
| Estados e transições CAS | `runs.ts` (639) |
| Loop: contexto → cérebro → ferramentas → persistência | `executor.ts` (6 745) |
| Montagem de contexto | `context.ts`, `context-budget.ts` |
| Caixa de entrada da run (mensagens que chegam durante o trabalho) | `run-inbox.ts`, `steering.ts`, `live-steer.ts` |
| Fim de turno (continuar, esperar, perguntar, terminar) | `continuation.ts` (932), `decideTurnEnd` |
| Retomada após espera genérica | `run-resume.ts`, `waits.ts`, `run-hold.ts` |
| Trabalho longo em segundo plano | `works.ts` (1 535) |
| Limites de trabalho autônomo | `stretch.ts`, `stuck.ts`, `brakes.ts` |
| Cancelamento, "Continuar", motivo de parada | `cancel.ts`, `continue-run.ts`, `run-stopped.ts` |
| Cérebros | `brains/{model,claude-code,codex,select,prompt,types}.ts` |
| Model Gateway (orçamento, uso, custo) | `gateway.ts` (1 100) |

## 5. Computador do agente

| Responsabilidade | Onde |
| --- | --- |
| Pareamento, alvos, acesso | `machines.ts`, `machine-access.ts` |
| Hub de sockets e RPC entre réplicas | `machine-hub.ts` |
| Tarefas longas no computador | `machine-tasks.ts`, runner `tasks.ts` |
| Contas Claude/Codex por agente | `machine-accounts.ts`, runner `accounts.ts` |
| Isolamento e política de comandos | runner `command-gate.ts`, `scope-gate.ts`, `git-guard.ts`, adapters `command-policy.ts` |
| Lançamento de Claude Code e bridge MCP | runner `brain.ts` (`claudeBrainArgs`), `mcp.ts` (`McpBridge`) |
| Codex | runner `brain-codex-app.ts`, `brain-codex.ts` |
| Navegador e desktop | `browser/*`, `desktop/*`, `screen/*`, runner `browser.ts`, `desktop*.ts` |

## 6. Tarefas

| Responsabilidade | Onde |
| --- | --- |
| Issues, colunas, projetos, responsáveis múltiplos | `issues.ts`, `issue-*.ts` |
| Conversa da tarefa (canal `task`) | `task-conversation.ts`, ADR 0145 |
| Trabalho do agente na tarefa | `issue-work.ts` |
| Task Manager (runs ativas, passos, parar) | `task-manager.ts`, ADR 0128 |

## 7. Orquestração

| Responsabilidade | Onde |
| --- | --- |
| Consultas e repasses entre agentes | `consultations.ts` (2 330) |
| Repasse de rotina para conversa | `hand-off.ts` |
| Missões (plano vivo, worktree, revisões) | `missions*.ts`, `mission-*.ts` |
| Autoridade de quem está por trás da run | `authority.ts`, `requester.ts` |
| Política por tipo de run | `tools/run-policy.ts` (544) |

## 8. Aprovações

| Responsabilidade | Onde |
| --- | --- |
| Pedido, decisão, expiração, execução única | `approvals.ts` (1 439), `tools/gateway.ts` (`actionHash`, `executeApproved`) |
| Ordem de execução entre runs | `approval-order.ts`, ADR 0182 |
| "Aprovar sempre" | `approval-rules.ts` |
| Nível por agente | `tools/run-policy.ts` (`ApprovalsLevel`), ADR 0173 |
| Pedidos de aprovação de negócio e de segredo | `approval-requests.ts`, `secret-request-tool.ts` |

## 9. MCP

| Responsabilidade | Onde |
| --- | --- |
| Cliente MCP (conectores) e catálogo de escopos | `connectors/*` |
| Servidor MCP próprio para apps de IA | `apps/server/src` (rota `/mcp`), ADR 0119/0181 |
| Bridge MCP para CLIs no computador | runner `mcp.ts` |

## 10. Skills

`skills.ts`, `skill-tools.ts`, `skill-updates.ts`, `context/learned-skills.ts`; tabelas `skills`, `skill_files`,
`agent_skills` **[C][D]** ADR 0020/0120.

## 11. Memória

`context/*` (itens, resumos, reflexão, consolidação, feedback, propostas, aprendizado), `search.ts`, adaptador
`packages/adapters/src/search.ts` **[C]** ADR 0017/0028/0120/0167.

## 12. Eventos

`events.ts`; transporte `apps/server/src/realtime/*`; entre réplicas `NOTIFY` (payloads pequenos: o evento carrega
ids, o cliente relê) **[D]** ADR 0083 §3, 0010.

## 13. Notificações

`notifications.ts`, `push*.ts`, `web-push.ts`, `email*.ts` **[C]**.

## 14. Rotinas

`routines.ts`, `routine-firing.ts`, `routine-events.ts`, `routine-state.ts`, `routine-ledger.ts`,
`routine-brief.ts`, `routine-room.ts`, `routine-hooks.ts`, `cron.ts`, `rrule.ts` **[C]** ADR 0053/0106/0130.
