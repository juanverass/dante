# Decisões arquiteturais

Decisões **vigentes**, com contexto e justificativa. Uma decisão aqui não é reaberta por
preferência: mudá-la exige Issue própria e registro explícito.

Formato: cada decisão tem um identificador estável (`AD-NN`), status e, quando aplicável,
onde ela é verificada no código/testes. Decisão superada não é apagada: recebe
`Status: substituída por AD-NN` e permanece como histórico.

---

## AD-01 — Telegram via long polling

Status: vigente (MVP 1, #6)

O bot usa `getUpdates` em long polling. Não há webhook, domínio público nem porta
exposta. O offset avança **antes** do despacho, para que um comando que falhe não seja
reexecutado no próximo poll.

Por quê: o D.A.N.T.E. roda na máquina pessoal do usuário; abrir porta de entrada
aumentaria superfície de ataque sem benefício.

Código: `Telegram/TelegramPollingService.cs`; testes em `TelegramPollingServiceTests`.

## AD-02 — Execução local das CLIs como processo filho

Status: vigente (MVP 1, #3–#5)

Claude Code e Codex são executados como processos locais, via `AgentProcessExecutor`,
com `UseShellExecute=false`, argumentos por `ArgumentList` e executável resolvido apenas
entre nomes fixos em entradas absolutas do `PATH`. Cancelamento encerra a árvore de
processos.

Por quê: reutiliza a instalação e a autenticação que o usuário já tem, sem SDK próprio
para cada agente.

Código: `Dante.Infrastructure/Agentes/AgentProcessExecutor.cs`, `Dante.Infrastructure/Agentes/AgentExecutableResolver.cs`; testes em
`AgentProcessExecutorTests`.

## AD-03 — Nenhum shell arbitrário

Status: vigente (MVP 1)

O usuário do Telegram escolhe o agente e o prompt; nunca o executável nem argumentos
internos. O prompt é sempre um único argumento posicional depois de `--`.

Por quê: o D.A.N.T.E. é orquestrador de agentes, não shell remoto.

Código: `Dante.Infrastructure/Agentes/ClaudeRunner.cs`, `Dante.Infrastructure/Agentes/CodexRunner.cs`; testes em
`ClaudeRunnerTests`, `CodexRunnerTests`.

## AD-04 — Autenticação local das CLIs; API keys opcionais

Status: vigente (MVP 1, reforçada em #22)

O D.A.N.T.E. não exige `OPENAI_API_KEY` nem `ANTHROPIC_API_KEY`: usa a autenticação já
existente de cada CLI (inclusive assinatura). Quando essas variáveis existem no host,
são preservadas para o processo filho e mascaradas nas respostas do Telegram.

## AD-05 — Allowlist fail-closed de usuários do Telegram

Status: vigente (MVP 1, #7)

A identidade é `message.from.id`. Lista ausente, vazia ou com qualquer entrada inválida
bloqueia todos os comandos; usuário não autorizado não recebe resposta.

Código: `Telegram/TelegramUserAuthorizer.cs`; testes em `TelegramUserAuthorizerTests`.

## AD-06 — Jobs somente em memória

Status: vigente (MVP 1, #9)

`JobRegistry` mantém jobs ativos e os 20 encerrados mais recentes em memória. Reiniciar
o Worker perde o histórico.

Por quê: simplicidade; persistência só entra com necessidade real (candidata a MVP
futuro).

Código: `Jobs/JobRegistry.cs`; testes em `JobRegistryTests`.

## AD-07 — RepositoryRegistry como catálogo explícito

Status: vigente (MVP 2, #19–#20)

Repositórios são cadastrados com alias `@nome`, path absoluto que precisa ser a raiz de
um repositório Git e, opcionalmente, `owner/repo` do GitHub conferido contra o
`remote.origin.url`. O catálogo persiste em `~/.dante/repositories.json`.

Código: `Dante.Infrastructure/Contextos/RepositoryRegistry.cs`; testes em `RepositoryRegistryTests`.

## AD-08 — Contexto explícito, sem inferência de repositório

Status: vigente, com a regra "sem alias → General Mode" substituída pela AD-14 (MVP 2, #21)

O repositório só é usado quando o comando traz `@alias` como primeiro argumento. Sem
alias, a execução é General Mode. Alias desconhecido é erro. O D.A.N.T.E. nunca deduz o
repositório a partir do texto do prompt.

Desde a AD-14, sem `@alias` a execução usa o repositório ativo selecionado pelo usuário
com `/use`, e só é General Mode quando não há repositório ativo. Continuam vigentes: o
repositório vem sempre de uma escolha explícita (`@alias` ou `/use`), alias desconhecido
é erro e o repositório nunca é inferido do texto do prompt.

Por quê: executar um agente com permissões de escrita no projeto errado é pior do que
pedir o alias.

Código: `Telegram/TelegramPollingService.cs`; testes em `TelegramAgentRoutingTests`.

## AD-09 — General Workspace isolado

Status: vigente (MVP 2, #24)

Consultas gerais rodam em `~/.dante/workspaces/general` (ou `DANTE_GENERAL_WORKSPACE`,
que precisa ser absoluto), com ambiente reduzido e perfil restrito das CLIs. Um
workspace geral que coincida com um repositório cadastrado é rejeitado.

Código: `Dante.Infrastructure/Contextos/GeneralWorkspace.cs`, `Dante.Infrastructure/Agentes/AgentProcessExecutor.cs`; testes em
`GeneralWorkspaceTests`.

## AD-10 — Ambiente por repositório; segredos por host binding

Status: vigente (MVP 2, #22)

Cada repositório pode ter variáveis literais públicas (`/repo env set`) ou referências a
variáveis do host (`/repo env bind`). O valor de um binding é lido só na execução e nunca
é gravado no catálogo. Binding ausente no host impede o início do agente. Jobs com
bindings omitem a saída do agente no Telegram. Variáveis com nome de aparência sensível
(`TOKEN`, `SECRET`, `API_KEY`…) só podem ser configuradas por binding.

Por quê: segredos nunca trafegam pelo Telegram nem ficam em arquivo do D.A.N.T.E.

Código: `Dante.Infrastructure/Contextos/RepositoryRegistry.cs`; testes em `RepositoryRegistryTests` e
`TelegramRepositoryCommandTests`.

## AD-11 — Contexto imutável por job

Status: vigente (MVP 2, #23)

Cada job registra o contexto resolvido no início (`General` ou alias + path). Alterar o
catálogo depois não muda jobs existentes; `/status` mostra o contexto de cada job.

Código: `Dante.Application/Contextos/JobExecutionContext.cs` (no Worker até a #166); testes em `JobRegistryTests` e
`TelegramJobCommandTests`.

## AD-12 — Desenvolvimento por agentes guiado por contrato persistido

Status: vigente (Agent Harness v1, #40)

Claude e Codex desenvolvem o D.A.N.T.E. sob o mesmo
[contrato](../development/agent-contract.md). A fonte de verdade é repositório + Git +
Issues/PRs; memória de conversa não é fonte de verdade.

Papéis padrão: **Claude implementa, Codex revisa**, com override humano explícito por
Issue/PR. Ver [protocolo de review](../development/review.md#papéis-padrão).

---

## MVP 3

As regras de precedência de agente e contexto propostas na Epic #32 estão na AD-27.

## AD-13 — Configurações do assistente persistidas fora do checkout, fail-closed

Status: vigente (MVP 3, #33)

Preferências conversacionais vivem em `~/.dante/settings.json`, gravado com escrita
atômica (arquivo temporário + rename) sob lock. `DefaultAgent` aceita apenas `Claude`
ou `Codex` (o repositório ativo por usuário, também nesse arquivo, é a AD-14). Sem arquivo, o padrão documentado é Claude.
Arquivo corrompido, vazio ou com valor desconhecido é erro na carga: o D.A.N.T.E. não
escolhe agente por inferência nem sobrescreve o arquivo inválido. O arquivo nunca guarda
tokens, credenciais ou segredos. O store é carregado na inicialização do host (injetado
no `Worker`), então um arquivo inválido impede o Worker de iniciar.

Por quê: o agente padrão decide qual CLI roda com o prompt do usuário; um valor ambíguo
deve parar o Worker, não ser adivinhado.

Código: `Dante.Infrastructure/Contextos/AssistantSettingsStore.cs`; testes em `AssistantSettingsStoreTests` e
`WorkerLifecycleTests`.

## AD-14 — Repositório ativo por usuário, persistido; contexto stale exige nova seleção

Status: vigente (MVP 3, #36)

`/use @alias` grava o repositório ativo por Telegram User ID em `ActiveRepositories` no
`~/.dante/settings.json` (mesma escrita atômica e carga fail-closed da AD-13: user ID não
numérico ou alias malformado impede o Worker de iniciar). Só aliases cadastrados no
`RepositoryRegistry` são aceitos na seleção. Sem `@alias` explícito, toda execução de
agente do usuário — mensagem comum, `/claude` ou `/codex` — usa o repositório ativo; um
`@alias` explícito vale só para aquela execução e não altera o ativo. Isso substitui a
regra "sem alias → General Mode" da AD-08; a proibição de inferir o repositório pelo
texto do prompt continua valendo — o ativo é sempre uma escolha explícita do usuário.

`/repo remove` limpa o repositório ativo de todos os usuários que apontavam para o alias.
Se ainda assim o ativo não estiver mais no catálogo (falha ao gravar a limpeza, edição
manual do catálogo), a execução é **recusada** com instrução para `/use @alias` ou
`/use general`: o D.A.N.T.E. não cai silenciosamente para General Mode.

Por quê: o usuário que selecionou um repositório espera que o agente trabalhe nele;
executar em outro contexto sem aviso seria uma escolha implícita. A precedência completa
de agente e contexto está centralizada no resolvedor da AD-27.

Código: `Dante.Infrastructure/Contextos/AssistantSettingsStore.cs`, `Telegram/TelegramPollingService.cs`;
testes em `AssistantSettingsStoreTests` e `TelegramActiveRepositoryTests`.

## AD-27 — Resolvedor único de agente e contexto com precedência determinística

Status: vigente (MVP 3, #37)

Toda execução de agente — mensagem comum que abre sessão, `/session start`, `/claude`,
`/codex` e o fallback one-shot sem `SessionRegistry` — decide agente e contexto no
`AgentContextResolver`, e só nele:

```text
Agente:       /claude | /codex (ou claude|codex no /session start) → agente padrão
Repositório:  @alias explícito como primeiro token → repositório ativo (/use) → General Mode
```

O resolvedor devolve agente, contexto (`JobExecutionContext`), ambiente do repositório,
prompt limpo (sem o comando e sem o `@alias`) e a origem de cada decisão
(`AgentSource`: `Explicit`/`Default`; `ContextSource`: `Explicit`/`Active`/`General`).
Overrides valem só para aquela resolução: nada é gravado em settings. Um `@alias` fora do
primeiro token é texto do prompt; o resolvedor nunca infere agente ou repositório pelo
conteúdo.

Qualquer recusa (`ContextResolutionFailure`) impede o início de job ou sessão e nunca cai
para outro contexto: prompt vazio após remover comando/alias (o chamador responde com o
uso do seu comando), alias malformado, alias explícito desconhecido, repositório ativo
fora do catálogo (AD-14), binding de ambiente ausente (AD-10) e workspace geral sobreposto
a um repositório (AD-09).

Mensagem comum com sessão ativa não passa pelo resolvedor: vira turno da sessão, cujo
agente e contexto foram fixados ao iniciá-la (AD-20, AD-23).

Por quê: as regras estavam duplicadas entre o caminho one-shot e o de sessões, com
ordens de validação e mensagens divergentes. Um ponto único torna a precedência
testável por tabela e impede que um caminho novo esqueça uma recusa.

Código: `Dante.Application/Contextos/AgentContextResolver.cs` (no Worker até a #166, AD-38),
`Telegram/TelegramPollingService.cs`; testes em
`AgentContextResolverTests` (tabela de precedência e recusas), `TelegramAgentRoutingTests`,
`TelegramActiveRepositoryTests` e `TelegramPlainMessageTests`.

---

## Interactive Agent Sessions (Epic #60)

## AD-15 — Sessões interativas por protocolo estruturado, um processo por sessão

Status: vigente (Epic #60, #61)

O modo interativo dirige as CLIs pelas interfaces estruturadas, nunca por TUI, PTY ou
raspagem de ANSI:

- **Claude Code**: `--print --input-format stream-json --output-format stream-json
  --verbose --permission-prompt-tool stdio`. Approvals e `AskUserQuestion` chegam ao
  D.A.N.T.E. como `control_request` `can_use_tool`; interrupt é `control_request`
  `interrupt`. Sem `--permission-prompt-tool stdio` os pedidos são negados
  automaticamente e não chegam ao host.
- **Codex**: `app-server --listen stdio://` (JSON-RPC em JSONL), com
  `thread/start`, `turn/start`, `turn/steer`, `turn/interrupt` e approvals/input como
  requests do servidor. `codex exec` continua sendo o caminho one-shot.

Cada sessão tem exatamente um processo de agente vivo, que recebe todos os turnos pelo
stdin; fechar o stdin encerra a sessão. As diferenças entre os protocolos ficam atrás de
`IAgentSessionDriver` e são declaradas em `AgentDriverCapabilities`. Executável fixo,
`ArgumentList` e prompt como dado (AD-02, AD-03) continuam valendo: o texto do usuário
vai no corpo JSON, nunca na linha de comando.

Por quê: os spikes da #61 provaram início, multi-turno, eventos, approval, input,
interrupt e encerramento nas duas CLIs instaladas por essas interfaces
(`docs/spikes/interactive-protocols/`). TUI seria frágil e dependente de versão.

Código: `Sessions/IAgentSessionDriver.cs`; evidência em
`docs/spikes/interactive-protocols/README.md`.

## AD-16 — Contrato neutro de sessão; fila do D.A.N.T.E., steer explícito

Status: vigente (Epic #60, #61)

`AgentSession` é a máquina de estados neutra (`Starting → Idle ⇄ Running ⇄
WaitingForUser`, `→ Closing → Closed`, `→ Failed`); `AgentEvent` é o fluxo de eventos
(turno iniciado, delta/mensagem, ferramenta iniciada/concluída, mudança de arquivo,
aviso/erro, approval, input, turno concluído). Drivers emitem eventos sem ids do
D.A.N.T.E.; a sessão os carimba com sessão, turno e request.

Ids: `S000001` (sessão), `T000001` (turno), `R000001` (approval/input), globais no
Worker como os de job. Ids upstream (`session_id`, `threadId`, `turnId`, ids JSON-RPC)
ficam no driver; só o id upstream de um request volta a ele na resposta.

Correlação:

- **sessão ↔ processo**: 1:1 pela vida inteira da sessão. O driver inicia o processo e
  devolve `AgentSessionStarted` (id upstream + PID), que a sessão guarda em
  `UpstreamSessionId` e `ProcessId`. Processo morto é sessão `Failed`, nunca um processo
  novo na mesma sessão;
- **sessão/turno ↔ job**: sessão interativa **não é job**. Ela não entra no
  `JobRegistry`, e um turno interativo não tem job id: é identificado por sessão + turno.
  Jobs continuam sendo só as execuções one-shot (`codex exec`, `claude --print`).
  `JobExecutionContext` é reaproveitado apenas para descrever modo e diretório. Onde a
  sessão aparece para o usuário (`/status`, registry) é da #65 (AD-20);
- **request ↔ turno**: todo request nasce num turno e morre com ele; eventos carregam
  `SessionId` e `TurnId`, e approval/input carregam também o `RequestId`.

Semântica:

- **queue** (padrão): mensagem durante um turno fica na fila do D.A.N.T.E. e vira um
  turno novo quando o ativo termina. A fila é FIFO e nada a ultrapassa: com a sessão
  ociosa mas fila não vazia (logo após iniciar ou após um turno terminar), a mensagem
  nova também é enfileirada, e `TryStartQueued` executa a mais antiga. A fila é do D.A.N.T.E. porque o Codex absorve um
  `turn/start` feito durante um turno ativo em vez de enfileirá-lo;
- **steer** (só explícito): com steer nativo (Codex), a mensagem vai ao turno ativo e é
  aplicada no próximo boundary do modelo; sem steer nativo (Claude), o turno é
  interrompido e a mensagem roda em seguida, antes da fila. Com a sessão ociosa e fila não
  vazia, steer só coloca a mensagem à frente da fila. Steer é recusado com
  approval/input pendente;
- **interrupt / stop turn**: interrompe o turno ativo, expira os requests pendentes e
  descarta a fila; a sessão volta a `Idle`;
- **close session**: recusa novas mensagens, expira requests, descarta a fila,
  interrompe o turno e fecha o processo; `Closed` é terminal;
- **falha do processo**: `Failed` é terminal; nada mais é enviado ao driver.

Um request só é respondido pelo dono da sessão, na sessão e no turno em que nasceu, com
o tipo de resposta certo (approval × input), uma única vez; request expirado, de outra
sessão ou que chegou durante um interrupt é recusado. A integração com `JobRegistry`,
Telegram e perfis de permissão fica para #65–#67.

Por quê: um contrato único impede que o Telegram dependa de detalhes de cada CLI, e a
fila conservadora evita desviar trabalho em andamento sem pedido explícito.

Código: `Sessions/AgentSession.cs`, `Sessions/AgentEvent.cs`; testes em
`AgentSessionTests`.

## AD-17 — Processo interativo: saída incremental limitada, stdin serializado, parada sem órfãos

Status: vigente (Epic #60, #62)

Sessões interativas usam `InteractiveAgentProcessLauncher`, separado do
`AgentProcessExecutor` one-shot, que continua inalterado para jobs. Os dois montam o
processo pelo mesmo `AgentProcessStartInfo`: executável fixo, `ArgumentList`, sem shell e
a mesma filtragem de ambiente de General Mode e de repositório (AD-02, AD-03, AD-09,
AD-10).

- **saída**: stdout e stderr são lidos linha a linha e publicados num canal limitado
  (256 linhas) assim que chegam; leitor lento pausa as bombas e, pelo pipe cheio, o
  agente, em vez de crescer memória. Toda linha passa pelo hook de redaction opcional
  antes de sair do wrapper; se o hook lançar, o canal termina com essa exceção e a árvore
  de processos é morta, em vez de o consumidor esperar para sempre;
- **entrada**: `WriteLineAsync` escreve uma linha JSONL inteira por vez, serializada; linha
  com quebra é recusada. O token cancela só a espera pela vez de escrever: linha iniciada
  é escrita inteira, para não corromper o framing. Stdin fechado, parada em curso ou
  processo encerrado recusam input com `AgentProcessInputClosedException`;
- **ciclo de vida do processo**: `Running → InputClosed → Exited`, com `Stopping` durante
  a parada. O ciclo da sessão (idle, turno, espera por usuário) continua sendo o
  `AgentSessionState` da AD-16;
- **turno × sessão**: interromper um turno é mensagem de protocolo escrita pelo driver no
  stdin; encerrar a sessão é `StopAsync` (fecha o stdin, espera o período de graça e mata a
  árvore de processos). `DisposeAsync` mata a árvore imediatamente. Nenhum dos caminhos
  deixa processo filho órfão;
- **descendentes**: depois que o agente sai, os filhos dele deixam de ser alcançáveis por
  `Process.Kill(entireProcessTree)`. Por isso o wrapper registra os descendentes enquanto
  o agente vive (a cada segundo e antes de fechar o stdin, via `/proc` no Linux e Toolhelp
  no Windows) e mata os sobreviventes quando ele sai, inclusive em saída graciosa. PID e
  horário de início identificam cada processo, para nunca matar um PID reutilizado.
  Limites: descendente criado e o agente encerrado dentro do mesmo intervalo pode escapar;
  em outros sistemas operacionais só vale a morte da árvore viva.

Por quê: os protocolos estruturados (AD-15) são JSONL sobre um único processo vivo; uma
linha intercalada ou um processo esquecido quebraria a sessão ou vazaria recursos no
host.

Código: `Dante.Infrastructure/Agentes/InteractiveAgentProcess.cs`, `Dante.Infrastructure/Agentes/InteractiveAgentProcessLauncher.cs`,
`Dante.Infrastructure/Agentes/AgentProcessStartInfo.cs`, `Dante.Infrastructure/Agentes/ProcessTree.cs`; testes em
`InteractiveAgentProcessTests`.

## AD-18 — Driver Claude: stream-json com `--session-id` fixo e perfis mapeados para `--permission-mode`

Status: vigente (Epic #60, #63)

`ClaudeSessionDriver` implementa `IAgentSessionDriver` sobre um único `claude --print`
por sessão, iniciado pelo `InteractiveAgentProcessLauncher` (AD-17):

```text
claude --print --input-format stream-json --output-format stream-json --verbose
       --include-partial-messages --session-id <uuid> --permission-mode <modo>
       --permission-prompt-tool stdio
       [General Mode: --restricted --strict-mcp-config --tools Read,Write,Edit,AskUserQuestion]
```

- **início**: o driver gera o `session_id` e o fixa com `--session-id`, porque o Claude só
  o anuncia no `system/init` do primeiro turno; `StartAsync` só retorna depois do
  `control_request` `initialize` confirmado, e falha com `AgentProtocolException` se o
  Claude o recusar ou morrer antes;
- **turnos**: cada turno é uma mensagem `user` no stdin; o driver emite `TurnStartedEvent`
  antes de escrevê-la e `TurnCompletedEvent` no `result` (`Interrupted` quando houve
  interrupt, `Failed` com a mensagem de erro nos demais erros). Deltas vêm de
  `--include-partial-messages` (`text_delta`, `ItemId` = id da mensagem);
- **ferramentas**: `tool_use`/`tool_result` viram `ToolStarted`/`ToolCompleted`; `Bash` é
  `Command`, `Write`/`Edit`/`MultiEdit`/`NotebookEdit` são `FileChange` e emitem
  `FileChangeEvent` quando concluem com sucesso;
- **approval e input**: `can_use_tool` vira `ApprovalRequestedEvent`, exceto
  `AskUserQuestion`, que vira `UserInputRequestedEvent` com perguntas `q1`, `q2`…; o
  `request_id` do Claude é o id upstream. "Aprovar na sessão" devolve as
  `permission_suggestions` de destino `session` em `updatedPermissions` (AD-22; validado
  contra o Claude Code 2.1.284: com `setMode acceptEdits`/`session`, o segundo `Write` do
  turno não pediu aprovação);
  respostas de `AskUserQuestion` vão em `updatedInput.answers`, chaveadas pelo texto da
  pergunta. Outros `control_request` recebem erro para o Claude não esperar para sempre;
- **steer**: não há (AD-15); `SteerAsync` lança `NotSupportedException` e a sessão usa
  interrupt + nova mensagem;
- **falhas**: linha fora do protocolo ou processo que sai sem `CloseAsync` terminam
  `ReadEventsAsync` com `AgentProtocolException` e matam o processo; `CloseAsync` fecha o
  stdin (`StopAsync`, AD-17) e termina o fluxo normalmente.

Perfis (`AgentPermissionProfile`, padrão `Manual`) mapeiam para `--permission-mode`:
`Manual → manual`, `Auto → auto`, `Plan → plan`. Nenhum perfil de acesso irrestrito existe
no driver; a escolha de perfil pelo usuário é descrita na AD-22. O one-shot
(`ClaudeRunner`, `--permission-mode auto`) continua inalterado.

Por quê: `--session-id` torna o id upstream conhecido no início, como o contrato da AD-16
exige; os formatos de resposta foram confirmados contra a CLI instalada em vez de
inferidos.

Código: `Sessions/ClaudeSessionDriver.cs`, `Dante.Domain/Agentes/AgentPermissionProfile.cs`,
`Sessions/AgentProtocolException.cs`; testes em `ClaudeSessionDriverTests`, contra o
Claude simulado de `tests/Dante.ProcessProbe/FakeClaude.cs`.

## AD-19 — Driver Codex: `app-server` com thread efêmera e perfis mapeados para approval/sandbox

Status: vigente (Epic #60, #64)

`CodexSessionDriver` implementa `IAgentSessionDriver` sobre um único
`codex app-server --listen stdio://` por sessão (JSON-RPC em JSONL), iniciado pelo
`InteractiveAgentProcessLauncher` (AD-17):

- **início**: `initialize` (com `capabilities.experimentalApi`) → `initialized` →
  `thread/start` (`cwd`, `approvalPolicy`, `sandbox`, `ephemeral: true`). O `thread.id` é o
  id upstream da sessão e o `model` da resposta é guardado para o modo `plan`. Falha em
  qualquer passo encerra o processo e falha o `StartAsync`;
- **turnos**: `turn/start` na mesma thread, só com a sessão ociosa (o Codex absorveria o
  turno no ativo, AD-16); o `turn.id` da resposta é o turno ativo usado por steer e
  interrupt — exceto quando o `turn/completed` desse turno chegou antes da resposta ser
  processada (turno rápido), caso em que não há turno ativo e interrupt continua no-op (#68). `turn/started` → `TurnStartedEvent`; `turn/completed` → `TurnCompletedEvent`
  (`completed`, `interrupted`, demais = `Failed` com `turn.error.message`);
- **eventos**: `item/agentMessage/delta` → delta; `item/completed` `agentMessage` →
  mensagem; `commandExecution` → `Command` (sucesso = `completed` e exit code 0);
  `fileChange` → `FileChange` + `FileChangeEvent` com paths e diffs quando aplicado;
  `mcpToolCall`/`dynamicToolCall`/`webSearch` → `Tool`; `warning` e `error` com
  `willRetry` → `WarningEvent`, `error` definitivo → `ErrorEvent`;
- **approval e input**: `item/commandExecution/requestApproval` e
  `item/fileChange/requestApproval` → `ApprovalRequestedEvent`, respondidos com
  `accept`/`acceptForSession`/`decline` (o motivo da negação não tem campo no protocolo e
  fica no D.A.N.T.E.); `item/tool/requestUserInput` → `UserInputRequestedEvent` com os ids
  de pergunta do próprio Codex, respondido em `answers.<id>.answers`. O id JSON-RPC do
  request (número ou texto, na forma textual) é o id upstream. Outros requests do
  servidor (elicitation MCP, permissões, ferramentas dinâmicas, auth) recebem erro
  JSON-RPC `-32601` para o Codex não esperar para sempre; `serverRequest/resolved` retira
  o request da lista de pendentes;
- **steer**: `turn/steer` com `expectedTurnId` do turno ativo, aplicado no próximo
  boundary do modelo;
- **interrupt**: approvals pendentes recebem `cancel` e perguntas pendentes recebem
  respostas vazias antes de `turn/interrupt`, para nada ficar esperando upstream; o
  processo e a thread continuam. Sem turno ativo, interrupt não faz nada;
- **falhas**: request recusado pelo Codex (JSON-RPC `error`) falha só aquela chamada com
  `InvalidOperationException`; linha fora do protocolo ou processo que sai sem
  `CloseAsync` terminam `ReadEventsAsync` com `AgentProtocolException` e matam o
  processo; `CloseAsync` fecha o stdin (`StopAsync`, AD-17) e termina o fluxo normalmente.

Perfis (`AgentPermissionProfile`, padrão `Manual`) mapeiam para `thread/start`:

| Perfil | `approvalPolicy` | `sandbox` | `turn/start` |
| --- | --- | --- | --- |
| `Manual` | `on-request` | `workspace-write` | — |
| `Auto` | `on-request` | `workspace-write` | — |
| `Plan` | `on-request` | `read-only` | `collaborationMode` `plan` com o modelo da thread |

Evolução do perfil `Auto`: `thread/start` recebe `approvalsReviewer=auto_review`, validado na
CLI 0.159.3. A política anterior `never` impedia solicitar exceções ao sandbox. Agora o
revisor da CLI avalia pedidos de acesso automaticamente, podendo aprovar ou negar; `Manual`
e `Plan` recebem `approvalsReviewer=user`. O driver exige confirmação da política e do
revisor no retorno de `thread/start` em `Auto`, recusando suporte ausente ou divergente.

Nenhum perfil chega a `danger-full-access`; a escolha pelo usuário é descrita na AD-22.
Input humano do Codex continua **experimental** e, na 0.157.1, só aparece no
perfil `Plan` (AD-15). `app-server` não tem `--ignore-user-config`: em General Mode o
isolamento vem do ambiente filtrado, do diretório neutro e do `sandbox` da thread. O
one-shot (`CodexRunner`, `codex exec`) continua inalterado.

Por quê: a thread efêmera acompanha a vida da sessão em memória (Epic #60), e cancelar os
requests pendentes no interrupt evita um turno preso esperando resposta que nunca virá.

Código: `Sessions/CodexSessionDriver.cs`, `Dante.Domain/Agentes/AgentPermissionProfile.cs`,
`Sessions/AgentProtocolException.cs`; testes em `CodexSessionDriverTests`, contra o
app-server simulado de `tests/Dante.ProcessProbe/FakeCodex.cs`.

## AD-20 — SessionRegistry em memória: dono, contexto fixo e sessão ativa por seleção explícita

Status: vigente (Epic #60, #65)

`SessionRegistry` (singleton) é o catálogo das sessões interativas durante a vida do Worker. Ele cria um driver por
sessão (`IAgentSessionDriverFactory`), é o único que chama o driver e drena o fluxo de eventos de cada sessão para
a `AgentSession` (AD-16), repassando cada evento já carimbado a um `IAgentSessionEventSink` opcional — a entrega ao
Telegram é da #66.

- **dono**: toda sessão pertence ao Telegram User ID que a iniciou. Operações (mensagem, interrupt, close,
  seleção) só alcançam sessões do próprio usuário; sessão de outro usuário é respondida exatamente como sessão
  inexistente. Resposta a approval/input é roteada pelo `R…` à sessão do request e recusada para quem não é dono;
- **contexto fixo**: agente, contexto (`JobExecutionContext` já resolvido: General ou alias + path), ambiente e
  perfil de permissão são fixados no início e não mudam durante a sessão. `/use` e `/agent set` valem para
  execuções e sessões novas, nunca para uma sessão existente. A resolução de agente e contexto continua fora do
  registry (no `AgentContextResolver`, AD-27): o registry recebe o contexto pronto;
- **sessão ativa**: no máximo uma por usuário. Iniciar uma sessão com sucesso a torna ativa; trocar é
  `Select(usuário, S…)` e limpar é `Select(usuário, null)`. `close` pelo dono limpa a seleção. Sessão que falha
  continua selecionada, para que a próxima mensagem seja **recusada** com o erro em vez de ir para outro lugar
  (mesma postura da AD-14);
- **turnos**: mensagem para sessão ociosa vira turno novo na mesma sessão e no mesmo processo; durante um turno,
  fila/steer seguem a AD-16. Quando um turno termina, o registry inicia o próximo da fila;
- **falhas**: fluxo de eventos quebrado, processo que sai sem `close` ou mensagem que não chega ao agente
  (`StartTurnAsync` falhou) tornam a sessão `Failed`, publicam `ErrorEvent` e matam o processo — um turno aberto
  que nunca chegou ao agente ficaria `Running` para sempre. Steer recusado pelo agente (o turno pode ter acabado
  entre o envio e o `turn/steer`) só recusa aquela mensagem;
- **relação com jobs**: sessões não entram no `JobRegistry` e turnos não têm job id (AD-16). Ids distintos
  (`J…` job, `S…` sessão, `T…` turno, `R…` request) evitam ambiguidade; `/status` lista os jobs e, em seção
  própria, só as sessões do usuário que pediu;
- **retenção e reinício**: sessões vivas e as 20 encerradas mais recentes ficam em memória. Ao parar o host, o
  registry (descartado pelo container) marca toda sessão viva como `Failed` ("sessões não sobrevivem ao reinício
  do Worker") e mata seus processos; depois do reinício, qualquer `S…` antigo é "não encontrada", com o mesmo
  aviso.

Por quê: o contexto de uma sessão é uma escolha feita quando ela começa; mudá-lo silenciosamente no meio de uma
conversa faria o agente trabalhar no lugar errado. Tratar sessão alheia como inexistente evita revelar sessões de
outros usuários.

Código: `Sessions/SessionRegistry.cs`, `Sessions/IAgentSessionDriverFactory.cs`,
`Sessions/IAgentSessionEventSink.cs`, `Sessions/AgentSessionSnapshot.cs`; testes em `SessionRegistryTests` e
`TelegramJobCommandTests`.

## AD-21 — Entrega Telegram independente do resultado do agente

Status: vigente (Epic #60, #66), com formato e cadência das partes de sessão atualizados pela AD-23

`TelegramDeliveryService` recebe eventos já carimbados pelo `SessionRegistry`, identifica cada
parte com sessão/turno, agrupa deltas em intervalos de 750 ms e entrega sequencialmente por
turno. Jobs também registram a resposta final ali depois de concluir a execução. Registros
recentes em memória distinguem `Pending`, `Delivered` e `Failed`, preservam a parte que falhou
e as seguintes e permitem `/resend` sem chamar o agente novamente. `/status` mostra o último
resultado do turno e o estado da entrega separadamente. Saídas de sessões com segredos vinculados são omitidas;
segredos de autenticação do host são redigidos antes de cada envio, inclusive quando divididos
entre deltas. A saída por registro é limitada e o truncamento é indicado ao usuário.

Falhas transitórias (`HttpRequestException`, timeout, 429 e 5xx) recebem até quatro tentativas
com backoff exponencial; 429 respeita `retry_after` quando informado. Erros permanentes encerram
a tentativa, deixam a entrega como `Failed` e registram apenas o tipo do erro. Uma falha de
entrega nunca muda o estado concluído do job ou do turno. Registros de entrega e sessões não
sobrevivem ao reinício do Worker.

Desde a AD-23, a saída da sessão ativa não leva prefixo de sessão/turno (só a de outras sessões,
como `[S…]`, decidido no envio de cada parte), o streaming sai em linhas inteiras com pausa mínima
entre partes do mesmo turno e partes sem texto visível não são enviadas. A retenção de segredos
parciais passou a ser exata: só fica retido o final do texto que ainda pode completar um segredo
conhecido (sufixo que é prefixo dele), em vez de sempre os últimos `tamanho do maior segredo - 1`
caracteres — com uma API key no ambiente, linhas curtas de progresso ficavam presas até o fim do
turno. Separação entre entrega e execução, retry, `/resend`,
redaction e omissão por segredos vinculados continuam como descrito acima.

Por quê: a conclusão de ações do agente não prova que o Telegram recebeu sua resposta. Separar
os estados permite recuperação sem repetir efeitos no repositório ou no GitHub.

Código: `Telegram/TelegramDeliveryService.cs`, `Telegram/TelegramPollingService.cs`,
`Telegram/TelegramBotApi.cs`; testes em `TelegramDeliveryServiceTests` e `TelegramBotApiTests`.

## AD-22 — Approvals e input por IDs correlacionados, perfis escolhidos para sessões novas

Status: vigente (Epic #60, #67), com a escolha do perfil padrão (em memória até o reinício)
substituída pela AD-24 e a apresentação de input humano evoluída pela AD-30 (#105)

O Telegram apresenta pedidos de aprovação e input com IDs `S…`, `T…` e `R…`. Os comandos
`/approve`, `/approve-session`, `/deny` e `/input` exigem os três IDs; o registry confirma
o dono e o turno antes de encaminhar a resposta ao driver. Um request é consumido uma vez.
Respostas parciais de input, duplicadas, tardias ou destinadas a outro turno são recusadas.
`/approve-session` só é oferecido quando o driver indica suporte no próprio request:
`acceptForSession` no Codex e ao menos uma `permission_suggestion` com
`destination: session` no Claude. Só essas sugestões voltam em `updatedPermissions`; as de
destino persistente (`userSettings`, `projectSettings`, `localSettings`) são descartadas,
para que aprovar na sessão nunca grave permissão que sobreviva a ela.

Cada request expira após cinco minutos em memória. Ao expirar, o registry remove o estado
pendente e envia negação de approval ou input vazio ao driver para liberar o turno. O
Telegram recebe um aviso de expiração. Resposta humana, expiração, interrupção (inclusive
steer por interrupção) e fechamento são serializados por sessão: cada um muda o estado e
alcança o driver como um passo só, de modo que um `stop`/`close` nunca limpa o request
upstream entre a expiração local e a negação enviada ao agente. O prazo é conferido contra
o relógio da sessão antes de expirar, para um timer adiantado não deixar o request pendente
para sempre. `/status` mostra os IDs ainda pendentes. Pedidos continuam chegando mesmo com
segredos vinculados ao ambiente, mas os detalhes são omitidos nessa situação conforme a
política de saída da AD-21.

`/permissions` consulta ou escolhe `manual`, `auto` e `plan` para sessões novas do usuário;
a escolha dura até o reinício do Worker. `/session start` aceita um perfil explícito que
vale só para aquela sessão. O padrão é `manual`. Os mapeamentos Claude e Codex seguem as
AD-18 e AD-19, sem alteração dos comandos one-shot. `full` não é exposto: as CLIs têm
capacidades diferentes para acesso ampliado, e não há nesta Issue um mapeamento comum
validado que justifique oferecê-lo.

Por quê: a decisão deve alcançar exatamente a ação bloqueada no turno do dono; o prazo
evita que um agente permaneça esperando indefinidamente. Perfis ficam fixos durante a
sessão (AD-20), de modo que uma mudança de preferência não altera permissões em execução.

Código: `Sessions/AgentSession.cs`, `Sessions/SessionRegistry.cs`,
`Telegram/TelegramPollingService.cs`, `Telegram/TelegramDeliveryService.cs`; testes em
`SessionRegistryTests` e `TelegramDeliveryServiceTests`.

## AD-23 — Conversa session-first no Telegram; one-shot só por comando explícito

Status: vigente (Epic #60, #68), com o modo padrão persistido acrescentado pela AD-24.
A abertura implícita permanece sem aviso de ciclo de vida (correção do review da PR #82).

Mensagem comum é conversa com uma sessão interativa, não um job:

- **com sessão ativa**: vira turno dessa sessão; durante um turno entra na fila (AD-16) e o
  usuário recebe só um curto `Recebido`;
- **sem sessão ativa**: o D.A.N.T.E. abre uma sessão com o agente padrão, no contexto atual e
  com o perfil de `/permissions`, e envia a própria mensagem como primeiro turno. O contexto é
  o `@alias` no início da mensagem, se houver (vale para aquela sessão e não altera o
  repositório ativo), senão o repositório ativo, senão General Mode — a mesma resolução e as
  mesmas recusas do `/session start` (alias desconhecido, repositório ativo fora do catálogo,
  workspace geral sobreposto). A sessão nova passa a ser a ativa e as mensagens seguintes são
  turnos dela até `/session close`, `/session start` ou `/session select`;
- **sessão ativa encerrada** (`Failed`): continua selecionada (AD-20) e a mensagem é recusada
  com a saída `/session start`, em vez de abrir silenciosamente outra conversa sem o contexto;
- **falha ao iniciar**: resposta curta (`Não foi possível iniciar a conversa…`) com o detalhe
  em `/status`; a sessão falha não fica selecionada e a próxima mensagem tenta de novo.

`/claude` e `/codex` continuam sendo execução one-shot (jobs, `Job ID`, `/cancel`), sem tocar
na conversa. Sem `SessionRegistry` na composição, mensagens comuns caem para one-shot.
`/agent set` e `/use` não alteram uma sessão existente (AD-20); quando a sessão ativa difere da
nova preferência, a resposta diz que ela continua e indica `/session start`.

Apresentação no Telegram:

- a sessão ativa fala como conversa: texto do agente, linhas curtas de progresso (`→ comando`,
  arquivos alterados, `✗ … falhou`), `Resposta interrompida.`, `A resposta falhou: …` e
  `(sem resposta do agente)` quando o turno termina sem texto visível. Não há `Job ID` nem
  aviso de abertura de conversa nem `Turno iniciado/concluído`. Saída de outra sessão leva o
  prefixo `[S…]`. A identificação é decidida no envio de cada parte, contra a sessão ativa do usuário no momento: o
  `TelegramDeliveryService` acompanha a seleção do `SessionRegistry` (atualizada pelo
  `TelegramPollingService` a cada abertura implícita, `/session start`, `select` e `close`), então
  a saída de uma sessão que deixa de ser ativa no meio de um turno passa a chegar como `[S…]` e a
  de uma sessão reselecionada volta a chegar sem prefixo;
- IDs aparecem só onde o usuário precisa agir (pedido de aprovação/input, cujos comandos
  exigem `S… T… R…`), em `/status`, `/session` e nos demais comandos explícitos. `/status` mostra
  também o erro de sessões encerradas, exceto quando a sessão tem segredos vinculados;
- caminhos dentro do diretório da sessão aparecem relativos a ele; nas linhas de progresso o
  wrapper `/bin/bash -lc '…'` do Codex é omitido. O pedido de aprovação mostra o comando completo, com o wrapper;
- enquanto o turno da sessão ativa roda, `sendChatAction(typing)` é renovado a cada 4 s. Como o
  indicador é do chat inteiro, ele nunca roda para outra sessão: para quando a sessão deixa de ser
  ativa, pausa enquanto um pedido espera o usuário e para no fim do turno. Falha do indicador é
  ignorada.

Cadência e limites do Telegram: o primeiro lote de um turno sai 750 ms após o primeiro evento;
quaisquer partes consecutivas do mesmo turno — inclusive várias geradas por um único flush, como
uma resposta maior que 4000 caracteres — saem com pelo menos 1,5 s entre si (orientação do
Telegram de cerca de uma mensagem por segundo por chat); o resultado final de um job one-shot
mantém o envio contínuo; o streaming sai em linhas inteiras — uma parte nunca
começa ou termina no meio de uma linha, salvo linha maior que uma mensagem — e respeita a
retenção de segredos parciais da AD-21; partes de até 4000 caracteres; parte sem texto visível
não é enviada, porque o Telegram a recusaria e a entrega do turno ficaria `Failed`. 429 respeita
`retry_after` (AD-21).

Por quê: no dogfooding real (#68), mensagens comuns ainda iniciavam um job sem contexto a cada
mensagem e expunham o ciclo interno (`iniciado`, `Job ID`, `concluído`), e o streaming dividia
palavras em dezenas de mensagens. A conversa passa a ser o caminho padrão sem esconder o estado
operacional, que segue disponível em `/status`. Validado contra Claude Code 2.1.286 e codex-cli
0.159.3 (multi-turno com contexto, aprovação negada e permitida, input, interrupt, steer e
close).

Código: `Telegram/TelegramPollingService.cs`, `Telegram/TelegramDeliveryService.cs`,
`Telegram/TelegramBotApi.cs`; testes em `TelegramPlainMessageTests`, `TelegramDeliveryServiceTests`
e `InteractiveSessionEndToEndTests` (Telegram → `SessionRegistry` → drivers reais → CLIs
simuladas do `Dante.ProcessProbe`).

## AD-24 — Modos operacionais: perfis com nomes amigáveis, padrão persistido por usuário e capacidade por agente

Status: vigente (#76), evoluída pela #108 com troca explícita na sessão ociosa.

Os perfis de permissão da AD-22 são apresentados ao usuário como **modos** de trabalho:
`manual` (aprovação; `approval` é sinônimo na entrada), `auto` (automático) e `plan`
(planejamento). Nomes, descrições e parsing ficam num único lugar (`AgentSessionModes`),
compartilhado por `/mode`, `/permissions`, `/session start` e o arquivo de configurações. Os
mapeamentos para cada CLI continuam os das AD-18 e AD-19.

- **padrão por usuário, persistido**: `/mode <modo>` grava o modo padrão do Telegram User ID em
  `SessionModes` no `~/.dante/settings.json`, com a mesma escrita atômica e carga fail-closed da
  AD-13 (usuário não numérico, `null` ou valor fora de `manual|auto|plan` impede o Worker de
  iniciar; sinônimos não são aceitos no arquivo). Sem escolha, o modo é `manual`. Isso substitui a
  escolha em memória até o reinício da AD-22. `/permissions` continua como interface de baixo nível
  e lê e grava o mesmo padrão;
- **regra original (#76), evoluída pela #108**: o modo era imutável na sessão, fixado na
  abertura (AD-20). Mudar o padrão continua valendo só para sessões futuras. Agora
  `/mode session <modo>` solicita explicitamente a troca na sessão ativa ociosa, sem mudar
  o padrão. Um modo em `/session start` continua valendo só para aquela sessão;
- **capacidade dinâmica validada por driver (#108)**: `AgentDriverCapabilities.ModeSwitch`
  distingue ausência de suporte, aplicação ociosa confirmada (Claude) e override no próximo
  turno (Codex). Claude usa `set_permission_mode` e exige o modo retornado (`manual` normaliza
  para `default`). Codex guarda a intenção separada do perfil efetivo e envia `approvalPolicy`,
  `approvalsReviewer`, `sandboxPolicy` e `collaborationMode` no próximo `turn/start`, na mesma
  thread efêmera. Sair de plan envia colaboração `default` explicitamente. Só
  `thread/settings/updated` compatível com a política solicitada, cwd, modelo e esforço
  confirma a aplicação e atualiza o snapshot. `/status` mostra o modo efetivo e a intenção
  pendente. Não se usa `thread/resume`, que recusou threads efêmeras na CLI 0.159.3;
- **ponto seguro**: troca exige `Idle`, fila vazia e nenhum request pendente. Início de turno,
  despacho da fila, troca e respostas upstream são serializados. Turno ativo, approval e
  input pendentes recusam a operação sem descartar nem responder a requests. O usuário
  precisa aguardar ou interromper explicitamente. Solicitar o modo atual cancela uma troca
  pendente do Codex. Só o dono pode trocar;
- **falha de troca**: recusa explícita preserva perfil anterior e sessão. Falta de confirmação,
  confirmação incompatível ou cancelamento/timeout após envio ao Claude encerram a sessão
  com erro, preservando o último perfil confirmado e evitando processo vivo divergente.
  Agente sem suporte orienta abertura explícita de nova sessão. Nenhuma troca reinicia
  processo/thread silenciosamente, altera modelo/esforço/contexto ou concede full;
- **evidência**: [investigação #108](../spikes/session-mode/README.md), com Claude Code
  2.1.287 e Codex 0.159.3, controles reais e seis transições dirigidas entre modos com
  lembrança da conversa. Teste reproduzível opt-in: `LiveSessionModeEvidenceTests`;
- **visível**: `/status` mostra o modo de cada sessão, `/mode` mostra o padrão e o modo da sessão
  ativa, e `/session start` o informa na abertura explícita. A abertura implícita por mensagem
  comum não envia aviso adicional de ciclo de vida, preservando a conversa direta da AD-23;
- **capacidade por agente**: `AgentDriverCapabilities.Modes` declara os modos que cada driver
  mapeia. O `SessionRegistry` recusa um modo fora dessa lista antes de iniciar o processo e sem
  registrar sessão, com erro que lista os modos disponíveis. Hoje Claude e Codex declaram os três;
- **sem acesso irrestrito**: nenhum modo concede `full`, e o D.A.N.T.E. nunca escolhe um modo por
  inferência. One-shot (`/claude`, `/codex`) continua sem modos.

Por quê: o usuário precisa saber e escolher quanta autonomia o agente tem sem conhecer os detalhes
internos de cada CLI; um padrão que se perde no reinício mudaria silenciosamente o comportamento
das próximas conversas.

Código: `Sessions/AgentSessionModes.cs`, `Sessions/IAgentSessionDriver.cs`,
`Sessions/SessionRegistry.cs`, `Dante.Infrastructure/Contextos/AssistantSettingsStore.cs`,
`Telegram/TelegramPollingService.cs`; testes em `TelegramModeCommandTests`, `SessionRegistryTests`
e `AssistantSettingsStoreTests`.

## AD-25 — Modelo por usuário e agente, validado pela CLI e fixado ao iniciar

Status: vigente (#77)

`/model` consulta ou escolhe o modelo padrão de cada agente para o Telegram User ID,
persistido em `Models` no `~/.dante/settings.json` com escrita atômica e carga
fail-closed. Sem preferência, o D.A.N.T.E. omite o modelo e mantém o padrão da CLI.
`/model <agente> default` remove a preferência sem exigir consulta ao catálogo.

`AgentModelCatalog` consulta `initialize` (Claude stream-json) e `model/list`
(Codex app-server, paginado), sem iniciar turno, no workspace e ambiente gerais.
O catálogo é separado por agente, descarta modelos ocultos do Codex e aceita os IDs
resolvidos que o Claude informa para seus aliases. Consultas têm prazo de 30 segundos;
respostas válidas ficam em cache por dez minutos, permitindo perceber mudanças da CLI
sem reiniciar o Worker. Falha de consulta nunca é interpretada como autorização para
usar um nome arbitrário.

`/session start ... model=<modelo>` sobrescreve apenas aquela sessão;
`model=default` ignora a preferência e usa o padrão da CLI. O modelo fica no snapshot
e nas opções do driver, imutável durante a sessão. Claude recebe `--model`; Codex
recebe `model` em `thread/start`, e seu modelo reportado é reutilizado no modo plan.
`/status` mostra a escolha ou o padrão da CLI (com modelo reportado, quando disponível).
Alterar a preferência avisa que sessões existentes mantêm o modelo original.

One-shot explícito usa a preferência do agente nomeado pelo comando, fixada no job,
e passa `--model` como argumento separado. Nomes são validados no catálogo antes de
iniciar jobs ou sessões. Preferência removida pela CLI recusa novas execuções após
a renovação do catálogo e orienta escolher outra ou voltar a `default`, sem fallback
silencioso. Sessões existentes continuam com sua configuração original.

Por quê: modelos e aliases variam por CLI e versão; consultar a CLI evita manter uma
lista estática e impede que uma preferência de Claude chegue ao Codex. Recusar uma
preferência obsoleta preserva a escolha do usuário e torna a recuperação explícita.

Código: `Dante.Infrastructure/Agentes/AgentModelCatalog.cs`, `Dante.Application/Agentes/AgentModelSelection.cs`,
`Dante.Infrastructure/Contextos/AssistantSettingsStore.cs`, `Sessions/SessionRegistry.cs`, drivers,
runners e `Telegram/TelegramPollingService.cs`. Testes: `AgentModelCatalogTests`,
`TelegramModelCommandTests`, settings, runners e drivers.


## AD-26 — Esforço por agente e usuário, validado por modelo e independente de permissões

Status: vigente (#78)

`/effort` consulta preferências e níveis anunciados pela CLI para cada modelo;
`/effort claude|codex <nível>|default` grava ou remove a preferência em `Efforts`,
no settings local, com escrita atômica, rollback e carga fail-closed. Não há lista
estática nem equivalência entre CLIs. O catálogo da AD-25 fornece `EffortLevels`;
sem modelo escolhido usa a entrada marcada como default. Sem default identificável,
a seleção explícita recusa e orienta escolher um modelo. Modelos sem níveis não
aceitam esforço explícito. Sem esforço escolhido, a CLI mantém o padrão nativo.

`AgentModelSelection` carrega modelo e esforço juntos, fixados no snapshot e nas
opções do driver ao iniciar. `/session start ... effort=<nível>|default` vale apenas
para aquela sessão. Novas conversas e jobs usam a preferência do agente nomeado;
`/status` e `/session list` mostram o esforço escolhido ou o padrão da CLI.
Alterações de preferência não chegam às sessões existentes.

Claude recebe `--effort` tanto no processo interativo quanto no one-shot. Codex
recebe `effort` em cada `turn/start`; em plan também recebe `reasoning_effort`
nas configurações de colaboração para preservar a escolha. One-shot Codex recebe
`--config model_reasoning_effort="<nível>"` como argumento separado. Sem esforço
explícito esses parâmetros são omitidos. Perfis de permissão, sandbox e ferramentas
continuam determinados apenas pelo modo de execução: esforço não amplia acesso.

A combinação modelo × esforço é validada antes do processo de execução. Preferência
incompatível após troca de modelo ou atualização da CLI recusa novas execuções após
renovação do catálogo, com orientação para outra escolha ou `default`; não há
fallback silencioso. Remover a preferência não depende do catálogo estar disponível.

Por quê: esforço é uma capacidade do modelo, não uma permissão do agente. Usar os
nomes anunciados por cada CLI evita aceitar níveis inexistentes ou reinterpretar a
intenção do usuário. A escolha fixa mantém a sessão coerente entre turnos.

Código: `Dante.Application/Agentes/AgentModelSelection.cs`, catálogo, runners, settings, drivers e
`Telegram/TelegramPollingService.cs`. Testes: settings, runners, drivers reais com
ProcessProbe e `TelegramModelCommandTests` (seleção combinada de modelo e esforço).

---

## Operação local

## AD-28 — Execução contínua como serviço systemd do usuário, com a distro WSL mantida pelo logon do Windows

Status: vigente (#39)

O Worker roda como `dante.service` do **systemd do usuário** (`systemctl --user`), não como serviço de sistema:
mesmo usuário Linux das CLIs, sem root e com acesso direto às credenciais locais (`~/.claude`, `~/.codex`) e a
`~/.dante`. O linger do usuário faz o systemd do usuário subir junto com a distro, sem shell de login. A unit
versionada (`deploy/systemd/dante.service`) não tem segredos: tokens, allowlist, `DANTE_GENERAL_WORKSPACE` e
variáveis de host dos bindings vêm de `~/.config/dante/dante.env`, fora do repositório e com permissão `600`.

O `dante.env` só aceita atribuições literais (`deploy/dante-env.sh`): como o `EnvironmentFile` do systemd não
expande `$VAR`, `~` nem comandos, um valor que dependa do shell chegaria diferente ao Worker (um
`DANTE_GENERAL_WORKSPACE` começando por `$HOME` faria o Worker recusar o path relativo e não iniciar). A
conversão do `~/.config/dante/env` do setup manual remove `export` e recusa o arquivo inteiro diante de qualquer
linha não literal, sem executá-lo, sem gravar parcialmente e citando só números de linha. A validação ocorre
antes de publicar ou instalar; o serviço só é habilitado e iniciado com o token preenchido.

- O Worker publicado (`dotnet publish`) roda de `~/.local/share/dante/app`, fora do checkout em `/mnt/c`; uma
  nova instalação publica ao lado e troca o diretório com o serviço parado.
- `Restart=on-failure` com limite de 5 falhas em 5 minutos: saída limpa (stop, SIGTERM) não reinicia, e um
  erro persistente de configuração (AD-13) não fica em loop.
- `KillMode=mixed`: o `SIGTERM` vai só para o Worker, cujo host encerra polling, jobs e sessões (AD-17); o que
  restar no cgroup recebe `SIGKILL`, sem processos de agente órfãos.
- `NoNewPrivileges=yes`; `PATH` explícito incluindo `~/.local/bin`, onde as CLIs ficam.
- Logs no journald do usuário, uma linha por evento com prioridade (formatter `systemd` do console logger).

O WSL não sobe sozinho com o Windows e desliga uma distro sem processos cliente. A tarefa agendada `DANTE WSL`
(`deploy/windows/Register-DanteAutostart.ps1`) roda no logon do usuário, com privilégio limitado e sem senha
guardada, e mantém um `sleep infinity` na distro via `wslg.exe`, o lançador do WSL sem janela. Por isso o
D.A.N.T.E. fica disponível a partir do logon, não do boot: iniciar antes do logon exigiria tarefa com senha
armazenada ou privilégio maior.

Por quê: é a combinação mais simples que dispensa terminal aberto e mantém o menor privilégio. Serviço de sistema
com `User=` exigiria root para instalar e operar; Task Scheduler chamando o Worker direto perderia o supervisor
(restart, stop gracioso, logs) que o systemd já oferece.

Código: `deploy/`; testes em `LocalServiceDeploymentTests`. Validado com o Worker publicado em unidade
transitória do systemd do usuário (start, logs, stop gracioso e restart após `SIGKILL`); a reinicialização real
da máquina é validação do usuário.

---

## Mídias no Telegram (Epic #92)

## AD-29 — Anexos como entrada neutra, imagem nativa por CLI e artefato só por canal explícito

Status: vigente (#93, spike; recebimento na #94, encaminhamento aos agentes na #95, áudio e vídeo na #96, entrega de artefatos na #97, imagem para LinkedIn na #98); orienta #99. Evidências e contrato completo em
[`docs/spikes/multimodal`](../spikes/multimodal/README.md).

Validado em Claude Code 2.1.287 e codex-cli 0.159.3 (sessão e one-shot):

- **imagem** chega ao modelo nos quatro caminhos, sem mudar modo nem permissão: Claude sessão por blocos `image`
  base64 no `content`; Codex sessão por itens `localImage` (também em `turn/steer`); Claude one-shot pela
  ferramenta `Read` com `--add-dir` do diretório do anexo; Codex one-shot por `-i`;
- **áudio e vídeo** não chegam ao modelo em nenhuma CLI. O `localAudio` do schema do `app-server` é aceito, mas o
  modelo não ouve: suporte não se infere pelo schema nem pela capacidade do modelo;
- **geração de imagem** existe só no Codex (`imageGeneration.savedPath` na sessão, PNG em
  `~/.codex/generated_images/`, cota do plano ChatGPT). Ela redesenha os prints em vez de copiá-los. Claude não
  gera imagem.

Decisões:

- O canal de entrada passa a ser texto opcional + anexos já baixados (`id`, `kind`, `mediaType`, `path`,
  `bytes`, dimensões), sem tipos do Telegram no `SessionRegistry`, nos drivers ou nos runners. Cada driver
  traduz para o formato nativo da sua CLI.
- Anexos ficam fora do checkout, em `~/.dante/attachments/<usuário>/<sessão|job>/` (`700`/`600`), nunca no
  repositório nem no workspace geral. O isolamento da AD-09 se mantém: o Claude one-shot recebe `--add-dir`
  apenas daquele diretório.
- Mídia sem pedido não inicia agente: fica pendente por usuário e contexto, é consumida pelo próximo texto e
  descartada com aviso na troca de contexto ou após 10 min. Álbum vira um único lote. Mídia durante um turno
  entra na fila FIFO como item único (AD-16).
- Limites: até 20 MB por arquivo (`getFile`); imagem JPEG/PNG/GIF/WebP de até 7 MB e 8000 px por lado; até
  10 imagens e 20 MB por turno; retenção até o fim da sessão ou do job. Áudio e vídeo são recusados
  explicitamente até haver ferramenta aprovada (#96). *Substituído na #96: áudio e vídeo são aceitos quando as
  ferramentas locais aprovadas estão instaladas (ver abaixo).*
- Artefato só sai por canal explícito: evento estruturado da CLI (`imageGeneration.savedPath`) ou pedido do
  usuário por caminho dentro do diretório da sessão. Nunca por varredura do workspace nem por path citado na
  prosa. Geração de imagem fica restrita às sessões, porque o one-shot não informa o caminho.
- Ferramenta, dependência ou serviço que ainda não existe na máquina (transcrição, `ffmpeg`, biblioteca de
  imagem, API paga) exige decisão humana registrada antes da Issue que depende dela.

Por quê: o protocolo de cada CLI já transporta imagens, então o D.A.N.T.E. só precisa baixar, limitar, isolar e
correlacionar anexos, sem processar conteúdo. Separar entrada neutra de tradução por driver segue a AD-16, e
exigir canal explícito para artefatos impede que o agente (ou um prompt injetado) faça o bot enviar arquivos
arbitrários do disco.

Implementação do recebimento (#94):

- só mensagens de usuários autorizados chegam ao download;
- `getFile` + download por `ResponseHeadersRead`, com o limite conferido no tamanho declarado, no `Content-Length`
  e nos bytes lidos, timeout de 60 s, `file_path` aceito só como segmentos simples, e o token fora de logs e
  exceções;
- o tipo vem do conteúdo (`ImageInspector`), nunca do nome ou MIME declarados; a foto usa o maior `PhotoSize`
  dentro do limite;
- arquivos `A000001.<ext>` em `~/.dante/attachments/<usuário>/pending/` (`700`/`600`), gravados como `.part` e
  renomeados só depois de validados. O contador reinicia com o processo, então um id só é usado se nenhum arquivo
  de execução anterior (sobra com menos de 24 h) o tiver, em qualquer extensão; nada é sobrescrito;
- itens de um álbum são confirmados juntos após 1,5 s sem item novo do mesmo `media_group_id`, ignorando
  `message_id` repetido;
- a chave de contexto dos pendentes é a sessão ativa ou, sem ela, agente padrão + contexto resolvido (AD-27);
- no estado da #94, a resposta informa que as imagens ainda não vão aos agentes.

Implementação do encaminhamento (#95):

- a entrada neutra é `AgentInput` (texto + `Attachment` na ordem de envio), usada em `AgentSession`, na fila, no
  `SessionRegistry` e nos drivers; um item da fila é uma mensagem com seus anexos;
- legenda é o pedido: a foto ou o álbum vira um turno (sessão ativa, abertura implícita ou fila) e uma legenda
  `/claude`/`/codex` vira one-shot; outra legenda iniciada por `/` é recusada e as imagens ficam pendentes;
- o próximo texto que chega a um agente (mensagem comum, `/steer`, `/claude`, `/codex`) leva todos os pendentes do
  contexto **em que a conversa está**, inclusive quando um `@alias` ou o one-shot sobrepõe agente ou contexto naquela
  execução: o pedido é o que o usuário enviou logo depois das imagens. Troca de contexto por comando continua
  descartando os pendentes;
- recusa antes do driver, sem converter em nome de arquivo nem trocar agente/modelo: anexo de outro usuário, tipo
  diferente de imagem ou driver sem `ImageInput` (hoje Claude e Codex têm). Os arquivos de uma mensagem recusada são
  apagados;
- os consumidos vão para `<usuário>/<sessão|job>/` (`AttachmentStore.MoveTo`); o diretório é apagado quando a sessão
  termina (fechada ou falha) ou o job termina, e limpo antes do uso se sobrou de uma execução anterior com o mesmo id;
- o álbum é um lote indivisível: as imagens só entram nos pendentes quando ele termina, junto com a legenda e no
  contexto em que chegou. Qualquer update posterior do mesmo usuário (texto, comando, outro álbum) conclui antes os
  álbuns abertos dele, então o pedido do álbum mantém o lugar antes do que veio depois; se o contexto mudou por
  outro meio até a janela fechar, imagens e legenda são descartadas juntas, com aviso. A conclusão pela janela é
  serializada com os updates.

Implementação da entrega de artefatos (#97):

- canais: `ArtifactProducedEvent` (do `imageGeneration.savedPath` do Codex), aceito só sob
  `$CODEX_HOME/generated_images` e só se for imagem; e `/send <caminho>`, aceito só sob o diretório de trabalho da
  sessão ativa do usuário. Path em prosa nunca vira envio;
- `ArtifactStore.Capture` resolve links simbólicos componente a componente e exige que o caminho real fique na raiz do
  canal; no Linux, confere pelo `/proc/self/fd` que o arquivo aberto é o verificado. Recusa diretório, arquivo vazio
  ou acima de 50 MB e nomes de credencial/configuração (`.env*`, `id_*`, `*.pem`, `*.key`, `.git/`, `.ssh/`…);
- o aceito vira cópia privada em `~/.dante/artifacts/<usuário>/F000001.<ext>` (`700`/`600`); retry e `/resend` mandam
  a cópia, sem o agente. A cópia vive enquanto o registro está entre os 50 mais recentes: a poda roda ao registrar e
  ao fim de cada upload, do mais antigo para o mais novo, e para num upload ainda pendente (que usa a cópia). Na
  inicialização, todas as cópias são apagadas; links dentro do store são removidos como links, nunca seguidos, e
  cópia ou exclusão nunca passam por um link interno;
- upload multipart: imagem dentro dos limites do `sendPhoto` vai como foto e como documento original; o resto
  (inclusive áudio e vídeo) como documento, com `disable_content_type_detection`. O upload roda em segundo plano, com
  retry de falhas transitórias e aviso no chat em falha definitiva;
- sessão com segredos vinculados não envia arquivo por nenhum canal (AD-10): conteúdo binário não pode ser redigido.

Implementação de áudio e vídeo (#96):

- decisão humana (2026-10-02, registrada na #96): ferramentas locais e gratuitas, instaladas pelo mantenedor —
  `ffmpeg`/`ffprobe` 8.0.1 e `whisper.cpp` 1.8.3 (`whisper-cli`, apt) com o modelo `ggml-small.bin` em
  `~/.dante/models/` (ou `DANTE_WHISPER_MODEL`). Nenhuma API paga nem credencial nova;
- recebimento: voz e áudio são `Audio`; vídeo e video note são `Video`; documento conta pelo MIME declarado, que só
  escolhe o tipo. O contêiner é conferido pelos primeiros bytes (`MediaInspector`: OGG, WebM/MKV, MP4/MOV/M4A, WAV,
  FLAC, MP3), o que recusa playlists e outros textos que fariam o ffmpeg abrir arquivos ou URLs; as trilhas são
  conferidas pelo `ffprobe` na preparação. Até 20 MB, conferido antes do download; sem as ferramentas, recusa antes
  do download com o que instalar. Animação continua recusada;
- preparação (`MediaPreparer`, `MediaTools`): fala vira transcrição com timestamps por trecho e idioma detectado;
  vídeo vira até 6 quadros (JPEG de até 1280 px, no meio de fatias iguais) no lugar do vídeo, na ordem dos anexos,
  mais a transcrição da trilha. Até 10 min de cada mídia; quadros contam no limite de 10 imagens por turno. O texto
  derivado vem depois do pedido, num bloco que declara a proveniência e tudo que não foi analisado (análise parcial,
  entre quadros, sem trilha, quadros fora do limite, transcrição indisponível). O agente nunca recebe o arquivo
  original: o modelo não ouve nem assiste, e suporte não se infere pelo schema (`localAudio`);
- ferramentas com nomes fixos resolvidos em entradas absolutas do `PATH`, `ArgumentList`, sem shell, ambiente mínimo,
  `-protocol_whitelist file` e saída limitada; cancelamento encerra a árvore do processo. Falta de ferramenta é
  verificada antes de executar e vira mensagem acionável; erro do processo nunca expõe caminho nem saída da ferramenta;
- onde roda: dentro do turno, não no recebimento. Na sessão, `MediaPreparingSessionDriver` envolve o driver de cada
  CLI (`AgentDriverCapabilities.MediaInput`): o turno abre na hora, a preparação roda em segundo plano, e só então o
  input preparado inicia o turno upstream. Assim a fila FIFO (AD-16) mantém a ordem, `/status` mostra o turno, a
  interrupção cancela as ferramentas e encerra o turno como interrompido sem tocar a CLI, e falha encerra só aquele
  turno com o motivo. Steer textual durante a preparação vai junto do turno preparado; anexos nesse momento são recusados
  explicitamente, sem alterar o turno, para preservar o orçamento original de imagens e bytes. No one-shot, a preparação roda dentro
  do job (`/cancel` a interrompe; falha encerra o job com o motivo). Limite de 10 min por mensagem;
- `/steer` não leva áudio nem vídeo (a preparação não cabe num turno já em andamento upstream): o registry recusa, e
  o Telegram recusa a orientação mantendo os pendentes para a próxima mensagem comum;
- retenção: original e quadros ficam no diretório da sessão ou do job e saem com ele; o WAV intermediário e o JSON do
  `whisper.cpp` são apagados logo após a transcrição, e tudo que foi derivado é apagado em falha, timeout ou
  cancelamento.

Por quê: preparar dentro do turno reaproveita a fila, o cancelamento e o estado que a sessão já tem, sem bloquear o
long polling durante uma transcrição de minutos; o decorador mantém os drivers e o `SessionRegistry` sem
conhecimento de ferramentas. Ferramentas locais evitam custo e credencial e mantêm a mídia na máquina.

Código: `Attachments/`, `Artifacts/ArtifactStore.cs`, `Sessions/AgentInput.cs`, `Sessions/SessionRegistry.cs`, drivers e runners,
`Telegram/TelegramDeliveryService.Artifacts.cs`,
`Sessions/MediaPreparingSessionDriver.cs`, `Telegram/TelegramMediaReceiver.cs`, `Telegram/TelegramBotApi.cs`,
`Telegram/TelegramPollingService.cs`; testes em `AttachmentStoreTests`, `PendingAttachmentsTests`, `TelegramBotApiTests`,
`TelegramMediaIntakeTests`, `TelegramImageTurnTests`, `MediaPreparerTests`, `MediaSessionTurnTests`,
`LiveMediaEvidenceTests`, `SessionRegistryTests` e nos testes de drivers e runners.

Imagem para LinkedIn (#98):

- decisão humana (2026-10-02, registrada na #98): rota B, montagem determinística local com `ffmpeg`, sem dependência
  nova; a rota A (geração pelo Codex) não é usada, porque redesenha os prints e inventa o texto miúdo. Referência
  visual do mantenedor: vitrine com fundo claro, título, subtítulo, prints em cartões arredondados com sombra,
  etiqueta sob cada print (uma em destaque) e URL no rodapé;
- o agente decide só texto e arranjo: `/vitrine <pedido>` abre um turno da conversa (agente, contexto e modo da sessão)
  cujo texto pede um JSON (`ShowcaseSpec`: formato, título, subtítulo, cor, prints por id com etiqueta e destaque,
  rodapé), validado campo a campo contra os prints da sessão. Nenhuma ferramenta nova é liberada ao agente (sem shell
  nem MCP no General Mode);
- novo canal explícito de artefato: só o turno aberto por `/vitrine` vira imagem. `TelegramShowcase` envolve o sink de
  eventos, segura o texto desse turno (o JSON não vai ao chat), monta o PNG ao fim e o envia por
  `TelegramDeliveryService.SendFile` com raiz no diretório de anexos da sessão (#97: cópia privada, `/resend`, recusa
  em sessão com segredos). Os ids citados só podem ser imagens que o usuário enviou à sessão; resposta fora do formato
  é mostrada com o motivo. Prosa nunca vira arquivo;
- a sessão precisa estar ociosa e com fila vazia, para que o próximo turno seja comprovadamente o pedido; a
  correlação é registrada antes do envio, sem corrida com os eventos;
- montagem (`ShowcaseLayoutBuilder`, `ShowcaseRenderer`): canvas paisagem 1600×900, quadrado 1400×1400 ou retrato
  1200×1500; prints em altura comum, nas linhas que os deixam maiores; textos quebrados por largura estimada. O
  `ffmpeg` roda sem shell, com ambiente mínimo, `-protocol_whitelist file`, timeout de 60 s; textos do agente vão por
  arquivo (`textfile`, `expansion=none`), nunca para a linha de comando, e caminhos do grafo com caracteres especiais
  são recusados. Fonte Inter quando instalada (`fonts-inter`), senão DejaVu Sans;
- cada pedido gera `vitrine-<turno>-v<n>.png` no diretório da sessão, apagado com ela; ajuste é um novo `/vitrine` na
  mesma conversa.

Por quê: colar os prints garante fidelidade ao que o site mostra; deixar ao agente só o JSON mantém o modelo no que
ele faz bem (texto, escolha e ordem) sem ampliar suas permissões, e o canal ligado a um comando explícito do usuário
preserva a regra de que artefato só sai por canal explícito.

Código: `Attachments/`, `Artifacts/ArtifactStore.cs`, `Artifacts/ShowcaseSpec.cs`, `Artifacts/ShowcaseRenderer.cs`,
`Telegram/TelegramShowcase.cs`, `Sessions/AgentInput.cs`, `Sessions/SessionRegistry.cs`, drivers e runners,
`Telegram/TelegramDeliveryService.Artifacts.cs`,
`Telegram/TelegramMediaReceiver.cs`, `Telegram/TelegramBotApi.cs`, `Telegram/TelegramPollingService.cs`; testes em
`AttachmentStoreTests`, `PendingAttachmentsTests`, `TelegramBotApiTests`, `TelegramMediaIntakeTests`,
`TelegramImageTurnTests`, `ShowcaseTests`, `TelegramShowcaseTests`, `LiveShowcaseEvidenceTests`, `SessionRegistryTests`
e nos testes de drivers e runners.

## AD-30 — Input humano por mensagem correlacionada, botões para opções e Reply explícito

Status: vigente (#105); evolui a apresentação de input da AD-22, preservando seu protocolo e timeout.

O transporte Telegram declara `SupportsInputMessages` quando entrega inline keyboards e retorna
`message_id`, inclusive sem botões. No `TelegramBotApi` real, a pergunta não mostra IDs nem
`/input`: uma única pergunta com até dez opções de até 64 unidades UTF-16, sem quebras de linha,
usa um botão por opção. Esses são limites de legibilidade adotados pelo produto, não limites de
texto de botão anunciados pelo Telegram. Os callbacks têm só sessão/turno/request e índice de opção,
sempre dentro do limite de 64 bytes da [Bot API](https://core.telegram.org/bots/api#inlinekeyboardbutton).
Texto livre e opções fora desses limites usam Reply nativo; múltiplas perguntas ficam em um pedido,
com respostas na ordem separadas por `|`. Pedido longo é dividido pelos limites de delivery, com
correlação por Reply em cada parte e botões apenas na última. Segredos vinculados ocultam pergunta
e opções; opções que contenham segredos conhecidos do host não viram labels de botão.

A correlação vive só na entrega em memória, junto dos registros recentes: usuário + chat +
`message_id` → sessão/turno/request. Ela não decide se o request está válido; o `SessionRegistry`
continua sendo a autoridade, conferindo dono, turno, tipo, conteúdo completo e prazo antes de
consumir uma única vez. A opção é recuperada da pergunta ainda pendente no registry, nunca do
texto do callback. Reply é roteado antes de comandos/conversa e não consome anexos pendentes.
Reply não correlacionado ou já encerrado é recusado com orientação, sem iniciar um turno.
Mensagens comuns sem Reply continuam na conversa: não há inferência de input pendente.

`/input` permanece compatível e aceita os IDs visíveis em `/status`; as instruções textuais
são mostradas quando o transporte não oferece a capacidade. `InputResolvedEvent` notifica a
entrega após a resposta chegar ao driver; `RequestClosedEvent` informa o encerramento de inputs
na interrupção/fechamento. Expiração, conclusão do turno e erro também encerram a apresentação.
Esses eventos não criam outra máquina de estados. A mensagem é editada e o keyboard removido
com a mesma serialização, retry e tratamento de corrida durante envio usados pelas aprovações.

Por quê: a interação normal deve parecer conversa, sem cópia de IDs e sem adivinhar a qual pedido
uma mensagem comum se destina. Correlacionar a mensagem e revalidar no registry preserva a
segurança da AD-22 mesmo com vários pedidos ou após seleção de outra sessão.

Código: `Telegram/TelegramDeliveryService.Inputs.cs`, `Telegram/TelegramPollingService.Inputs.cs`,
`Telegram/TelegramInputCallback.cs`, `Telegram/TelegramUpdate.cs`, `Sessions/SessionRegistry.cs`.
Testes: `TelegramInputInteractionTests`, `TelegramBotApiTests` e regressões de delivery/registry.

## Cotas de uso (Epic #114)

## AD-31 — Cotas da assinatura lidas da própria CLI, em processo efêmero, sem cache e com métrica ausente explícita

Status: vigente (#115, spike; `/uso` com Codex na #116 e Claude na #117). Evidências, matriz e contrato completo em
[`docs/spikes/usage-quotas`](../spikes/usage-quotas/README.md).

Validado em Claude Code 2.1.287 (claude.ai Pro) e codex-cli 0.159.3 (ChatGPT Plus), sem iniciar turno:

- **Codex**: `account/rateLimits/read` no `app-server`, método documentado e estável. Janelas identificadas pela
  duração comprovada (`windowDurationMins` 300 = sessão de 5 h, 10080 = semana), nunca pela posição
  `primary`/`secondary` nem por ser "curta"; buckets por `limitId`, e só o `codex` é a cota geral;
- **Claude**: `control_request` `get_usage` (`skip_behaviors: true`) no stream-json. É **experimental** na CLI
  (o SDK o expõe como `usage_EXPERIMENTAL_MAY_CHANGE_DO_NOT_RELY_ON_THIS_API_YET`) e ausente da documentação
  pública; `five_hour` = sessão, `seven_day` = semana geral, semanas por modelo à parte. `rate_limit_event` só
  existe durante turnos e o `/usage` como prompt devolve texto para humanos, por isso nenhum dos dois é a fonte.

Decisões:

- A consulta roda num processo efêmero da CLI do agente pedido, no General workspace com o ambiente do General Mode
  (o mesmo caminho do catálogo de modelos, AD-25): usa a conta que atende os agentes, não exige sessão e não toca
  em sessão, turno, request, modo, modelo ou esforço existentes. O D.A.N.T.E. nunca chama endpoint de uso do
  provedor diretamente nem lê credenciais.
- Contrato neutro por agente: janela de sessão, semana e janelas adicionais rotuladas pelo provedor, cada uma com
  percentual 0–100, reset e duração quando informados, mais o instante da consulta. Métrica ausente é
  "indisponível" com motivo, nunca zero nem estimativa por tokens/custo; falha de consulta é erro classificado
  (sem login, sem assinatura, CLI sem suporte, timeout, falha do serviço, cancelamento).
- Tempo restante calculado do reset real com relógio injetável; reset vencido não é renovação confirmada.
- Sem cache no D.A.N.T.E.: a resposta mostra o horário da consulta. O Claude Code pode responder com leitura própria
  de até 60 s, ou até 1 h quando o serviço falha, sem sinalizar; isso fica documentado como limite.
- Nenhum limite substitui outro: sem o bucket `codex`, sessão e semana ficam indisponíveis e os buckets presentes
  aparecem à parte; janela de duração não comprovada também fica à parte, nunca vira sessão.
- A forma do `get_usage` é revalidada a cada versão do Claude Code; resposta fora do schema vira "CLI sem suporte",
  nunca valor inventado. Isso inclui percentual fora de 0–100 (nas duas CLIs, sem corte) e `rate_limits_available`
  ausente ou não booleano: só `false` explícito é conta sem cota de assinatura.

Por quê: as duas CLIs já sabem ler a cota da conta autenticada sem custo de modelo. Perguntar a elas mantém a
autenticação e os segredos onde já estão (AD-04) e evita depender de endpoints privados, enquanto o processo
efêmero isola a consulta das sessões vivas.

## Persistência do D.A.N.T.E. Brain (Epic #133)

## AD-34 — PostgreSQL canônico, índices derivados e portabilidade lógica do Brain

Status: decisão de armazenamento (#135), para implementação pela #160 após a
arquitetura conceitual da #134. O Worker atual continua sem banco; não há adapter
ou migrations Brain disponíveis nesta decisão.

PostgreSQL é a fonte de verdade estruturada escolhida pelo humano: identidade,
Spaces/Projects, itens/relações/proveniência/validade/sensibilidade, fontes e
snapshots. Full-text nativo é o mecanismo lexical; pgvector armazena embeddings
com modelo/versão/dimensão e origem revisada, como índice reconstruível. Filesystem
privado guarda originais quando apropriado, com ID lógico/hash/referência no banco.

Separar dados canônicos (`brain_data`), ledger/metadados (`brain_meta`) e derivados
(`brain_index`); o canônico não depende de tipos/funções de vector. Falha de
semântica permite lexical; falha de indexação não perde commit canônico. Geração
nova de embedding/reindexação não altera identidade/proveniência do conhecimento.

Migrations explícitas e imutáveis, checksum/lock/versão, runtime sem superuser/DDL,
queries parametrizadas com escopo obrigatório e concorrência por revisão. IDs e
contratos do domínio não expõem SQL, provider ou tipos físicos. Não migrar estado
volátil das sessões/jobs nem substituir stores JSON existentes nesta Epic.

Backup local mínimo congela writers/ingestão/GC, combina dump canônico + originais
e manifesto de hashes/versões/contagens, e valida restore em banco vazio com
rebuild derivado. PostgreSQL não garante atomicidade com filesystem; referências
só são publicadas após finalizar bytes imutáveis. Export autorizado em Markdown +
JSON versionado preserva IDs/metadados/relações/proveniência, sem aprisionamento no
provider. Backup privado e export redigido têm finalidades distintas.

Por quê: PostgreSQL atende integridade/transações e recuperação lexical/vetorial
no mesmo armazenamento, preservando domínio e export independentes; índices
reconstruíveis evitam tornar o serviço/modelo de embeddings fonte de verdade.
Não reabrir PostgreSQL versus SQLite/Markdown nesta Issue.

ADR completa, operação preparatória WSL, backup/restore e contrato/testes para
#160: [PostgreSQL Storage](../brain/POSTGRESQL_STORAGE.md).
Roteiro local documentado, não executado com banco real nesta Issue: daemon Docker
indisponível na distro. #160 deve produzir evidência de migrations, isolamento,
integridade, restore canônico sem vector e reconstrução dos índices.

## Contexto das sessões (Epic #118)

## AD-32 — Clear e compact pelos mecanismos nativos de cada CLI, confirmados upstream e só com a sessão ociosa

Status: vigente (#119, spike; `/clear` implementado na #120 e `/compact` na #121). Evidências, matriz e contrato em
[`docs/spikes/clear-compact`](../spikes/clear-compact/README.md).

Validado em Claude Code 2.1.287 e codex-cli 0.159.3, no mesmo processo da sessão e com os parâmetros dos drivers:

- **Claude**: `/compact` e `/clear` enviados como mensagem `user` no stream-json, como documenta o Agent SDK. Compact
  confirma por `compact_boundary` (com `pre_tokens`/`post_tokens`) e mantém o `session_id`; clear confirma por
  `conversation_reset` e troca o `session_id`;
- **Codex**: compact por `thread/compact/start`, que roda como um turno próprio com item `contextCompaction`; não há
  reset de thread, então clear é um novo `thread/start` efêmero no mesmo processo, com as políticas efetivas, seguido de
  `thread/unsubscribe` da antiga.

Decisões:

- Cada driver implementa `ClearContextAsync`/`CompactContextAsync` com esses mecanismos; o resto do D.A.N.T.E. vê só o
  resultado neutro (limpo com novo id upstream; compactado com métricas quando o upstream as informa; nada a compactar;
  falha com contexto anterior intacto; incerto).
- Sucesso só depois da confirmação upstream. Estado incerto encerra a sessão com erro, como na troca de modo (AD-24).
- As operações exigem sessão ociosa (sem turno, fila, approval/input, preparação de mídia ou troca de modo pendente) e
  passam pela mesma serialização do `SessionRegistry`: o Codex interrompe o turno ativo se receber a compactação, e o
  D.A.N.T.E. não pode depender de recusa upstream.
- Métrica de redução só do Claude (`compact_metadata`); o Codex não informa e nada é estimado.
- Texto de usuário que comece com `/` não chega ao Claude em turno ou steer: a CLI o executaria como comando.

Por quê: os dois protocolos já oferecem a operação sem endpoint privado nem prompt improvisado; manter o processo
preserva modelo, esforço, modo e diretório sem reabrir a sessão, e a confirmação explícita evita anunciar uma limpeza
ou compactação que não ocorreu.

## D.A.N.T.E. Brain (Epic #133)

## AD-33 — Brain como núcleo de conhecimento com recuperação seletiva, separado da sessão e do histórico

Status: vigente como direção arquitetural (#134); implementação pelas Issues da
Epic #133. Nenhum componente Brain está implementado por esta decisão.

Decisões:

- Evoluir o monólito modular local com um módulo Brain independente de Telegram,
  Claude/Codex e do mecanismo físico de persistência. O D.A.N.T.E. controla o
  conhecimento canônico; agentes apenas consomem contexto e sugerem candidatos.
- Conversation History, Working Memory e Knowledge têm ciclos de vida distintos.
  Working Context Snapshot é curto/substituível e não persiste chain-of-thought;
  Knowledge Items não são transcript. Fechar/limpar/compactar uma sessão não apaga
  automaticamente o conhecimento ou snapshot.
- Escopo obrigatório por TenantId + UserId + KnowledgeSpaceId + ProjectId opcional.
  Project pertence a um Space, que também pode conter conhecimento sem Project.
  IDs são estáveis/opacos; allowlist do Telegram não substitui policy de acesso.
- Proveniência, revisão, status, validade e sensibilidade pertencem ao contrato
  canônico. Inferência/confiança não equivalem a confirmação; correção/substituição
  preserva origem, enquanto exclusão solicitada impede reinjeção por derivados.
- Captura passa por candidatos e confirmação/classificação; índices/embeddings e
  resumos automáticos são reconstruíveis. SourceDocument bruto não vira fato.
- Context Builder é o componente central: busca autorizada, expansão limitada por
  relações, priorização, deduplicação e orçamento explícito → Context Pack rastreável.
  Filtrar antes de recuperação/injeção/exportação; Secret não é injetado automaticamente.
  Conteúdo recuperado não amplia permissões nem ganha autoridade de instrução de sistema.
- Economia de contexto é hipótese medida com continuidade/qualidade, não promessa.
  Semântica indisponível não deve impedir busca lexical; ausência de contexto
  pertinente não autoriza fallback ao transcript inteiro ou a outros Spaces.
- A #135 define storage/índices sem alterar esses contratos; #160 implementa a
  fundação após #134 e #135. Não incluir ComfyUI, execução durável, workers remotos,
  SaaS, framework de tools ou frontend completo nesta Epic.

Por quê: desacoplar vida da sessão de conhecimento útil permite continuidade entre
agentes sem acumular histórico irrestrito, e o escopo/policy evita que recuperação
seletiva transforme informação inferida, obsoleta ou sensível em contexto autorizado.

Contrato completo: [Arquitetura alvo](../maintainer/ARCHITECTURE.md#16-arquitetura-alvo-do-dante-brain-133-134).
Fluxos: [Brain](../maintainer/FLOWS.md#22-fluxos-alvo-do-brain-134-ainda-não-implementados).
Verificação futura: #138/#139/#140/#145/#147/#150/#155/#156; não altera runtime/testes atuais.

## AD-35 — Arquitetura hexagonal explícita com migração incremental do legado

Status: vigente (#165, Epic #164).

Domain não referencia camadas internas nem providers. Application referencia
Domain e define casos de uso/portas. Infrastructure referencia Application/Domain
e implementa portas. Worker e WebApi referenciam Application/Infrastructure,
como composition roots/adapters de entrada, sem referência entre os hosts.
`AddApplication()` e `AddInfrastructure(configuration)` são os contratos comuns.
Testes arquiteturais impedem referências proibidas e providers no núcleo.

O legado permanece temporariamente no Worker (#166/#167); somente seu registro
DI foi agrupado em AddWorker. Código novo do Brain e adapters não nasce no Worker.
Não introduzir regras funcionais ou reescrever Telegram/CLIs nesta fundação.
WebApi tem scaffold independente; operação HTTP fica na #170. EF Core (#168),
Mapster (#169) e CRUD (#171) são entregas separadas. O PR #163 não é incorporado;
a #160 aguarda os gates definidos na Epic #164.

Por quê: separar decisões do núcleo de detalhes de infraestrutura e dos hosts
permite crescer sem transformar Worker/WebApi em fonte de regras de negócio.

## AD-36 — Identidade Guid Id e vocabulário PT-BR no código novo do núcleo

Status: vigente (#165, Epic #164). Refina o vocabulário conceitual inglês da AD-33
para a implementação; não renomeia oportunisticamente o legado.

Toda entidade persistente usa `Guid Id` através de `EntidadeBase`; sem parâmetro
`TId`. A implementação das bases é da #171. Propriedades identificadoras/FKs têm
prefixo Id: IdTenant, IdUsuario, IdEspacoDeConhecimento, IdProjeto.

Entidades, value objects, enums/valores, propriedades, métodos, DTOs/SearchDtos,
AppServices, repositories específicos e casos de uso novos usam PT-BR sem acentos
nos identificadores. Sufixos estruturais permanecem Repository, AppService, Dto e
SearchDto; nomes técnicos externos (DbContext, EF Core, Mapster, Telegram, Claude,
Codex) permanecem como estabelecidos. Namespaces/pastas seguem as camadas e o
conceito/caso de uso/adapter. Vocabulário canônico no glossário.

Assinaturas padrão: IRepository<TEntity>, Repository<TEntity>,
ICrudBasicoAppService<TDto,TSearchDto,TEntity> e
CrudBasicoAppService<TDto,TSearchDto,TEntity>. Bases e regras ficam na #171;
entidades semânticas do Brain ficam nas respectivas issues. DTOs não expõem
entidades EF e não podem contornar invariantes ou redefinir identidade.

Por quê: uma identidade global simples e vocabulário consistente reduzem contratos
incompatíveis entre funcionalidades e evitam tradução duplicada pelos hosts.

## AD-37 — Mapster na Application com projeções/fábricas explícitas e configuração isolada

Status: vigente (#169, Epic #164).

Mapster 10.0.13 fica na Application, sem referência no Domain. AddApplication
registra IMapsterTypeAdapter; mappings são centralizados na Application e
compartilhados pelos hosts. Configuração própria por service provider, compilada
antes do uso e finalizada, sem GlobalSettings/scan automático. Registro explícito
por par/direção, com expressão MapWith que define todos os membros/constructor.

Sem cópia automática por nomes, direção inversa implícita ou atualização sobre
entidade existente. Saída expõe somente campos explicitamente escolhidos; criação
usa constructor/fábrica com invariantes e identidade do domínio. Atualização
chama métodos da entidade a partir do AppService, nunca map-to-target. SearchDto
não é transformado automaticamente em consulta. DTOs específicos usam PT-BR.

Por quê: compartilhar conversões sem transferir autorização/invariantes aos
hosts ou permitir que campos novos/sensíveis/Ids sejam copiados por convenção.
Operação/limites e exemplos no [guia de mapping](../development/mapping.md).

## AD-38 — Extração do legado em lotes: nome legado preservado, portas novas em PT-BR

Status: vigente (#166, Epic #164). Aplica AD-35/AD-36 à migração do legado.

O legado sai do Worker em lotes com fronteira clara e sem mudança de comportamento.
Domain recebe só conceito com regra/invariante e sem detalhe operacional: nenhum
caminho de execução, workspace, GitHub, variável do host ou ambiente/segredo no seu
modelo. Caso de uso e seus contratos de entrada/saída operacionais vão para a
Application, que recebe as dependências externas por portas novas implementadas
pelos adapters do Worker (implementação explícita da interface, sem mudar a API
pública do adapter). Modelo persistido de adapter permanece no adapter; a porta
expõe só o que o caso de uso precisa.

Tipo legado movido mantém o nome em inglês, marcado no arquivo como exceção
temporária até uma migração explícita; isso não vale para código novo. Portas e
contratos criados na extração nascem em PT-BR. Para evitar big-bang de `using`,
Worker e testes importam os namespaces que receberam tipos movidos por `<Using>`
global no `.csproj`, também marcado como compatibilidade temporária.

Primeiro lote: `AgentKind` (`Dante.Domain.Agentes`) e `AssistantSettings`
(`Dante.Domain.Preferencias`) no Domain; `AgentContextResolver`, `JobExecutionContext`
e `ResolvedRepositoryEnvironment` em `Dante.Application.Contextos`, sobre as portas
`IWorkspaceGeral`, `ICatalogoDeRepositorios` (que expõe `RepositorioCadastrado`:
alias e caminho) e `IPreferenciasDoAssistente`, implementadas por `GeneralWorkspace`,
`RepositoryRegistry` e `AssistantSettingsStore`. `RepositoryDefinition` e
`RepositoryEnvironmentEntry` (caminho, GitHub, bindings do host) seguem no adapter.

Por quê: mover em lotes pequenos mantém cada PR revisável e o comportamento
coberto pelos testes existentes, sem renomeação em massa do legado.

Código: `src/Dante.Domain`, `src/Dante.Application/Contextos`; testes em
`HexagonalArchitectureTests` (referências do núcleo compilado, ausência de `TId`,
Domain sem conceitos operacionais/de host,
ausência de IO no código do núcleo, adapters das portas) e `AgentContextResolverTests`.


## AD-39 — Base CRUD com identidade do domínio, atualização por métodos e pesquisa específica

Status: vigente (#171, Epic #164). Implementa as bases previstas na AD-36.

`EntidadeBase` fica no Domain com `Guid Id` gerado pelo domínio e setter protegido.
`IRepository<TEntity>`, `IUnitOfWork`, `ICrudBasicoAppService<TDto,TSearchDto,TEntity>` e
`CrudBasicoAppService<TDto,TSearchDto,TEntity>` ficam em `Dante.Application.Comum`, com
`TEntity : EntidadeBase` e sem `TId`. A base recebe repository, unit of work e
`IMapsterTypeAdapter` explicitamente no construtor e confirma cada escrita uma vez.

Criação usa o mapping `TDto → TEntity` registrado (constructor/fábrica do domínio,
AD-37). Atualização carrega a entidade e chama o hook abstrato `AplicarAlteracoes`,
em que o AppService específico chama métodos do domínio; o Id vem do parâmetro, nunca
do DTO. Pesquisa chama o hook abstrato `ConsultarAsync`, que valida o SearchDto e usa
uma consulta do repository específico. Operações são virtuais para extensão.

Por quê: compartilhar o fluxo comum sem permitir que mapping ou base genérica
contornem invariantes, redefinam identidade ou transformem SearchDto em consulta
arbitrária. Implementações EF de repository/unit of work ficam na #168.

Código: `src/Dante.Domain/Comum`, `src/Dante.Application/Comum`; testes em
`CrudBasicoAppServiceTests`. Guia: [base CRUD](../development/crud.md).

## AD-40 — EspacoDeConhecimento como limite do Brain, com proprietário fixo e arquivamento somente leitura

Status: vigente (#152, Epic #133). Primeira entidade funcional sobre a base CRUD (AD-39).

`EspacoDeConhecimento : EntidadeBase` fica em `Dante.Domain.EspacosDeConhecimento`; contratos,
DTOs e AppService em `Dante.Application.EspacosDeConhecimento`. O espaço tem proprietário
`IdUsuario` obrigatório e imutável, nome obrigatório (aparado, até 100 caracteres, sem
caracteres de controle), descrição opcional (até 1000) e estado `Ativo`/`Arquivado`. Nome
e descrição são apresentação: `Atualizar` os troca de forma atômica sem mudar Id nem
proprietário. Espaço arquivado é somente leitura até `Reativar`; arquivar/reativar exige o
estado oposto. Arquivar não apaga nem move o conteúdo.

O espaço não referencia Projeto, Telegram, EF Core ou PostgreSQL: Projetos e conhecimento
apontam para ele por `IdEspacoDeConhecimento`, e conhecimento pode pertencer direto ao
espaço, sem Projeto. A pesquisa exige o proprietário — não existe listagem global — e por
padrão omite arquivados. O mapping de criação usa o constructor do domínio e ignora Id e
estado do DTO; a atualização não muda proprietário nem estado, que só mudam por
`ArquivarAsync`/`ReativarAsync`.

O AppService não é registrado em DI enquanto não houver implementação de
`IEspacoDeConhecimentoRepository` e `IUnitOfWork` (#160 sobre a #168): registrá-lo antes
quebraria a validação de DI dos hosts. Tenant e autorização por usuário (#150), seleção
do espaço ativo e unicidade de nome por proprietário ficam para as respectivas issues.

Por quê: um limite de isolamento com proprietário estável e transições explícitas impede
que a apresentação (nomes) ou o mapping redefinam identidade e escopo do conhecimento.

Código: `src/Dante.Domain/EspacosDeConhecimento`, `src/Dante.Application/EspacosDeConhecimento`;
testes em `EspacoDeConhecimentoTests` e `EspacoDeConhecimentoAppServiceTests`.

## AD-41 — Adapters de saída do legado na Infrastructure, compostos por AddInfrastructure

Status: vigente (#167, Epic #164). Aplica AD-35/AD-38 aos adapters externos do Worker. A AD-40
é reservada à #152, desenvolvida em paralelo.

Lote da #167, sem mudança de comportamento:

| Antes (Worker) | Agora |
| --- | --- |
| `GeneralWorkspace`, `RepositoryRegistry`/`RepositoryDefinition`, `AssistantSettingsStore` | `Dante.Infrastructure.Contextos` |
| processos, runners, `InteractiveAgentProcess`, `AgentModelCatalog` | `Dante.Infrastructure.Agentes` |
| `UsageQuotaReader` | `Dante.Infrastructure.Uso` |
| `IClaudeRunner`, `ICodexRunner`, `AgentProcessResult`/`Status`, `IAgentModelCatalog`/`AgentModelInfo`, `AgentModelSelection` | `Dante.Application.Agentes` |
| `IUsageQuotaReader` e o relatório de cotas | `Dante.Application.Uso` |
| `Attachment`/`AttachmentKind` (anexo neutro da AD-29) | `Dante.Application.Anexos` |
| `AgentPermissionProfile` | `Dante.Domain.Agentes` |

Portas consumidas pelos casos de uso do Worker ficam na Application; abstrações técnicas
usadas só por adapters (`IAgentProcessExecutor`, `IAgentExecutableResolver`,
`IInteractiveAgentProcessLauncher`, `AgentProcessRequest`) ficam na Infrastructure com
eles. `AddInfrastructure` registra os adapters migrados para os dois hosts; nas portas de
contexto, porta e tipo concreto resolvem a mesma instância, porque o Telegram ainda usa
a API de escrita dos adapters legados. `AddWorker` compõe só o que continua no Worker.

O Domain recebe só o perfil de permissão, conceito independente de agente e de canal.
`AgentSessionModes` (nomes, alias `approval`, rótulos e descrições exibidos) é apresentação
e continua no Worker; `AgentModelSelection` (padrão e sintaxe de modelo/esforço das CLIs) é
contrato operacional da execução e vai para a Application. O formato persistido do modo no
`settings.json` (nome do perfil em minúsculas, o mesmo exibido) é definido pelo próprio
adapter, sem depender da apresentação. Um teste arquitetural impede rótulos, textos de
exibição, Telegram e semântica de CLI nos membros e no código-fonte do Domain.

Nomes legados continuam em inglês, marcados no arquivo como na AD-38. Diferente da #166,
os namespaces esvaziados (`Dante.Worker.Agents`, `.Repositories`, `.Settings`, `.Usage`)
deixam de existir, então cada consumidor troca o `using` antigo pelos namespaces novos que
de fato usa, sem `<Using>` global adicional. Infrastructure expõe internals só a
`Dante.Tests`, como o Worker.

Seguem no Worker para lotes futuros: drivers e contratos de sessão (`Sessions/`), jobs,
anexos e mídia (`Attachments/`: store, ferramentas ffmpeg/whisper), artefatos e `/vitrine`
(`Artifacts/`) e o cliente da Bot API, que é transporte do Telegram. EF Core/PostgreSQL
ficam na #168; nenhum `Repository<TEntity>` existe ainda para ser reutilizado.

Por quê: Worker e WebApi passam a compartilhar os adapters pela Infrastructure, e o Worker
fica restrito a host, Telegram e o núcleo de sessões ainda não extraído, sem big-bang.

Código: `src/Dante.Infrastructure/{Contextos,Agentes,Uso}`, `src/Dante.Application/{Agentes,Uso,Anexos}`,
`src/Dante.Domain/Agentes/AgentPermissionProfile.cs`; testes em `HexagonalArchitectureTests`.

## AD-42 — Projeto preso a um EspacoDeConhecimento, com repositório Git como associação opcional

Status: vigente (#136, Epic #133). Segunda entidade funcional sobre a base CRUD (AD-39), no
padrão da AD-40.

`Projeto : EntidadeBase` fica em `Dante.Domain.Projetos`; contratos, DTOs e AppService em
`Dante.Application.Projetos`. O projeto pertence a exatamente um espaço
(`IdEspacoDeConhecimento` obrigatório, fixo na criação), tem nome obrigatório (aparado, até
100 caracteres, sem caracteres de controle), descrição/objetivo opcional (até 1000) e estado
`Ativo`/`Arquivado`. `Atualizar` troca nome e descrição de forma atômica, sem mudar Id nem
espaço. Projeto arquivado é somente leitura até `Reativar`, e arquivar/reativar exige o
estado oposto.

Repositório Git é associação opcional, nunca identidade: `AliasDoRepositorio` guarda o alias
de um repositório cadastrado, trocado ou removido sem mudar o Id. `AssociarRepositorioAsync`
valida o alias pela porta `ICatalogoDeRepositorios` e grava o alias do catálogo; repositório
removido do catálogo depois não invalida o projeto. O projeto não guarda caminho, working
directory nem GitHub, e não muda o Repository Mode, o `/use` nem o diretório das sessões.

A criação exige espaço existente e ativo (espaço arquivado é somente leitura, AD-40). O
mapping de criação usa o constructor do domínio e ignora Id, alias e estado do DTO; a
atualização não muda espaço, associação nem estado, que só mudam pelas operações próprias.
A pesquisa exige o espaço (sem listagem global) e por padrão omite arquivados. Como na AD-40,
o AppService não é registrado em DI até haver persistência (#160); seleção do projeto ativo,
autorização por usuário (#150) e unicidade de nome por espaço ficam para as respectivas issues.

Por quê: o trabalho persistente precisa sobreviver a sessões, troca de agente e troca de
repositório sem que caminho, alias ou contexto de execução virem sua identidade ou escopo.

Código: `src/Dante.Domain/Projetos`, `src/Dante.Application/Projetos`; testes em `ProjetoTests` e
`ProjetoAppServiceTests`.

## AD-43 — Fundação EF opcional, transação por UoW e xmin shadow

Status: vigente (#168, Epic #164). AD-42 define o Projeto da #136.

AddInfrastructure registra persistência scoped somente com ConnectionStrings:Dante;
não aplica migrations automaticamente. DanteDbContext usa configurations concretas
na Infrastructure e migrations EF explícitas com histórico em brain_meta. Tabelas/
colunas usam snake_case PT-BR em brain_data; brain_index é reservado a derivados.
A primeira migration prepara schemas sem tabelas funcionais (#160). Down preserva
schemas compartilhados. Nenhum EF/provider entra no Domain/Application.

EntidadeConfiguration fixa Guid Id gerado pelo domínio e token shadow uint Versao
(xmin). Repository exige tracking do mesmo contexto para atualizar/remover, preservando
o token original. UoW confirma alterações de todos os repositories numa transação
SaveChanges; conflitos viram ConflitoDeConcorrenciaException sem expor EF. Não há
retry automático; descarte do escopo e reload são necessários após falha.

Por quê: permite consumo pela #160 sem acoplar domínio a tipos físicos ou exigir
banco no Worker atual, e evita lost updates/commits parciais. A conexão fica externa
e não é herdada pelas CLIs. Full-text/pgvector não entram na fundação canônica.

Código e limites: [guia de persistência](../development/persistence.md); testes
PersistenciaTests com entidade fictícia e PostgreSQL real opt-in.

## AD-44 — Conhecimento canônico com evidência e revisões, separado de confirmação e validade

Status: vigente (#138, Epic #133). Aplica os conceitos da AD-33 no Domain/Application.

Conhecimento usa Guid Id, espaço obrigatório/projeto opcional imutáveis, tipos/status
PT-BR, conteúdo e/ou JSON, tags, confiança opcional e Sensibilidade. Proveniência
identifica ator, origem e referência/revisão/trecho de evidência. Autor original é
fixo; snapshots imutáveis registram cada criação/confirmação/correção/invalidação/
substituição com ator e instante. Correções validam revisão esperada e preservam
versões anteriores. Não há transcript, captura automática ou índice derivado.

Criação só em Inferido/Temporario. Confirmar é explícito e Tipo Inferencia exige
reclassificação com evidência antes de confirmação. Confiança não confirma; corrigir
Confirmado devolve Inferido. Validade temporal [desde, até) é independente de status.
Substituição liga item anterior a outro ativo no mesmo espaço/projeto e mantém
conteúdo/proveniência; Substituido/Inativo não recebem novas mutações. Exclusão
explícita do CRUD é distinta de invalidar, e sua implementação física/derivados
permanece nas #160/#149/#153.

Application valida existência/associação de espaço e projeto e recusa escrituras
em escopos arquivados. Pesquisa é sempre por espaço, limitada e com filtros explícitos;
DTOs não redefinem identidade/escopo/autor/histórico/timestamps. UoW confirma cada
escrita uma vez. Sem DI do AppService até repository concreto, como AD-40/AD-42.

Por quê: preservar fonte e evolução do conteúdo evita transformar confiança ou
repetição em fato confirmado e permite correção sem perder evidência. Autorização
por identidade e policy de sensibilidade ainda são #150/#155; PostgreSQL/EF não
entram no núcleo. Contratos, limites e operações: [Conhecimento](../development/knowledge.md).

## AD-45 — Identidade e escopo do Brain pertencem ao adapter autorizado

Status: vigente (#150).

AutorizacaoDoBrain é scoped e recebe identidade uma única vez por operação, antes do
acesso. AcessoAoBrain é um seletor, não uma credencial: busca/leitura/manutenção
comparam usuário, espaço, projeto e permissões com o contexto autorizado. Os adapters
EF aplicam filtros por tenant/proprietário/escopo e sensibilidade, inclusive nas
leituras por ID, e validam escritas antes do commit. O tenant é persistido no espaço;
projetos e itens herdam esse limite por vínculo ao espaço. Não há RBAC/login comercial.

Telegram mapeia somente a allowlist válida para Guid determinístico por tenant/ID,
independente de username. Configuração ausente/malformada e identidade não resolvida
negam acesso. Contextos diretos são reservados à administração explícita e fixtures;
DI injeta o contexto autorizado mesmo quando ainda não há identidade. Migrations não
são executadas automaticamente. O tenant local preserva a simplicidade single-user.

## AD-46 — Contexto do Brain injetado no turno da conversa natural, por conversa upstream

Status: vigente (#145).

Mensagem comum que vai a uma sessão (aberta ou continuada) leva o PacoteDeContexto do
escopo Brain selecionado no chat/tópico como dado citado antes do pedido do usuário, sem
autoridade de sistema. O primeiro envio de cada conversa upstream é bootstrap (orçamento
2048 tokens estimados, até 12 itens); os seguintes são refresh (1024, até 6) e omitem as
chaves já injetadas na mesma revisão. Conta como injetado só o que a sessão aceitou
(turno iniciado ou enfileirado). `/clear`, `/compact` confirmado e troca de espaço/projeto
reiniciam o bootstrap; nenhum deles grava ou apaga Brain/ContextoDeTrabalho. O estado
por sessão vive em memória, com chaves/revisões/custo e nunca conteúdo, e reinício do
Worker só antecipa um novo bootstrap. `/steer`, `/vitrine` e one-shot não recebem pacote.

Por quê: a continuidade vem do Brain, não do transcript; repetir o pacote a cada turno
gastaria contexto, e só o adapter sabe quando a conversa upstream foi limpa. Troca de
escopo segue a regra de AD-20: a sessão ativa continua e o aviso orienta `/clear`.

Código: `Telegram/TelegramBrain.cs` (`IContinuidadeDoBrain`) e `TelegramPollingService`;
testes em `ContinuidadeDoBrainTests`. Contrato: [continuidade](../development/continuidade-brain.md).

## AD-47 — Métricas do Brain em JSONL local, sem conteúdo e com limiares fixos

Status: vigente (#148).

A medição registra envios aceitos, fins de turno e avaliações humanas em
`~/.dante/brain/metricas.jsonl`, fora do banco canônico, do backup e da exportação: são
observação operacional, não conhecimento. Só números, resultado, agente e IDs de escopo
e sessão. O histórico bruto de referência é medido na própria conversa (pedidos sem o
pacote e respostas); uma retomada compara a soma dos seus pacotes com o histórico das
sessões anteriores do escopo. Limiares fixos (ganho ≤ 0,50; neutralidade ≤ 1,00) evitam
ajustar o critério ao resultado. O uso informado pelas CLIs entra no `TurnCompletedEvent`
quando existe e é exibido como está; ausência é "indisponível". Cada turno medido herda o
escopo do envio que o abriu, por uma correlação opaca do `AgentInput` que a sessão
devolve na conclusão daquele turno, mesmo após fila, steer ou troca de escopo.

Por quê: a economia de contexto é hipótese da Epic #133; validá-la exige baseline e
custo comparáveis, sem transcript e sem misturar telemetria ao Brain.

Código: `Application/MetricasDoBrain`, `Infrastructure/MetricasDoBrain`,
`Telegram/MetricasDeSessaoDoBrain.cs`; testes em `MetricasDoBrainTests` e
`ContinuidadeDoBrainTests`. Contrato: [métricas](../development/metricas-brain.md).

## AD-48 — Infrastructure organizada por responsabilidade técnica

Status: vigente (#196, Epic #195). Complementa a AD-43 sem mudar seu comportamento.

`Dante.Infrastructure` separa persistência por responsabilidade, com namespace igual à
pasta: `Data` (DbContext e factory de design), `Data/Migrations` (migrations e
snapshot), `Persistence` (infraestrutura genérica: Repository, UnitOfWork e a base de
configuration), `Modulos` (configurations e repositories específicos de entidades) e
`Banco` (administração explícita do PostgreSQL). Nomes técnicos de pasta podem ficar
em inglês; conceitos de domínio seguem PT-BR. Adapters de consulta de uma feature ficam
na pasta da feature (`BuscaDoBrain`, `AuditoriaDoBrain`, `QualidadeDoBrain`...).

Migrations continuam no assembly da Infrastructure, sem projeto Migrator. O EF
identifica migrations pelo id do atributo, não pelo namespace: mover arquivos e trocar
namespace não altera o histórico em `brain_meta` nem exige migration nova.

Por quê: `Persistencia` acumulava contexto, migrations, mappings, repositories e
administração, o que dificultava localizar responsabilidades e encolher o DbContext.
A divisão por entidade dentro de `Modulos` fica na #197.

## AD-49 — Application por feature: um tipo por arquivo e namespace da feature

Status: vigente (#203, Epic #202). Segue a AD-48 da reorganização de Infrastructure (#196).

Cada feature de `Dante.Application` é uma pasta com namespace
`Dante.Application.<Feature>`. Cada tipo público de topo tem seu próprio arquivo; não
há agregadores `ContratosDe*.cs` nem DTOs/ports declarados no arquivo do AppService.
Feature com ports além do próprio repository, ou com casos de uso internos, é
complexa: contratos públicos vão para `Contratos/`, ports e os records trocados só com
elas para `Portas/`, e validação/casos de uso internos para `Validacao/`/`CasosDeUso/`.
AppServices, interfaces públicas e mappings ficam na raiz. Subpastas **não** criam
namespace, o que um teste arquitetural verifica.

O legado extraído do Worker (`Agentes`, `Anexos`, `Contextos`, `Uso`) mantém sua
organização até migração explícita. Não existe projeto `Dante.Application.DTO`.

Por quê: localizar DTO, port e AppService sem busca global e dar um destino previsível
a mappings (#204), validators (#205) e casos de uso extraídos (#206/#207), sem mudar
namespaces públicos nem os consumidores na Infrastructure e nos hosts.
Guia: [organização da Application](../development/application.md).

## AD-50 — Persistência de cada entidade em `Modulos/<Modulo>`

Status: vigente (#197, Epic #195). Detalha a pasta `Modulos` da AD-48.

O mapping EF e o repository específico de cada entidade persistente ficam juntos em
`Dante.Infrastructure/Modulos/<Modulo>`, onde `<Modulo>` é o último segmento do
namespace da entidade no Domain (`Conhecimentos`, `CapturaDeConhecimento`...), com
namespace igual à pasta. Mappings se chamam `<Entidade>DbMapping` e continuam
`IEntityTypeConfiguration<T>` sobre a base `EntidadeConfiguration<T>`, carregados por
`ApplyConfigurationsFromAssembly`. `Persistence` guarda apenas a infraestrutura
genérica (`Repository<T>`, `UnitOfWork`, `EntidadeConfiguration<T>`). Filtros globais de
consulta continuam no `DanteDbContext` até a #198.

Por quê: localizar tudo que implementa a persistência de um conceito sem busca global,
como em `Infraestrutura/Modulos/<Modulo>` do ComandoFinanceiro, sem alterar o modelo EF
(o EF não depende do nome nem do namespace da configuration).

## AD-51 — Mappings Mapster por feature, compostos por lista explícita

Status: vigente (#204, Epic #202). Complementa a AD-37 e a AD-49.

Cada feature da Application declara seus pares/direções numa classe
`internal static <Entidade>Mapping` na raiz da feature, com
`Registrar(ConfiguracaoMapeamento)`. Projeção reutilizada por outras features fica no
mapping do dono do tipo (`ConhecimentoMapping.ParaProvenienciaDto`).
`MapeamentosDaApplication` permanece só como composição: uma lista explícita de
chamadas, sem expressões nem referência ao Domain, usada por `AddApplication` e
`AddMapeamentos`. Não há descoberta por reflexão nem scan de assembly; continuam
valendo `IMapsterTypeAdapter`, o registro explícito por par/direção e a criação por
constructor do domínio (AD-37).

Por quê: o arquivo central conhecia os detalhes de todas as features e crescia a cada
uma; a lista explícita mantém a composição auditável sem acoplar o ponto central aos
campos de cada contrato.

## AD-52 — Validação de entrada em validators estáticos por feature

Status: vigente (#205, Epic #202). Complementa a AD-49.

Validação de entrada (presença, formato, faixa, paginação, tamanho e combinação
puramente estrutural de campos) fica em classes `internal static <Conceito>Validator`
da feature — raiz no módulo simples, `Validacao/` no complexo — e no helper
`Comum/ValidacaoDeEntrada` para checks repetidos entre features. Validators não têm
estado nem dependências e preservam tipo de exceção, mensagem e `ParamName`. Regra que
depende de repository, estado persistido, identidade ou policy continua no
AppService/caso de uso; invariantes continuam nas entidades do Domain. Sem
FluentValidation.

Por quê: os mesmos checks de limite e escopo se repetiam em vários AppServices e
misturavam forma da entrada com regra de negócio; validators puros são localizáveis e
testáveis sem repository, sem mudar contratos públicos nem a ordem dos erros.


## AD-53 — Despacho local do Brain separado da administração PostgreSQL

Status: vigente (#199, Epic #195). Complementa AD-43/AD-48.

`Brain/ComandosDoBrain` mantém a interface local `--brain` comum aos dois hosts e
encaminha somente migrate, health, backup e restore a `Banco/ComandosDoBanco`.
Importação, auditoria e busca resolvem seus próprios serviços no escopo, sem
resolver `AdministracaoDoBanco`. Esta permanece responsável pela única chamada
produtiva a `Database.MigrateAsync`, exclusivamente em operação administrativa
explícita. Startup normal não migra. Migrations ficam em Data/Migrations no assembly
da Infrastructure, sem projeto Migrator, mudança de IDs ou schema nesta entrega.

Por quê: comandos funcionais do Brain não precisam conhecer nem resolver operações
administrativas do PostgreSQL. Preservar a interface dos hosts evita alterar os
scripts locais enquanto a divisão interna explicita essa responsabilidade.

## AD-54 — Application compõe seus serviços com ativação scoped do Brain opcional

Status: vigente (#208, Epic #202). Complementa AD-35/AD-43 e a composição da #200.

`AddApplication` registra mapping/policy e, por lista explícita em
`Composicao/ServicosDoBrain` (namespace da Application), autorização, AppServices e
casos de uso da conversa. `AddInfrastructure` registra somente adapters, repositories,
DbContext e administração técnica. Os hosts mantêm
`AddApplication().AddInfrastructure(configuration)`. Validators/policies puros
estáticos continuam sem registro DI; objetos transitórios de uma chamada continuam
criados pelos próprios casos de uso.

Como o Brain é opcional e a Application não conhece configuração PostgreSQL, os
serviços que exigem ports são registrados via factories scoped de ActivatorUtilities.
A ausência de adapters permite validar/iniciar os hosts sem banco, mas resolver um
caso de uso dependente de port ausente falha explicitamente. Não há stub que simule
sucesso. TryAdd preserva overrides/fakes e evita duplicações. Testes resolvem toda a
lista com adapters configurados, além de ValidateOnBuild/ValidateScopes, pois a
validação automática não inspeciona dependências dentro de factories.

DbContext composto por DI exige AutorizacaoDoBrain mesmo quando AddApplication foi
omitido: a dependência opcional do constructor nunca vira modo administrativo por
falta de registro. Construção direta de contextos continua reservada a administração
explícita/design-time/fixtures. Nenhum lifetime de serviço/adapter muda.

Por quê: o ownership dos casos de uso pertence ao núcleo; a ativação adiada conserva
o Brain opcional sem acoplar Application à configuração de storage nem aos hosts.
