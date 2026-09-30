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

Código: `Agents/AgentProcessExecutor.cs`, `Agents/AgentExecutableResolver.cs`; testes em
`AgentProcessExecutorTests`.

## AD-03 — Nenhum shell arbitrário

Status: vigente (MVP 1)

O usuário do Telegram escolhe o agente e o prompt; nunca o executável nem argumentos
internos. O prompt é sempre um único argumento posicional depois de `--`.

Por quê: o D.A.N.T.E. é orquestrador de agentes, não shell remoto.

Código: `Agents/ClaudeRunner.cs`, `Agents/CodexRunner.cs`; testes em
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

Código: `Repositories/RepositoryRegistry.cs`; testes em `RepositoryRegistryTests`.

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

Código: `Agents/GeneralWorkspace.cs`, `Agents/AgentProcessExecutor.cs`; testes em
`GeneralWorkspaceTests`.

## AD-10 — Ambiente por repositório; segredos por host binding

Status: vigente (MVP 2, #22)

Cada repositório pode ter variáveis literais públicas (`/repo env set`) ou referências a
variáveis do host (`/repo env bind`). O valor de um binding é lido só na execução e nunca
é gravado no catálogo. Binding ausente no host impede o início do agente. Jobs com
bindings omitem a saída do agente no Telegram. Variáveis com nome de aparência sensível
(`TOKEN`, `SECRET`, `API_KEY`…) só podem ser configuradas por binding.

Por quê: segredos nunca trafegam pelo Telegram nem ficam em arquivo do D.A.N.T.E.

Código: `Repositories/RepositoryRegistry.cs`; testes em `RepositoryRegistryTests` e
`TelegramRepositoryCommandTests`.

## AD-11 — Contexto imutável por job

Status: vigente (MVP 2, #23)

Cada job registra o contexto resolvido no início (`General` ou alias + path). Alterar o
catálogo depois não muda jobs existentes; `/status` mostra o contexto de cada job.

Código: `Jobs/JobExecutionContext.cs`; testes em `JobRegistryTests` e
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

As regras de precedência de agente e contexto propostas na Epic #32 entram aqui quando
forem implementadas.

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

Código: `Settings/AssistantSettingsStore.cs`; testes em `AssistantSettingsStoreTests` e
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
de agente e contexto será centralizada no resolvedor da #37.

Código: `Settings/AssistantSettingsStore.cs`, `Telegram/TelegramPollingService.cs`;
testes em `AssistantSettingsStoreTests` e `TelegramActiveRepositoryTests`.

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

Código: `Agents/InteractiveAgentProcess.cs`, `Agents/InteractiveAgentProcessLauncher.cs`,
`Agents/AgentProcessStartInfo.cs`, `Agents/ProcessTree.cs`; testes em
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
  `permission_suggestions` em `updatedPermissions` (validado contra o Claude Code 2.1.284:
  com `setMode acceptEdits`/`session`, o segundo `Write` do turno não pediu aprovação);
  respostas de `AskUserQuestion` vão em `updatedInput.answers`, chaveadas pelo texto da
  pergunta. Outros `control_request` recebem erro para o Claude não esperar para sempre;
- **steer**: não há (AD-15); `SteerAsync` lança `NotSupportedException` e a sessão usa
  interrupt + nova mensagem;
- **falhas**: linha fora do protocolo ou processo que sai sem `CloseAsync` terminam
  `ReadEventsAsync` com `AgentProtocolException` e matam o processo; `CloseAsync` fecha o
  stdin (`StopAsync`, AD-17) e termina o fluxo normalmente.

Perfis (`AgentPermissionProfile`, padrão `Manual`) mapeiam para `--permission-mode`:
`Manual → manual`, `Auto → auto`, `Plan → plan`. Nenhum perfil de acesso irrestrito existe
no driver; a escolha de perfil pelo usuário e um eventual `full` são da #67. O one-shot
(`ClaudeRunner`, `--permission-mode auto`) continua inalterado.

Por quê: `--session-id` torna o id upstream conhecido no início, como o contrato da AD-16
exige; os formatos de resposta foram confirmados contra a CLI instalada em vez de
inferidos.

Código: `Sessions/ClaudeSessionDriver.cs`, `Sessions/AgentPermissionProfile.cs`,
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
  interrupt. `turn/started` → `TurnStartedEvent`; `turn/completed` → `TurnCompletedEvent`
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
| `Auto` | `never` | `workspace-write` | — |
| `Plan` | `on-request` | `read-only` | `collaborationMode` `plan` com o modelo da thread |

Nenhum perfil chega a `danger-full-access`; a escolha pelo usuário e um eventual `full`
são da #67. Input humano do Codex continua **experimental** e, na 0.157.1, só aparece no
perfil `Plan` (AD-15). `app-server` não tem `--ignore-user-config`: em General Mode o
isolamento vem do ambiente filtrado, do diretório neutro e do `sandbox` da thread. O
one-shot (`CodexRunner`, `codex exec`) continua inalterado.

Por quê: a thread efêmera acompanha a vida da sessão em memória (Epic #60), e cancelar os
requests pendentes no interrupt evita um turno preso esperando resposta que nunca virá.

Código: `Sessions/CodexSessionDriver.cs`, `Sessions/AgentPermissionProfile.cs`,
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
  registry (hoje no `TelegramPollingService`, amanhã no resolvedor da #37): o registry recebe o contexto pronto;
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

Status: vigente (Epic #60, #66)

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

Por quê: a conclusão de ações do agente não prova que o Telegram recebeu sua resposta. Separar
os estados permite recuperação sem repetir efeitos no repositório ou no GitHub.

Código: `Telegram/TelegramDeliveryService.cs`, `Telegram/TelegramPollingService.cs`,
`Telegram/TelegramBotApi.cs`; testes em `TelegramDeliveryServiceTests` e `TelegramBotApiTests`.
