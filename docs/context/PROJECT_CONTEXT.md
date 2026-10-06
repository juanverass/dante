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

A solução possui Domain/Application/Infrastructure e os hosts Worker/WebApi,
com dependências para o núcleo (AD-35). A migração do legado é incremental:
o comportamento Telegram permanece em `Dante.Worker` (Generic Host .NET),
com Brain opcional PostgreSQL e adapter de conversa natural. A WebApi é scaffold
independente com health, sem endpoints funcionais Brain.
Código novo do núcleo usa PT-BR e Guid Id (AD-36). Fluxo legado:

```text
Telegram (long polling)
   ↓
TelegramPollingService ── TelegramUserAuthorizer
   ├─ intenção Brain → TelegramBrain → Application → PostgreSQL opcional
   ↓ comando/agente
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
| `TelegramBrain` | `Telegram/` | Identidade/escopo Brain e roteamento de intenções naturais aos casos de uso; confirmação de alterações. |
| `RepositoryRegistry` | `Dante.Infrastructure/Contextos/` | Catálogo persistente de aliases, paths, GitHub e ambiente por repositório. |
| `AssistantSettingsStore` | `Dante.Infrastructure/Contextos/` | Agente padrão, repositório ativo e modo padrão por usuário, persistidos em `~/.dante/settings.json`. |
| `GeneralWorkspace` | `Dante.Infrastructure/Contextos/` | Diretório neutro para consultas gerais. |
| `JobRegistry` | `Jobs/` | Estado, contexto e cancelamento dos jobs, em memória. |
| `SessionRegistry` | `Sessions/` | Sessões interativas em memória: dono, contexto fixo, sessão ativa por usuário e roteamento de turnos aos drivers. |
| `ClaudeSessionDriver` / `CodexSessionDriver` | `Sessions/` | Traduzem `stream-json` (Claude) e `app-server` (Codex) para o contrato neutro de eventos. |
| `InteractiveAgentProcessLauncher` | `Dante.Infrastructure/Agentes/` | Processo interativo sem shell: saída incremental limitada, stdin serializado, parada sem órfãos. |
| `ClaudeRunner` / `CodexRunner` | `Dante.Infrastructure/Agentes/` | Argumentos fixos de cada CLI por modo. |
| `AgentProcessExecutor` | `Dante.Infrastructure/Agentes/` | Inicia o processo sem shell, filtra ambiente, captura saída, cancela a árvore. |
| `AgentExecutableResolver` | `Dante.Infrastructure/Agentes/` | Resolve apenas `claude`/`codex` em entradas absolutas do `PATH`. |
| `PlanilhasAppService` | `Dante.Application/Planilhas/` | Planilhas genéricas: cadastro, leitura, busca e escrita exata com auditoria. |
| `GoogleOAuthService` / `GoogleSheetsAdapter` | `Dante.Infrastructure/Google/` | Conta Google (OAuth local, credencial cifrada) e Sheets API v4. |
| `ServidorMcpDePlanilhas` | `Dante.Worker/Planilhas/` | Servidor MCP stdio que dá as ferramentas de planilha às sessões. |

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
- **PostgreSQL/pgvector** — canônico/índices opcionais do Brain; configuração externa, migrations e reindexação explícitas.
- **Google Sheets API** — capacidade genérica de planilhas (AD-55): OAuth local com cliente do usuário
  (`Google__ClientId`/`Google__ClientSecret`), exposta aos agentes pelo servidor MCP `dante_planilhas`.

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
- token do bot não aparece em logs;
- tokens do Google ficam fora do Telegram, de logs e da linha de comando dos agentes; o refresh token é cifrado em
  disco; escrita em planilha segue o modo de aprovação da sessão e nunca escolhe um alvo ambíguo em silêncio.

## Limites arquiteturais

Fora do sistema hoje, por decisão (ver [ARCHITECTURE_DECISIONS](ARCHITECTURE_DECISIONS.md)):

- fila persistente de jobs;
- webhook ou entrada HTTP funcional para executar agentes;
- execução de comandos arbitrários;
- worktrees automáticos e execução concorrente isolada por Issue;
- seleção automática de Issues e handoff automático pelo próprio D.A.N.T.E.

O desenvolvimento **do** D.A.N.T.E. por agentes (o Agent Harness em `docs/development/`)
é processo de engenharia do repositório, não funcionalidade do produto.

## Evolução aprovada: D.A.N.T.E. Brain (Epic #133)

A [AD-33](ARCHITECTURE_DECISIONS.md#ad-33--brain-como-núcleo-de-conhecimento-com-recuperação-seletiva-separado-da-sessão-e-do-histórico)
define conhecimento por espaço/projeto, proveniência/status/validade/sensibilidade,
separado de histórico e snapshot operacional. O núcleo e persistência opcional estão
implementados, com busca lexical/híbrida, fontes brutas rastreáveis, auditoria/exportação,
policy fail-closed e Context Builder neutro. Secret não entra em índices ou contexto
automático. Identidade e permissões são do adapter autorizado (AD-45), nunca do prompt.

Worker oferece intenções Brain por conversa natural (#157), incluindo captura em
candidato e confirmação antes de alterações. Sem banco/mensagem Brain, conversa com
agentes mantém seu fluxo. Seleção Brain não muda Repository Mode ou /use. Integração
automática dos pacotes aos drivers/sessões continua na #145, com métricas/E2E nas issues
próprias. Economia de tokens será medida junto de continuidade/qualidade, não presumida.
Não há ComfyUI, workers remotos, SaaS ou frontend completo nesta Epic.
Contrato detalhado em [Arquitetura](../maintainer/ARCHITECTURE.md#16-arquitetura-alvo-do-dante-brain-133-134).
