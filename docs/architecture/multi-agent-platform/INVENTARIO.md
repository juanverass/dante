# Inventário da arquitetura atual

Retrato do código em `main` @ `79c1d01` (2026-11-16), conferido em código e testes, não só na documentação.
Serve de base para a proposta: tudo o que está marcado **reutilizar** permanece; só muda o que está em
**o que precisa mudar**.

## Camadas e hosts

| Projeto | Responsabilidade real hoje | Observações |
| --- | --- | --- |
| `Dante.Domain` | Entidades do Brain (`Conhecimento`, `EspacoDeConhecimento`, `Projeto`, candidatos, relações, fontes); `EntidadeBase` (Guid, AD-36); enums legados `AgentKind` e `AgentPermissionProfile` | Não há entidade de agente, sessão, job ou execução. |
| `Dante.Application` | Casos de uso do Brain, planilhas, `AgentContextResolver` (AD-27), `JobExecutionContext`, `AgentModelSelection`, portas de contexto e checkout (`ILeitorDeCheckouts`) | Feature-first (AD-49), validators estáticos (AD-52), composição em `AddApplication` (AD-54). |
| `Dante.Infrastructure` | Processos das CLIs (`AgentProcessExecutor`, `InteractiveAgentProcessLauncher`, contenção systemd AD-63), catálogos JSON (`RepositoryRegistry`, `AssistantSettingsStore`), EF Core do Brain, Google | `DanteDbContext` tem schema padrão `brain_data`, filtros globais e validação de escrita do Brain. |
| `Dante.Worker` | Telegram, `SessionRegistry`, `AgentSession`, drivers Claude/Codex, `JobRegistry`, mídia, artefatos, MCP do Brain e de planilhas | Legado ainda no host (AD-35/38): sessões e drivers não migraram para Application/Infrastructure. |
| `Dante.WebApi` | Scaffold com health, ProblemDetails e OpenAPI (AD-35, #170) | Sem endpoint funcional, sem acesso a repositórios. |

## Componentes

| Componente | Responsabilidade | Estado | Contrato público | Testes | Reutilizar | O que precisa mudar |
| --- | --- | --- | --- | --- | --- | --- |
| `JobRegistry` (`Worker/Jobs`) | Jobs one-shot: criação, início, cancelamento, conclusão; 20 encerrados | Em memória; ids `J000001` por contador que reinicia com o processo (AD-06) | `Create`, `TryStart`, `TryCancel`, `Complete`, `GetVisible`; `JobSnapshot` | `JobRegistryTests`, `TelegramJobCommandTests` | Como índice de runtime (token de cancelamento do processo vivo) | Deixa de ser fonte de verdade: o job vira `Execucao` do tipo `Avulsa` no ledger. |
| `SessionRegistry` (`Worker/Sessions`, 1 111 linhas) | Catálogo de sessões vivas, dono, sessão ativa, fila de turnos, roteamento de approvals, bombeamento de eventos | Em memória; no shutdown marca tudo `Failed` (AD-20) | `StartAsync`, `SubmitAsync`, `InterruptAsync`, `CloseAsync`, `RespondAsync`, `ChangeModeAsync`, `Clear/CompactContextAsync`, `Select`, `List` | `SessionRegistryTests`, `InteractiveSessionEndToEndTests` | Integralmente, como runtime das sessões vivas | Publicar metadados da sessão no ledger (porta nova); receber o contexto a partir de uma versão de agente; ids estáveis entre reinícios. |
| `AgentSession` | Máquina de estados neutra da sessão e carimbo de ids S/T/R | Estável (AD-16) | `Apply(AgentEvent)`, estados `Starting → Idle ⇄ Running ⇄ WaitingForUser → Closed/Failed` | `AgentSessionTests` | Integralmente | Nada no contrato; só um projetor lê seus eventos. |
| `AgentEvent` | Fluxo neutro de eventos (turno, delta, ferramenta, arquivo, artefato, approval, input, uso de tokens) | Estável | records em `AgentEvent.cs` | testes de drivers e sessão | Integralmente, como fonte dos eventos persistidos | Nenhuma alteração; persistência por projeção (sem deltas). |
| `IAgentSessionDriver` + `ClaudeSessionDriver` / `CodexSessionDriver` | Tradução dos protocolos stream-json e app-server; capacidades declaradas | Estáveis (AD-15/18/19/24/32/58) | `IAgentSessionDriver`, `AgentSessionStartOptions`, `AgentDriverCapabilities` | `ClaudeSessionDriverTests`, `CodexSessionDriverTests`, `Live*EvidenceTests` | Integralmente | Nenhuma mudança de protocolo na primeira fase. Instruções do agente registrado entram por `ContextInstructions`, que já existe. |
| `ClaudeRunner` / `CodexRunner` + `AgentProcessExecutor` | One-shot sem shell, ambiente filtrado | Estáveis (AD-02/03/09/10) | `IClaudeRunner`, `ICodexRunner` | `*RunnerTests`, `AgentProcessExecutorTests` | Integralmente | Chamados por um executor de etapa em vez de diretamente pelo Telegram. |
| `InteractiveAgentProcess(Launcher)` + contenção | Processo vivo, stdin serializado, parada sem órfãos; scope `dante-agent-<guid>.scope` em modo estrito | AD-17 + AD-63 (estrito por padrão no código; implantação no homelab sob autorização própria) | `IInteractiveAgentProcessLauncher` | `InteractiveAgentProcessTests`, `ContencaoNativaTests` | Integralmente | **Lacuna**: não existe varredura de scopes órfãos na inicialização. Com o Worker morto à força, o scope (fora do cgroup do Worker) pode sobreviver. A recuperação precisa parar essas unidades. |
| `AgentContextResolver` | Precedência única de agente e contexto | Estável (AD-27, AD-58) | `Resolve(...)` → agente, `JobExecutionContext`, ambiente, origem | `AgentContextResolverTests` (tabela) | Integralmente | Ganha uma entrada: agente registrado por handle (sem mudar o significado de `@alias`). |
| `AssistantSettingsStore` | Agente padrão, repositório ativo, modo, modelo e esforço por usuário em `~/.dante/settings.json` | Estável (AD-13/14/24/25/26) | `IPreferenciasDoAssistente` | `AssistantSettingsStoreTests` | Integralmente: alimenta os agentes embutidos `claude` e `codex` | Nada. |
| `RepositoryRegistry` + vínculos | Catálogo de aliases, ambiente e vínculos (AD-07/10/58) | Estável | `ICatalogoDeRepositorios` | `RepositoryRegistryTests` | Integralmente | Nada no catálogo; worktrees são derivados do repositório cadastrado. |
| `ILeitorDeCheckouts` | Estado git somente leitura (branch, alterações, commits não enviados) | Estável (AD-58) | porta na Application | `LeitorDeCheckoutsGitTests` | Integralmente, inclusive para auditar worktrees e retomar etapas interrompidas | Nada. |
| Approvals da CLI | `can_use_tool` (Claude) e requests do app-server (Codex), correlacionados S/T/R, 5 min, uma resposta | Estável (AD-22/30) | `RespondAsync`, `AgentPendingRequest` | `TelegramInlineApprovalTests`, `SessionRegistryTests` | Integralmente para ações **da CLI** | Registro de auditoria persistido. Não há aprovação de ações **do D.A.N.T.E.** com hash. |
| `TelegramDeliveryService` | Entrega com retry, `/resend`, redaction, omissão com segredos | Estável (AD-21/23) | `IAgentSessionEventSink` (decorado por showcase e métricas) | `TelegramDeliveryServiceTests` | Integralmente | Recebe também eventos de execução (novo sink), sem mudar o de sessão. |
| Servidores MCP (`dante_brain`, `dante_planilhas`) | Ferramentas do D.A.N.T.E. para as CLIs; Brain por named pipe exclusivo da sessão (AD-56) | Estáveis (AD-55/56/57) | `IProvedorDeFerramentas`, `CompositorDeFerramentas`, `AgentToolServer` | `BrainMcp*Tests`, planilhas | O padrão IPC por sessão é exatamente o canal do futuro `dante_coordenacao` | Concessão por versão de agente em vez de conjunto fixo por usuário. |
| Brain (Braúna) | Conhecimento, contexto seletivo, captura com confirmação (AD-33…65) | Implementado, PostgreSQL opcional | AppServices da Application; MCP | dezenas de suítes | Como provedor de contexto | Nada. Não coordena. |
| EF Core / UoW | `DanteDbContext`, `Repository<T>`, `UnitOfWork`, xmin como `Versao` (AD-43) | Implementado para o Brain | `IRepository<T>`, `IUnitOfWork` | `PersistenciaTests`, `BrainEfTests` | Padrões (EntidadeConfiguration, xmin, UoW, migrations explícitas, `--brain migrate`) | O contexto atual exige `AutorizacaoDoBrain` e aplica filtros do Brain: execuções precisam de **um DbContext próprio** no mesmo banco. |
| Agent Harness | Contrato, turnos, handoff, review (`docs/development`) | Processo de engenharia do repositório, não produto (PROJECT_CONTEXT) | `## TURNO ASSUMIDO`, `## HANDOFF`, `## REVIEW` | — | Como **especificação** do pipeline implementar → revisar → corrigir que o produto passará a executar | Nada no harness; o produto o representa como roteiro. |
| Worktrees | — | **Não existem no código.** `dante-wt-*` do homelab são prática manual | — | — | — | Novo componente; exige AD que complemente a AD-58. |

## Epics de referência

| Epic | Entregou | Situação |
| --- | --- | --- |
| #32 MVP 3 — Conversational Context | Resolvedor único (AD-27), repositório ativo (AD-14), settings (AD-13), avisos de override | Fechada |
| #60 Interactive Agent Sessions | Drivers, sessão neutra, `SessionRegistry`, entrega, approvals/input, conversa session-first (AD-15…23) | Fechada |
| #40 Agent Harness v1 | Contrato e protocolos de turno/review | Fechada |
| #234 CI | Porta de provedor de CI (aberta) | Fonte futura de eventos para rotinas; não é dependência |

## ADRs que a plataforma toca

| AD | Determina hoje | Situação proposta |
| --- | --- | --- |
| AD-06 | Jobs só em memória, 20 encerrados, perdidos no reinício | **Substituída** pela AD-66 proposta quando o ledger virar fonte de verdade |
| AD-11 | Contexto imutável por job | **Mantida** e estendida à execução e à etapa |
| AD-15/16 | Um processo por sessão; sessão não é job; contrato neutro | **Mantidas**. Execução é um terceiro conceito, não substitui sessão nem job |
| AD-17/63 | Processo sem órfãos; contenção estrita | **Mantidas**. Complemento: varredura de scopes órfãos na inicialização |
| AD-20 | `SessionRegistry` em memória, contexto fixo, sessão ativa, nada sobrevive ao reinício | **Mantida** para o runtime; **complementada**: os metadados persistem e a sessão aparece como `Interrompida` após reinício em vez de "não encontrada" |
| AD-21/22/23 | Entrega, approvals da CLI, conversa session-first | **Mantidas** |
| AD-24/25/26 | Modo, modelo e esforço por usuário e agente da CLI | **Mantidas**; viram a configuração dos agentes embutidos `claude`/`codex` |
| AD-27 | Resolvedor único; `@alias` = repositório | **Mantida**; complementada por handle de agente sem `@` |
| AD-33/46/56 | Brain separado de sessão e histórico; contexto por MCP | **Mantidas**; Brain não coordena |
| AD-34 | PostgreSQL do Brain; "não migrar estado volátil de sessões/jobs **nesta Epic**" | Escopo da Epic #133. A AD-66 proposta decide explicitamente usar o mesmo banco para execuções |
| AD-35/36/43/54 | Hexagonal, Guid/PT-BR, EF opcional, Brain opcional | **Mantidas**; orquestração segue o mesmo padrão com DbContext próprio |
| AD-58 | D.A.N.T.E. não executa `git` de escrita; nenhum bloqueio de checkout | **Complementada** pela AD-71 proposta: conjunto fechado de comandos de worktree; push e merge continuam fora |
