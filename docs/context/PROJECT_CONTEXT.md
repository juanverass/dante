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
   ├─ com @alias            → RepositoryRegistry → Repository Mode
   ├─ sem @alias, com /use  → repositório ativo  → Repository Mode
   └─ sem @alias, sem ativo → GeneralWorkspace   → General Mode
   ↓ contexto resolvido (JobExecutionContext)
   ├─ mensagem comum (conversa, AD-23)        ├─ /claude, /codex (one-shot)
   ↓                                          ↓
SessionRegistry                            JobRegistry
   ↓                                          ↓
ClaudeSessionDriver / CodexSessionDriver   ClaudeRunner / CodexRunner
   ↓                                          ↓
InteractiveAgentProcess (stdin/stdout)     AgentProcessExecutor (sem shell, ArgumentList)
   ↓                                          ↓
claude stream-json / codex app-server      claude / codex (processo filho)
   ↓ eventos                                  ↓ resultado
TelegramDeliveryService ──────────────────────┘
   ↓
Telegram
```

A conversa é o caminho padrão: uma sessão interativa por conversa, com um processo de agente
vivo que recebe todos os turnos. O one-shot continua disponível por comando explícito.

## Componentes

| Componente | Arquivo | Responsabilidade |
| --- | --- | --- |
| Composição | `src/Dante.Worker/Program.cs` | Registro de DI e hosted services. |
| `TelegramPollingService` | `Telegram/` | Long polling, parsing de comandos, respostas, disparo de jobs. |
| `TelegramBotApi` | `Telegram/` | Cliente HTTP da Bot API (`getUpdates`, `sendMessage`, `sendChatAction`). |
| `TelegramDeliveryService` | `Telegram/` | Agrupa, formata, redige e entrega eventos de sessão e resultados de jobs, com retry, `/resend` e indicador de digitação. |
| `TelegramUserAuthorizer` | `Telegram/` | Allowlist por `message.from.id`; fail-closed. |
| `RepositoryRegistry` | `Repositories/` | Catálogo persistente de aliases, paths, GitHub e ambiente por repositório. |
| `AssistantSettingsStore` | `Settings/` | Agente padrão, repositório ativo e modo padrão por usuário, persistidos em `~/.dante/settings.json`. |
| `GeneralWorkspace` | `Agents/` | Diretório neutro para consultas gerais. |
| `JobRegistry` | `Jobs/` | Estado, contexto e cancelamento dos jobs, em memória. |
| `SessionRegistry` | `Sessions/` | Sessões interativas em memória: dono, contexto fixo, sessão ativa por usuário e roteamento de turnos aos drivers. |
| `ClaudeSessionDriver` / `CodexSessionDriver` | `Sessions/` | Traduzem `stream-json` (Claude) e `app-server` (Codex) para o contrato neutro de eventos. |
| `InteractiveAgentProcessLauncher` | `Agents/` | Processo interativo sem shell: saída incremental limitada, stdin serializado, parada sem órfãos. |
| `ClaudeRunner` / `CodexRunner` | `Agents/` | Argumentos fixos de cada CLI por modo. |
| `AgentProcessExecutor` | `Agents/` | Inicia o processo sem shell, filtra ambiente, captura saída, cancela a árvore. |
| `AgentExecutableResolver` | `Agents/` | Resolve apenas `claude`/`codex` em entradas absolutas do `PATH`. |

## Stack

- .NET 10, C#, `Microsoft.Extensions.Hosting` (Worker SDK);
- xUnit nos testes (`tests/Dante.Tests`), com `tests/Dante.ProcessProbe` como processo
  auxiliar para testar execução real de processos;
- sem dependências de Telegram SDK: a Bot API é chamada via `HttpClient`.

## Modos de execução

**General Mode** — execução sem `@alias` e sem repositório ativo. Executa em `~/.dante/workspaces/general` (ou
`DANTE_GENERAL_WORKSPACE`), fora de qualquer repositório cadastrado. O processo filho
recebe apenas variáveis básicas de sistema, rede e autenticação das CLIs. O Codex roda
com sandbox `workspace-write`; o Claude em modo restrito com ferramentas de arquivo.

**Repository Mode** — comando com `@alias` como primeiro argumento ou, sem `@alias`,
com repositório ativo selecionado pelo usuário via `/use @alias` (persistido por
Telegram User ID; `@alias` explícito vale só para aquela execução). Executa no path
cadastrado do repositório, com o ambiente configurado para aquele alias. Alias
desconhecido é erro; repositório ativo que saiu do catálogo recusa a execução em vez de
cair para General Mode; nunca há inferência de repositório pelo texto do prompt.

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
- eventos de sessão passam pela redaction a cada parte entregue; approvals e input só são
  aceitos do dono da sessão, no turno e na solicitação certos; acesso irrestrito (`full`) não
  é oferecido;
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
