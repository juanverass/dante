# Estado atual

Retrato **substituível** do D.A.N.T.E. em `main`. Descreve o HEAD, não uma sessão: quem
muda o estado do projeto reescreve a parte afetada em vez de acrescentar entradas. O
histórico consolidado fica em [DEVELOPMENT_HISTORY](DEVELOPMENT_HISTORY.md).

Estado de Issues em andamento (worker, branch, handoff) **não** vive aqui: vive nas
próprias Issues e PRs do GitHub. Para o estado vivo do backlog, consulte o GitHub.

Última revisão: 2026-09-28, durante o Agent Harness v1 (Epic #40).

## Marcos

| Marco | Situação |
| --- | --- |
| MVP 1 — Telegram → D.A.N.T.E. → Claude/Codex → Telegram (Epic #1) | concluído |
| MVP 2 — Context-aware orchestration (Epic #18) | concluído |
| Agent Harness v1 (Epic #40) | em andamento |
| MVP 3 — Conversational Context (Epic #32) | planejado; nenhuma Issue iniciada |

## Funcionalidades disponíveis

- bot Telegram por long polling com allowlist de usuários;
- `/ping`;
- `/claude [@alias] <prompt>` e `/codex [@alias] <prompt>`;
- General Mode em workspace isolado e Repository Mode por `@alias`;
- `/repos` e `/repo add|show|remove`;
- `/repo env set|bind|list|remove` com segredos por referência ao host;
- `/status` com contexto de cada job e `/cancel <jobId>`.

Detalhes de uso: [README](../../README.md).

## Limitações atuais

- jobs e histórico somente em memória (perdidos ao reiniciar);
- Worker iniciado manualmente (execução automática como serviço: #39);
- sem agente padrão nem mensagens sem slash command (MVP 3);
- sem repositório ativo por usuário (MVP 3);
- sem worktrees, fila persistente ou execução concorrente isolada por Issue;
- sem CI no GitHub: validação é local.

## Em andamento

- **Epic #40 — Agent Harness v1**: contrato de agentes, contexto persistente,
  protocolo de turno/handoff, backlog, skills e protocolo de review. PRs empilhados a
  partir de #48.

Para saber quem está trabalhando em qual Issue, consulte os comentários de turno na
própria Issue.

## Build e testes

Estado conhecido em `main` (79674db):

```text
dotnet build Dante.sln   sucesso, sem avisos
dotnet test Dante.sln    67 aprovados, 0 falhas
```

## Próximos marcos

1. concluir e validar o Agent Harness v1 (#40), incluindo o teste zero-chat (#47);
2. MVP 3 — Conversational Context (#32): configurações persistentes (#33), `/agent`
   (#34), mensagens sem slash command (#35), `/use` (#36), resolvedor de agente e
   contexto (#37), UX (#38);
3. execução automática como serviço local (#39).
