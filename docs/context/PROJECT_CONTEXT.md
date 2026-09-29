# Contexto do projeto

Contexto relativamente estável do D.A.N.T.E. Muda quando a arquitetura ou o propósito
mudam, não a cada Issue. O estado de agora está em [CURRENT_STATE](CURRENT_STATE.md);
as decisões e suas justificativas, em [ARCHITECTURE_DECISIONS](ARCHITECTURE_DECISIONS.md).

Quando este documento e o código divergirem, **o código e os testes vencem** — e este
documento deve ser corrigido.

## Visão

D.A.N.T.E. (*Distributed Agent Network for Task Execution*) é um orquestrador local em
.NET que permite controlar o **Claude Code** e o **Codex CLI** remotamente pelo
Telegram. O Telegram é só a interface: os agentes executam na máquina do usuário, com as
CLIs já instaladas e autenticadas localmente.

Objetivo de longo prazo: uma rede de agentes que executa tarefas de desenvolvimento —
consultas gerais, trabalho em repositórios, e futuramente seleção de Issues, handoff e
review — sem depender de o usuário estar no terminal.

## Arquitetura de alto nível

Um único processo `Dante.Worker` (Generic Host do .NET), sem banco e sem porta de
entrada:

```text
Telegram (long polling)
   ↓
TelegramPollingService ── TelegramUserAuthorizer
   ↓ comando
   ├─ sem @alias → GeneralWorkspace      → General Mode
   └─ com @alias → RepositoryRegistry    → Repository Mode
   ↓ contexto resolvido (JobExecutionContext)
JobRegistry
   ↓
ClaudeRunner / CodexRunner
   ↓
AgentProcessExecutor (sem shell, ArgumentList)
   ↓
claude / codex (processo filho)
```

## Componentes

| Componente | Arquivo | Responsabilidade |
| --- | --- | --- |
| Composição | `src/Dante.Worker/Program.cs` | Registro de DI e hosted services. |
| `TelegramPollingService` | `Telegram/` | Long polling, parsing de comandos, respostas, disparo de jobs. |
| `TelegramBotApi` | `Telegram/` | Cliente HTTP da Bot API (`getUpdates`, `sendMessage`). |
| `TelegramUserAuthorizer` | `Telegram/` | Allowlist por `message.from.id`; fail-closed. |
| `RepositoryRegistry` | `Repositories/` | Catálogo persistente de aliases, paths, GitHub e ambiente por repositório. |
| `GeneralWorkspace` | `Agents/` | Diretório neutro para consultas gerais. |
| `JobRegistry` | `Jobs/` | Estado, contexto e cancelamento dos jobs, em memória. |
| `ClaudeRunner` / `CodexRunner` | `Agents/` | Argumentos fixos de cada CLI por modo. |
| `AgentProcessExecutor` | `Agents/` | Inicia o processo sem shell, filtra ambiente, captura saída, cancela a árvore. |
| `AgentExecutableResolver` | `Agents/` | Resolve apenas `claude`/`codex` em entradas absolutas do `PATH`. |

## Stack

- .NET 10, C#, `Microsoft.Extensions.Hosting` (Worker SDK);
- xUnit nos testes (`tests/Dante.Tests`), com `tests/Dante.ProcessProbe` como processo
  auxiliar para testar execução real de processos;
- sem dependências de Telegram SDK: a Bot API é chamada via `HttpClient`.

## Modos de execução

**General Mode** — comando sem `@alias`. Executa em `~/.dante/workspaces/general` (ou
`DANTE_GENERAL_WORKSPACE`), fora de qualquer repositório cadastrado. O processo filho
recebe apenas variáveis básicas de sistema, rede e autenticação das CLIs. O Codex roda
com sandbox `workspace-write`; o Claude em modo restrito com ferramentas de arquivo.

**Repository Mode** — comando com `@alias` como primeiro argumento. Executa no path
cadastrado do repositório, com o ambiente configurado para aquele alias. Alias
desconhecido é erro; nunca há inferência de repositório.

## Integrações

- **Telegram Bot API** — long polling; token em `Telegram__BotToken`.
- **Claude Code CLI** e **Codex CLI** — executadas como processo filho; autenticação
  local da própria CLI (assinatura ou API key opcional).
- **Git** — usado pelo `RepositoryRegistry` para validar raiz do repositório e remote.

## Segurança fundamental

- somente IDs autorizados em `Telegram__AllowedUserIds` são atendidos; lista ausente ou
  inválida bloqueia tudo;
- o D.A.N.T.E. controla agentes, **não** é shell remoto: executáveis e argumentos
  internos são fixos, e o prompt é um único argumento de dados;
- paths de repositório são absolutos e validados como raiz Git;
- General Mode não herda ambiente nem diretório de projetos;
- segredos ficam no host e são referenciados (`/repo env bind`), nunca persistidos no
  catálogo nem enviados pelo Telegram; jobs com bindings omitem a saída do agente;
- token do bot não aparece em logs.

## Limites arquiteturais

Fora do sistema hoje, por decisão (ver [ARCHITECTURE_DECISIONS](ARCHITECTURE_DECISIONS.md)):

- banco de dados ou persistência remota;
- fila persistente de jobs;
- webhook ou porta de entrada;
- execução de comandos arbitrários;
- worktrees automáticos e execução concorrente isolada por Issue;
- seleção automática de Issues e handoff automático pelo próprio D.A.N.T.E.

O desenvolvimento **do** D.A.N.T.E. por agentes (o Agent Harness em `docs/development/`)
é processo de engenharia do repositório, não funcionalidade do produto.
