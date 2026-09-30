# Estado atual

Retrato **substituível** do D.A.N.T.E. em `main`. Descreve o HEAD, não uma sessão: quem
muda o estado do projeto reescreve a parte afetada em vez de acrescentar entradas. O
histórico consolidado fica em [DEVELOPMENT_HISTORY](DEVELOPMENT_HISTORY.md).

Estado de Issues em andamento (worker, branch, handoff) **não** vive aqui: vive nas
próprias Issues e PRs do GitHub. Para o estado vivo do backlog, consulte o GitHub.

Última revisão: 2026-09-30, com as mensagens sem slash command (#35).

## Marcos

| Marco | Situação |
| --- | --- |
| MVP 1 — Telegram → D.A.N.T.E. → Claude/Codex → Telegram (Epic #1) | concluído |
| MVP 2 — Context-aware orchestration (Epic #18) | concluído |
| Agent Harness v1 (Epic #40) | concluído |
| MVP 3 — Conversational Context (Epic #32) | em andamento: configurações persistentes (#33), `/agent` (#34) e mensagens sem slash command (#35) |

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
  override de uma execução); slash command desconhecido responde erro e não inicia agente.

Detalhes de uso: [README](../../README.md).

## Limitações atuais

- jobs e histórico somente em memória (perdidos ao reiniciar);
- Worker iniciado manualmente (execução automática como serviço: #39);
- sem repositório ativo por usuário (MVP 3);
- sem worktrees, fila persistente ou execução concorrente isolada por Issue;
- sem CI no GitHub: validação é local.

## Em andamento

- **Epic #32 — MVP 3 (Conversational Context)**: configurações persistentes (#33),
  `/agent` (#34) e mensagens sem slash command (#35); demais Issues da Epic seguem no backlog.

Para saber quem está trabalhando em qual Issue, consulte os comentários de turno na
própria Issue.

## Build e testes

Estado conhecido com #35:

```text
dotnet build Dante.sln   sucesso, sem avisos
dotnet test Dante.sln    106 aprovados, 0 falhas
```

## Próximos marcos

1. MVP 3 — Conversational Context (#32): após as configurações persistentes (#33), `/agent`
   (#34) e mensagens sem slash command (#35), `/use` (#36), resolvedor de agente e
   contexto (#37), UX (#38);
2. execução automática como serviço local (#39).
