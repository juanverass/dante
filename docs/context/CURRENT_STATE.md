# Estado atual

Retrato **substituível** do D.A.N.T.E. em `main`. Descreve o HEAD, não uma sessão: quem
muda o estado do projeto reescreve a parte afetada em vez de acrescentar entradas. O
histórico consolidado fica em [DEVELOPMENT_HISTORY](DEVELOPMENT_HISTORY.md).

Estado de Issues em andamento (worker, branch, handoff) **não** vive aqui: vive nas
próprias Issues e PRs do GitHub. Para o estado vivo do backlog, consulte o GitHub.

Última revisão: 2026-09-30, com o contrato de sessões interativas (#61).

## Marcos

| Marco | Situação |
| --- | --- |
| MVP 1 — Telegram → D.A.N.T.E. → Claude/Codex → Telegram (Epic #1) | concluído |
| MVP 2 — Context-aware orchestration (Epic #18) | concluído |
| Agent Harness v1 (Epic #40) | concluído |
| MVP 3 — Conversational Context (Epic #32) | pausado pela Priority Lock da Epic #60, após #33–#36 |
| Interactive Agent Sessions (Epic #60) | em andamento: contrato de sessão e spikes de protocolo (#61) |

## Funcionalidades disponíveis

- bot Telegram por long polling com allowlist de usuários;
- `/ping`;
- `/claude [@alias] <prompt>` e `/codex [@alias] <prompt>`;
- General Mode em workspace isolado e Repository Mode por `@alias`;
- `/repos` e `/repo add|show|remove`;
- `/repo env set|bind|list|remove` com segredos por referência ao host;
- `/status` com contexto de cada job e `/cancel <jobId>`;
- configurações do assistente em `~/.dante/settings.json` (agente padrão, Claude quando
  não configurado);
- `/agent` e `/agent set claude|codex` para consultar e alterar o agente padrão;
- mensagens sem slash command executadas pelo agente padrão (`/claude` e `/codex` como
  override de uma execução); slash command desconhecido responde erro e não inicia agente;
- `/use @alias`, `/use general` e `/use`: repositório ativo por usuário, persistido em
  `~/.dante/settings.json` e usado por toda execução sem `@alias` explícito (AD-14).

Detalhes de uso: [README](../../README.md).

## Limitações atuais

- jobs e histórico somente em memória (perdidos ao reiniciar);
- Worker iniciado manualmente (execução automática como serviço: #39);
- precedência de agente e contexto ainda sem resolvedor único (#37);
- sem worktrees, fila persistente ou execução concorrente isolada por Issue;
- sessões interativas só existem como contrato (`Sessions/`, AD-15/AD-16): ainda sem
  drivers, registry nem integração com o Telegram, que segue só com jobs one-shot;
- sem CI no GitHub: validação é local.

## Em andamento

- **Epic #60 — Interactive Agent Sessions**: contrato `AgentSession`/`AgentEvent`/
  `IAgentSessionDriver` e spikes de Claude `stream-json` e Codex `app-server` (#61,
  `docs/spikes/interactive-protocols/`). Priority Lock: só Issues da #60 avançam;
- **Epic #32 — MVP 3 (Conversational Context)**: pausada após #33–#36; #37 e #38
  bloqueadas pela #60.

Para saber quem está trabalhando em qual Issue, consulte os comentários de turno na
própria Issue.

## Build e testes

Estado conhecido com #61:

```text
dotnet build Dante.sln   sucesso, sem avisos
dotnet test Dante.sln    140 aprovados, 0 falhas
```

## Próximos marcos

1. Interactive Agent Sessions (#60): infraestrutura bidirecional (#62), drivers Claude
   (#63) e Codex (#64), SessionRegistry (#65), streaming no Telegram (#66), approvals e
   perfis (#67), validação end-to-end (#68);
2. retomada do MVP 3 (#32): resolvedor de agente e contexto (#37), UX (#38);
3. execução automática como serviço local (#39).
