# Estado atual

Retrato **substituível** do D.A.N.T.E. em `main`. Descreve o HEAD, não uma sessão: quem
muda o estado do projeto reescreve a parte afetada em vez de acrescentar entradas. O
histórico consolidado fica em [DEVELOPMENT_HISTORY](DEVELOPMENT_HISTORY.md).

Estado de Issues em andamento (worker, branch, handoff) **não** vive aqui: vive nas
próprias Issues e PRs do GitHub. Para o estado vivo do backlog, consulte o GitHub.

Última revisão: 2026-10-01, com aprovações inline no Telegram (#80).

## Marcos

| Marco | Situação |
| --- | --- |
| MVP 1 — Telegram → D.A.N.T.E. → Claude/Codex → Telegram (Epic #1) | concluído |
| MVP 2 — Context-aware orchestration (Epic #18) | concluído |
| Agent Harness v1 (Epic #40) | concluído |
| MVP 3 — Conversational Context (Epic #32) | pausado pela Priority Lock da Epic #60, após #33–#36 |
| Interactive Agent Sessions (Epic #60) | Issues #61–#68 entregues; fechamento da Epic e fim da Priority Lock são decisão humana |

## Funcionalidades disponíveis

- bot Telegram por long polling com allowlist de usuários;
- `/ping`;
- `/claude [@alias] <prompt>` e `/codex [@alias] <prompt>`;
- General Mode em workspace isolado e Repository Mode por `@alias`;
- `/repos` e `/repo add|show|remove`;
- `/repo env set|bind|list|remove` com segredos por referência ao host;
- `/status` com contexto de cada job e `/cancel <jobId>`;
- configurações do assistente em `~/.dante/settings.json` (agente padrão, Claude quando
  não configurado; repositório ativo, modo padrão, modelos e esforço por usuário);
- `/agent` e `/agent set claude|codex` para consultar e alterar o agente padrão;
- `/model` e `/model claude|codex [<modelo>|default]`: preferência persistida por usuário
  e agente, validada no catálogo das CLIs (cache de dez minutos), usada nas novas sessões
  e nos one-shot explícitos; `/session start ... model=<modelo>` permite override local
  e `model=default` retorna ao padrão da CLI naquela sessão. Modelo fica fixo e visível
  em `/status`; preferência indisponível recusa novas execuções e orienta nova escolha
  ou retorno ao default, sem troca silenciosa (AD-25);
- `/effort` e `/effort claude|codex [<nível>|default]`: preferência por usuário/agente
  validada nos níveis anunciados pela CLI para o modelo escolhido, aplicada às novas
  sessões e one-shot; override `effort=<nível>|default` em `/session start`, esforço
  fixo entre turnos e visível em `/status`, independente de permissões (AD-26);
- conversa session-first (AD-23): mensagem sem slash command abre uma sessão interativa do
  agente padrão no contexto atual, ou continua a sessão ativa, e mostra essencialmente a
  resposta do agente, com progresso curto e indicador "digitando…"; `/claude` e `/codex`
  seguem como execução one-shot com Job ID; slash command desconhecido responde erro e não
  inicia agente;
- `/use @alias`, `/use general` e `/use`: repositório ativo por usuário, persistido em
  `~/.dante/settings.json` e usado por toda execução sem `@alias` explícito (AD-14).
- `/session start|list|select|stop|close` e `/steer`, com fila durante o turno e eventos
  agrupados no Telegram em linhas inteiras; `/agent set` e `/use` avisam quando a sessão ativa
  continua com outro agente ou contexto;
- entrega de resultados de jobs e eventos de sessão com retry/backoff, estado independente
  da execução em `/status` e recuperação de partes pendentes por `/resend` (AD-21).
- `/mode` consulta e escolhe o modo de trabalho das novas sessões — `manual` (aprovação), `auto`
  (automático) ou `plan` (planejamento) —, com padrão por usuário persistido em
  `~/.dante/settings.json`; `/session start` aceita modo explícito, a sessão mantém o modo até ser
  encerrada, `/status`, `/mode` e `/session start` o mostram, sem aviso na abertura implícita, e
  modo não suportado pelo agente é recusado antes de iniciar (AD-24). `/permissions` segue como interface de baixo nível do mesmo
  padrão. `/approve`, `/approve-session`, `/deny` e `/input` respondem a
  solicitações correlacionadas por sessão, turno e request, com expiração em cinco minutos
  e estado pendente em `/status` (AD-22).

- aprovações oferecem botões inline com validação de dono, turno, request e expiração;
  `/approve`, `/approve-session` e `/deny` seguem disponíveis como fallback textual;

Detalhes de uso: [README](../../README.md).

## Limitações atuais

- jobs e histórico somente em memória (perdidos ao reiniciar);
- Worker iniciado manualmente (execução automática como serviço: #39);
- precedência de agente e contexto ainda sem resolvedor único (#37);
- sem worktrees, fila persistente ou execução concorrente isolada por Issue;
- sessões interativas e resultados recentes de entrega ficam apenas em memória; ao
  reiniciar o Worker, sessões e saídas pendentes não podem ser recuperadas;
- perfil `full` não é oferecido, pois não há mapeamento comum validado entre as CLIs;
- input humano do Codex só aparece no perfil `plan` (limitação do `app-server`, AD-19);
- streaming longo ainda chega em várias mensagens (uma por lote de linhas); editar uma única
  mensagem progressivamente não está implementado;
- a validação real foi feita contra as CLIs instaladas com a API do Telegram simulada; o
  dogfooding pelo Telegram real depende do bot do usuário;
- sem CI no GitHub: validação é local.

## Em andamento

- **Epic #60 — Interactive Agent Sessions**: todas as Issues filhas entregues — contrato e
  spikes (#61), processo bidirecional (#62), drivers Claude (#63) e Codex (#64),
  `SessionRegistry` (#65), interface Telegram (#66), approvals, input e perfis (#67) e conversa
  session-first com validação end-to-end (#68). A Priority Lock vale até o fechamento da Epic
  por decisão humana;
- **Epic #32 — MVP 3 (Conversational Context)**: pausada após #33–#36; #37 e #38
  bloqueadas pela #60.

Para saber quem está trabalhando em qual Issue, consulte os comentários de turno na
própria Issue.

## Build e testes

Estado conhecido com #80:

```text
dotnet build Dante.sln   sucesso, sem avisos
dotnet test Dante.sln    329 testes aprovados
```

`InteractiveSessionEndToEndTests` exercita o caminho interativo completo (Telegram →
`SessionRegistry` → drivers reais → CLIs simuladas do `Dante.ProcessProbe`).

`InteractiveAgentProcessTests.GracefulExitDoesNotLeaveOrphanedChildProcess` (#62) pode ser
intermitente na suíte completa em WSL2 e passa isolado; já falhava assim antes da #63.

## Próximos marcos

1. fechamento da Epic #60 após o dogfooding pelo Telegram real (decisão humana);
2. retomada do MVP 3 (#32): resolvedor de agente e contexto (#37), UX (#38);
3. execução automática como serviço local (#39).
