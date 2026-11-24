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

Status: vigente sem `Orquestracao:Habilitada`; com a orquestração habilitada, substituída pela AD-67 (#300)

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

Status: vigente (Epic #60, #62); lançamento/garantia de descendentes substituídos pela AD-63 em modo estrito.

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

## AD-55 — Planilhas como capacidade genérica exposta aos agentes por MCP, com Google Sheets como primeiro provedor

Status: vigente (#224). Complementa AD-35/AD-41/AD-54.

A Application tem a feature `Planilhas`, sem domínio: `PlanilhasAppService` sobre as portas
`IPlanilhaService` (metadados, leitura com exibido/bruto/fórmula/mesclagens, valores exibidos
em lote, escrita de valores, acréscimo de linha e reconhecimento de URL/ID), `IConexaoDePlanilha`
(iniciar, consultar e revogar a conta), `ICadastroDePlanilhas` e `IAuditoriaDePlanilhas`. A
Application não referencia SDK, API, OAuth ou URL do Google, nem conceitos de nenhum domínio
(treino, finanças...); um teste recusa esse vocabulário no código de produção da integração.
Interpretar a intenção e a estrutura é do agente; a planilha de treino é só o cenário E2E.

A Infrastructure implementa Google Sheets por HTTP direto (sem SDK, como o Telegram):
`GoogleOAuthService` (app local, callback loopback em 127.0.0.1, PKCE S256, `state` em tempo
constante, access token só em memória, renovação automática e revogação), `GoogleCredentialStore`
(refresh token e cliente OAuth cifrados com AES-256-GCM, chave em arquivo separado, 0600/0700)
e `GoogleSheetsAdapter` (401 renova uma vez; 429/5xx repetidos com espera; demais erros viram
`FalhaDePlanilhaException` com mensagem segura). Cadastro e auditoria são arquivos locais.

Os agentes acessam a capacidade por um servidor MCP stdio padrão (`dante_planilhas`), que é o
próprio executável do Worker em modo `--mcp-planilhas`, iniciado pela CLI da sessão: Claude por
`--mcp-config` (leituras em `--allowedTools`), Codex por `-c mcp_servers.*` (leituras anotadas
`readOnlyHint`). Escritas seguem o modo da sessão pelos caminhos de aprovação existentes:
`can_use_tool` no Claude e `mcpServer/elicitation/request` com `codex_approval_kind =
mcp_tool_call` no Codex, mapeado para `ApprovalRequestedEvent`; `plan` recusa escrita. O spike
desta Issue confirmou o comportamento nas CLIs 2.1.287 e 0.159.3 (leitura sem aprovação, escrita
com elicitation/can_use_tool, aprovação automática no `auto_review`). O servidor só é oferecido a
sessões iniciadas com conta conectada e não recebe segredos pela linha de comando.

Escrita é sempre em coordenadas exatas, tudo ou nada, com valor anterior e auditoria por célula.
O núcleo recusa: alvo por referência com mais de um candidato do mesmo nível (devolvendo os
candidatos), valor esperado divergente, fórmula sem permissão explícita (e mais de 5 por vez),
célula interna de mesclagem, limpeza de mais de 20 células preenchidas e mais de 200 células.
Operações estruturais destrutivas não existem no MVP.

Alternativas descartadas: ferramentas dinâmicas experimentais do app-server e servidor MCP
"sdk" do Claude (protocolos internos, um por CLI); comandos Telegram determinísticos para ler e
escrever (tirariam a interpretação do agente); SDK do Google (dependência sem ganho para cinco
endpoints REST).

Por quê: um contrato genérico de planilha com um provedor substituível atende qualquer domínio
sem acoplar o núcleo ao Google, e o MCP padrão dá às duas CLIs as mesmas ferramentas, com a
aprovação de escrita no fluxo que o D.A.N.T.E. já controla.

Código: `src/Dante.Application/Planilhas`, `src/Dante.Infrastructure/{Google,Planilhas}`,
`src/Dante.Worker/Planilhas`, `src/Dante.Worker/Telegram/TelegramPlanilhas.cs`; guia em
[planilhas](../development/planilhas.md).

Ampliação autorizada da #224: XLSX existente no Drive mantém o mesmo ID e formato, sem
conversão. `GooglePlanilhasAdapter` seleciona por MIME entre Sheets e Drive;
`GoogleDriveXlsxAdapter` baixa o binário e `DocumentoXlsx` edita apenas valores OOXML em
memória, preservando as partes não alteradas. Sem dependência nova ou motor de cálculo:
leitura bruta e cache de fórmulas são declarados ao agente. A Application continua sem
conceitos do provider; propaga a revisão esperada e observações genéricas dos resultados.

O caminho Drive usa v2, cuja representação Files ainda fornece ETag; v3 retirou esse campo.
Versão é verificada após download e antes do upload; If-Match protege o envio da nova
revisão do mesmo arquivo. Upload com resultado incerto não é repetido automaticamente.
OAuth solicita Drive só com `Google__PermitirXlsxNoDrive=true`; nova autorização é
necessária. O escopo Drive permite arquivos existentes por ID, enquanto drive.file
precisaria de Picker/seleção do arquivo pelo app. Não há descoberta ou ferramenta genérica
de Drive. Permissões concedidas continuam cifradas e disponíveis ao MCP após reinício.

## AD-56 — Brain dos agentes por MCP e IPC de sessão (#226)

Status: vigente.

`dante_brain` é adapter MCP stdio que encaminha chamadas por named pipe local ao Worker.
Worker conserva conexão e resolve identidade/autorização/escopo por mensagem Telegram;
operações chamam AppServices existentes, sem acesso de agente a persistência ou segredo.
Endpoint aleatório same-user é exclusivo da sessão e não sobrevive a desconexão/fim
ou reinício. Mudança de escopo ou conversa recusa operações até nova sessão.

Providers independentes de tool servers são compostos por `CompositorDeFerramentas`;
planilhas permanece um provider, sem assumir conhecimento do Brain. Sem Brain disponível,
o agente continua utilizável. Aprovação MCP e confirmação de negócio são distintas:
consolidação/descarte/relação preparam proposta consumível pelo usuário no Telegram,
revalidando autorização e revisão. Título/tags opcionais são extensões do histórico JSON
de candidato e conservados na promoção, sem migration ou fusão de itens.

Por quê: permite orquestração de pedidos complexos pelas CLIs sem expor credenciais
observáveis na configuração MCP nem introduzir HTTP/WebApi como dependência do fluxo.
O limite de confiança local é a conta do sistema; não protege contra processo malicioso
do mesmo usuário já capaz de inspecionar as CLIs. Guia e testes: [Brain MCP](../development/brain-mcp.md).

## AD-57 — Identidade de ferramenta separada da apresentação de conversa

Status: vigente (#230). Complementa AD-55/AD-56.

`ToolStartedEvent` conserva descrição técnica e carrega servidor/nome estruturados
e apresentação opcional. Os drivers resolvem identidade em catálogo central de
apresentação, incluindo o formato MCP do Claude; Telegram consome apresentação
ou fallback genérico e reutiliza o texto na falha. Novas capacidades não exigem
condicionais no transporte. Comandos de terminal mantêm tratamento próprio e
logs debug conservam identidade sem argumentos.

O contrato de conversa compõe instruções Brain e planilhas e orientações de linguagem
no harness de ambos os agentes. Descoberta de fontes é tarefa do agente, usando
aliases/descrições e leitura progressiva, sem regras específicas de domínio.
Indisponibilidade de uma capacidade não impede outra. Texto literal não é reescrito.

Por quê: status técnico direto expunha detalhes de implementação no Telegram;
uma representação distinta mantém diagnóstico e oferece progresso natural sem
parsing frágil de descrições, enquanto o contrato torna a seleção semântica explícita.

## AD-58 — Repositórios vinculados por cadastro explícito, congelados com o principal

Status: vigente (#232). Complementa AD-08/AD-14/AD-27, AD-09, AD-10, AD-11 e AD-20, sem substituí-las.

Um repositório cadastrado pode ter **vinculados**: outros aliases do catálogo cujos checkouts
o agente lê e altera na mesma tarefa, ao lado do principal. O caso de origem é frontend e
backend em repositórios separados.

- **cadastro**: `/repo link @principal @vinculado`, `/repo unlink` e `/repo links`. O vínculo
  vive em `Links` da entrada do principal em `~/.dante/repositories.json`, com a mesma escrita
  atômica do catálogo; catálogos sem o campo continuam válidos. Só entre aliases cadastrados,
  nunca para o próprio repositório nem entre checkouts aninhados; repetido ou para si mesmo no
  arquivo impede a carga. Nada é inferido de nome, caminho ou texto do prompt;
- **direção e alcance**: direcional e direto. `@front → @back` dá ao front acesso ao back, não o
  contrário, e os vínculos do back não são seguidos. Vários vinculados diretos são permitidos;
- **resolução**: o principal continua vindo da precedência da AD-27 (`@alias` explícito →
  repositório ativo → General). O `AgentContextResolver` acrescenta os vinculados do principal
  pela porta `ICatalogoDeRepositorios.ResolverVinculos`. Vinculado fora do catálogo, diretório
  inexistente ou checkouts sobrepostos recusam a execução (`ContextResolutionFailure.InvalidLink`)
  com instrução de correção: nunca há execução com menos repositórios, troca de principal ou queda
  para General Mode. `/repo remove` apaga, no mesmo save, os vínculos que apontavam para o alias;
- **caminho físico**: sobreposição é verificada no `link` e em cada resolução sobre o caminho real
  (`CaminhoReal`: symlinks e junctions resolvidos em todos os componentes) e sem diferenciar
  maiúsculas, como no `/mnt/c` do WSL; na dúvida, recusa. O `/repo add` já exigia a raiz devolvida
  pelo `git rev-parse --show-toplevel`, que resolve links; a resolução cobre o diretório trocado por
  link depois do cadastro e o catálogo editado à mão. O vinculado sai da resolução pelo caminho
  físico, então reapontar um link depois não redireciona uma execução já iniciada;
- **snapshot**: `JobExecutionContext.RepositoriosVinculados` (alias + caminho) é congelado com o
  principal (AD-11) e fixado na sessão (AD-20). `link`/`unlink`/`remove` valem para jobs e sessões
  novos; os existentes mantêm o que receberam, inclusive após clear, troca de modo, interrupt ou
  timeout: clear e troca de modo reenviam o mesmo root congelado, sem nova resolução, mesmo que o
  diretório tenha sido removido ou recriado. **Revogar o acesso de uma sessão ou job em andamento
  exige encerrá-lo** (`/session close`, `/cancel`); a resposta de `unlink` e `remove` lista cada
  sessão e job do usuário que ainda acessa o vinculado, com esse comando. `Label` continua sendo só
  o principal; `Descricao` (`@front + @back`) aparece em `/status` e nas confirmações;
- **ambiente**: só o do principal (AD-10). Variáveis e bindings do vinculado nunca são resolvidos
  nem herdados. Não há concessão de ambiente do vinculado nesta decisão;
- **acesso das CLIs**: somente os checkouts resolvidos, nunca o diretório pai. Claude recebe um
  `--add-dir` por vinculado (one-shot e stream-json); Codex `exec` recebe `--add-dir`, e o
  `app-server` recebe `config.sandbox_workspace_write.writable_roots` no `thread/start` (início e
  clear) e os mesmos `writableRoots` no `sandboxPolicy` da troca de modo. O driver exige que a
  resposta de `thread/start` e a notificação de settings confirmem exatamente esses roots (validado
  na codex-cli 0.159.3); sem confirmação, a sessão não começa e o clear não acontece. `plan`
  continua `read-only`. General Mode recusa diretórios adicionais nos runners e drivers (AD-09);
- **Git e PRs**: o D.A.N.T.E. continua sem executar `git`/`gh`. Com vinculados, o agente recebe o
  `ContratoMultirrepositorio` (prompt de sistema/`developerInstructions` na sessão; antes do pedido
  no one-shot): checkouts autorizados, ambiente só do principal, consistência de contratos
  compartilhados e validação por checkout, branch e PR próprios por repositório alterado com
  referência cruzada, nenhum PR sem alteração, nunca merge e relato por repositório em falha
  parcial. Sem vinculados, nada muda no prompt. O contrato orienta, não garante: para que um
  resultado parcial seja observável sem depender do relato do agente, o D.A.N.T.E. lê o estado de
  cada checkout pela porta `ILeitorDeCheckouts` (git somente leitura, sem shell, com
  `core.fsmonitor=false` e prazo por consulta): branch, arquivos alterados sem commit e commits não
  enviados ou branch sem upstream. O relatório sai no fim de cada job e em `/status` para cada
  sessão viva com vinculados. A criação real de PRs não é rastreada pelo D.A.N.T.E.;
- **concorrência**: não há bloqueio novo sobre checkouts; vale o mesmo que para o principal hoje.
  `/send` continua aceitando só caminhos sob o diretório de trabalho (AD-29).

Por quê: features que atravessam frontend e backend exigiam pedidos separados, e o agente de um
lado não enxergava o contrato real do outro. O vínculo cadastrado mantém a escolha explícita das
AD-08/AD-14, e o snapshot fail-closed impede que um vínculo inválido mude silenciosamente onde o
agente escreve. Orquestrar branches e PRs dentro do D.A.N.T.E. contrariaria o limite de não ser
shell remoto; o contrato entregue ao agente mantém a responsabilidade onde o Git já é operado.

Código: `Dante.Infrastructure/Contextos/RepositoryRegistry.cs`,
`Dante.Application/Contextos/AgentContextResolver.cs`, `JobExecutionContext.cs`,
`ContratoMultirrepositorio.cs`, `ILeitorDeCheckouts.cs`, `Dante.Infrastructure/Contextos/CaminhoReal.cs`,
`LeitorDeCheckoutsGit.cs`, `Dante.Infrastructure/Agentes/DiretoriosVinculados.cs`, runners,
`Sessions/ClaudeSessionDriver.cs`, `Sessions/CodexSessionDriver.cs`, `Sessions/SessionRegistry.cs`,
`Telegram/TelegramPollingService.cs`; testes em `RepositoryRegistryTests`,
`AgentContextResolverTests`, `ClaudeRunnerTests`, `CodexRunnerTests`, `ClaudeSessionDriverTests`,
`CodexSessionDriverTests`, `SessionRegistryTests`, `LeitorDeCheckoutsGitTests`, `TelegramJobCommandTests` e
`TelegramRepositoryCommandTests`.

## AD-59 — Classificação semântica do conhecimento separada da autoria e do estado epistêmico

Status: **aprovada na revisão humana (#248)**; vigente a partir do merge do PR #263. Nenhum
código, schema ou dado muda por esta decisão; a implementação vem nas Issues derivadas
listadas abaixo, depois da #264. Complementa a ADR do Brain (#134) e a AD-56.

### Contexto

Três perguntas distintas sobre um conhecimento estão hoje respondidas por dois campos:

| Pergunta | Hoje |
| --- | --- |
| **O que é** (Fato, Decisão, Incidente, Solução, Aprendizado…) | `TipoDeConhecimento` |
| **Quem afirmou** (usuário literal, agente, fonte selecionada) | `NaturezaDoConteudo`, **só no candidato** |
| **Quanto vale** (inferido × confirmado) | `StatusDoConhecimento` |

E o tipo é sobrescrito para carregar autoria:

- `CandidatoDeConhecimento` (construtor): `ConclusaoDoAgente` força `Tipo = Inferencia`. O tipo
  sugerido é descartado já na captura e não fica em nenhum ato do histórico;
- `CandidatoDeConhecimento.Promover`: confirma somente se `Tipo != Inferencia`;
- `Conhecimento.Confirmar`: recusa `Tipo == Inferencia`;
- `OperacoesMcpDoBrain` (`brain_corrigir_candidato`, `brain_atualizar_conhecimento`) repete a
  regra: conclusão do agente sempre vira `Inferencia`;
- `Conhecimento` não persiste `Natureza`. Depois da promoção, `Tipo = Inferencia` é o único
  sinal de que o conteúdo veio do agente;
- a promoção com vínculo incidente→solução exige `Tipo = Aprendizado`
  (`CapturaDeConhecimentoAppService.ConfirmarAsync`), o que é inalcançável para conclusões
  do agente.

Além disso, a autoria declarada não é verificada contra quem escreveu a fonte:
`OperacoesMcpDoBrain.ValidarOrigem` aceita `DitoPeloUsuario` quando o conteúdo está na mensagem
respondida, sem conferir o autor dela; a captura natural (`TelegramBrain`, `CapturaDaConversa`)
classifica qualquer mensagem respondida como `FonteSelecionada`; e
`DocumentoFonteAppService.GerarCandidatoAsync` classifica todo trecho de documento como
`FonteSelecionada`. Nenhum desses caminhos confirma sozinho (a confirmação exige "confirmar" no
Telegram), mas um texto do bot ou do agente pode ser consolidado com autoria do usuário. A
correção do comportamento atual é a #264, independente desta ADR.

Consequências observadas na validação E2E (#147): os candidatos de teste deixaram de ser
Incidente/Solução; filtros por tipo não encontram um incidente diagnosticado pelo agente; o
vínculo `ResolvidoPor` entre eles não é representável (#259); decisões e inferências ficam
distinguíveis só por convenção de tipo.

### Opções

**A — manter o modelo atual.** Nenhum custo. O tipo semântico continua perdido para
conclusões do agente; `Inferencia` continua sendo ao mesmo tempo tipo e autoria; #259
permanece sem solução sem exceções ad hoc.

**B — eixos ortogonais (escolhida).**

- `Tipo` volta a ser **somente semântico**. Conclusão do agente conserva o tipo sugerido
  (Incidente, Solução, Decisão…);
- `Natureza` (`DitoPeloUsuario`, `ConclusaoDoAgente`, `FonteSelecionada`, e `Desconhecida`
  somente para legado) passa a ser persistida também no `Conhecimento`, por revisão (no
  histórico), como natureza de origem do item e como natureza da revisão atual;
- `Status` continua sendo o eixo epistêmico. Conclusão do agente entra e permanece
  `Inferido`;
- `Inferencia` deixa de ser gravado em itens novos e fica como valor legado, mantido no
  enum para leitura e filtro dos registros existentes.

Custo: migration (coluna de natureza em conhecimentos e no histórico JSON), ajuste de
promoção, confirmação, correção, MCP, Telegram, busca e Context Builder.

**C — intermediária.** Manter `Tipo = Inferencia` e guardar o tipo sugerido em
`DadosEstruturados` (ex.: `tipoSugerido`). Sem migration de schema, mas o tipo real fica fora
dos filtros e índices, `Inferencia` continua sobrecarregado e cada consumidor precisa conhecer
o campo auxiliar. Resolve parcialmente #259.

### Decisão

Adotar **B**, com as decisões da revisão humana:

| # | Decisão | Onde se aplica |
| --- | --- | --- |
| D1 | Mensagem respondida sem autor identificável nunca é declaração do usuário | P1, I3 |
| D2 | Conhecimento produzido pelo agente permanece identificado como conclusão do agente, independentemente de confirmação humana | P4, I1, I2, I10 |
| D3 | Conhecimento antigo sem autoria registrada permanece com autoria desconhecida; não se inventa proveniência retroativa | Dados existentes |
| D4 | `IConhecimentoAppService.ConfirmarAsync` obedece às mesmas invariantes; não existe caminho alternativo que as contorne | I11 |
| D5 | #264 é implementada antes das funcionalidades que dependem das novas regras de autoria | Ordem de implementação |
| D6 | "documente isso" respondendo a uma mensagem do agente registra o conteúdo, mas não o transforma em fato confirmado | P2, I9 |
| D7 | O tipo do conhecimento é preservado independentemente da autoria e do estado epistêmico | I12 |

### Princípios de autoria

- **P1:** uma mensagem do agente ou do bot **nunca** é declaração literal do usuário. Só conta
  como `DitoPeloUsuario` o texto escrito pelo próprio usuário autenticado, na mensagem atual ou
  numa mensagem respondida cujo autor é comprovadamente esse usuário. Mensagem respondida sem
  autor identificável não é do usuário (D1).
- **P2:** a autoria da fonte é preservada independentemente de quem solicitou a captura. Pedir
  para registrar não torna o solicitante autor do conteúdo: o usuário que responde a uma
  mensagem do agente com "documente isso" autoriza o **registro** de uma conclusão do agente,
  não a transforma em declaração sua nem em fato confirmado (D6).
- **P3:** evidências produzidas pelo agente continuam sendo conclusões do agente, mesmo quando
  armazenadas como documento, fonte, anexo ou trecho importado.
- **P4:** a autoria de origem é **permanente**. Confirmação humana, consolidação, promoção e
  correção não alteram retroativamente quem produziu a informação. Um conhecimento produzido
  pelo agente continua identificado como conclusão do agente em todo o seu ciclo de vida (D2).
  Se o usuário quiser afirmar o mesmo conteúdo como fato, ele o declara com as próprias
  palavras, gerando **outro conhecimento** de autoria do usuário, que pode referenciar o
  original (`DerivadoDe`) sem alterá-lo.

### Invariantes (verificáveis por teste em qualquer implementação)

1. **I1:** conhecimento cuja natureza de origem **ou** natureza da revisão atual seja
   `ConclusaoDoAgente` nunca tem `Status = Confirmado`, por nenhum caminho: promoção,
   confirmação, consolidação, atualização ou correção (D2).
2. **I2:** `Status = Confirmado` exige, cumulativamente: natureza de origem e natureza da
   revisão atual iguais a `DitoPeloUsuario` ou `FonteSelecionada` de autoria do usuário
   (I3, I9); confirmação humana explícita no Telegram. `Desconhecida` não satisfaz I2 (D3).
3. **I3:** `DitoPeloUsuario` exige origem literal **escrita pelo usuário autenticado**: o texto
   está na mensagem atual do usuário ou numa mensagem respondida cujo autor (`From.Id`) é esse
   mesmo usuário. Mensagem respondida do bot, do agente, de outro usuário ou **sem autor
   identificável** nunca é origem literal (P1, D1). Vale para MCP (captura, correção,
   atualização) e para a captura natural.
4. **I4:** correção ou atualização feita pelo agente grava `Natureza = ConclusaoDoAgente` na
   nova revisão e devolve o item a `Inferido`; nunca herda confirmação anterior.
5. **I5:** a natureza de um candidato não muda por correção (regra atual); a natureza de origem
   de um conhecimento e a natureza registrada em cada revisão são imutáveis.
6. **I6:** decisão referenciada pelo snapshot exige `Tipo = Decisao` e `Status = Confirmado`;
   por I1, nunca é conclusão do agente.
7. **I7:** o Context Builder, a busca e as exportações rotulam pela natureza e pelo `Status`,
   não pelo tipo: todo item `Inferido`, `ConclusaoDoAgente` ou `Desconhecida` aparece como não
   confirmado ou de autoria não comprovada, conforme o caso.
8. **I8:** a impressão de deduplicação do candidato continua incluindo a natureza.
9. **I9:** `FonteSelecionada` registra **quem escreveu a fonte** (usuário; documento importado
   pelo usuário; documento, evidência ou anexo produzido pelo agente; mensagem do bot),
   independentemente de quem pediu a captura (P2). Fonte de autoria do agente ou do bot é
   registrada como `ConclusaoDoAgente` (P3, D6).
10. **I10:** confirmação, consolidação de duplicatas e promoção não mudam a natureza de origem
    nem a de nenhuma revisão existente (P4). Consolidar uma duplicata `ConclusaoDoAgente` em um
    destino confirmado incorpora a proveniência sem promover a duplicata nem rebaixar o destino.
11. **I11:** **todo** caminho que grava `Status = Confirmado` passa pela mesma regra de domínio
    (`Conhecimento.Confirmar` e a promoção de candidato), que aplica I1/I2. Isso inclui
    `IConhecimentoAppService.ConfirmarAsync` (hoje sem chamador), WebApi, MCP, Telegram e
    qualquer caminho futuro. A regra vive no Domain, não nos adapters, para que nenhum caminho
    alternativo a contorne (D4).
12. **I12:** o tipo é escolhido pela semântica do conteúdo e **nunca é reescrito** por autoria
    ou estado epistêmico: captura, correção, atualização, promoção e confirmação conservam o
    tipo informado (D7). `Inferencia` deixa de ser atribuído automaticamente e permanece no
    enum apenas para leitura e filtro de registros legados.

### Impacto por componente

| Componente | Muda? | O quê |
| --- | --- | --- |
| Domain — `CandidatoDeConhecimento` | sim | não força `Inferencia` (I12); `Promover` decide confirmação por natureza (I1/I2) e repassa a natureza |
| Domain — `Conhecimento` | sim | natureza de origem e natureza por revisão, imutáveis (I5/I10); `Confirmar` aplica I1/I2 por natureza, não por tipo; ponto único de confirmação (I11) |
| Application — `ConhecimentoAppService.ConfirmarAsync` | sim | sujeito a I1/I2 pelo Domain; sem atalho próprio (I11, D4) |
| Persistência / migrations | sim | natureza de origem e da revisão atual em conhecimentos e no histórico JSON; valor `Desconhecida` para legado |
| Application — captura/confirmação | sim | vínculo incidente→solução deixa de exigir `Aprendizado` para conclusões do agente (#259) |
| Worker — MCP | sim | correção/atualização deixam de reescrever `Tipo` (I12); saídas exibem natureza |
| Worker — `OperacoesMcpDoBrain.ValidarOrigem` | sim | verifica o autor da mensagem respondida; sem autor identificável, recusa (I3, D1; #264) |
| Telegram/Application — captura natural (`TelegramBrain`, `CapturaDaConversa`) | sim | trecho respondido registra o autor da fonte; mensagem do bot ou sem autor gera conclusão do agente (I3, I9, D6; #264) |
| Telegram — fluxo natural (`AlteracoesDaConversa`) | sim | textos de consolidação usam natureza/status em vez de `Tipo == Inferencia` |
| Application — `DocumentoFonte` | sim | autoria do documento registrada na importação; trecho de documento do agente gera conclusão do agente (I9; #264) |
| Evidências do agente (#257) | sim | evidência registrada pelo agente nasce com autoria do agente; candidato gerado dela é `ConclusaoDoAgente` (P3, I9) |
| Busca | sim | filtro opcional por natureza; `tipo = Inferencia` continua retornando os itens legados |
| Context Builder | sim | rótulo por status/natureza (I7) |
| Snapshot | não | validação de decisões já exige `Decisao` + `Confirmado` (I6) |
| Relações | não | apenas consomem o novo caminho de promoção (#259) e `DerivadoDe` existente (P4) |

### Dados existentes (D3)

- A natureza de origem de um conhecimento existente vem **somente** de autoria já registrada:
  a natureza do candidato que o originou (`IdConhecimento`), quando ele existir. Sem candidato
  (criação direta, importação), a natureza é `Desconhecida`.
- **Nada é deduzido** de tipo, texto ou cadeias de origem. Em particular, `Tipo = Inferencia`
  não prova autoria do agente: a captura natural aceita o tipo explícito (`inferencia: …`) com
  `DitoPeloUsuario` (`CapturaDaConversa`). Item legado `Inferencia` sem candidato fica
  `Desconhecida`.
- O tipo original das conclusões do agente já gravadas como `Inferencia` **não é
  recuperável** (descartado na captura) e não é reconstruído. Reclassificação só por correção
  explícita, registrada como revisão nova (I4, I12).
- Status existentes não são alterados retroativamente. Novas confirmações seguem I2, e
  `Desconhecida` não satisfaz I2.
- Nenhuma revisão histórica é reescrita: revisões antigas sem o campo são lidas como
  "natureza não registrada".

### Ordem de implementação (D5)

1. **#264**: verificação do autor da mensagem respondida e da autoria de documentos (I3, I9 no
   modelo atual). Precede tudo que depende das novas regras de autoria.
2. **Natureza persistida no conhecimento**: Domain, migration, valor `Desconhecida` e
   preenchimento só a partir de candidatos; I1, I2, I5, I10, I11 (inclui
   `ConhecimentoAppService.ConfirmarAsync`). Depende desta ADR e da #264.
3. **Tipo preservado independentemente da autoria**: captura, correção, atualização, MCP e
   Telegram; I4, I8, I12. Depende de 2.
4. **Busca e Context Builder por natureza**: filtro e rótulo; I7. Depende de 2.
5. **#257** (evidências do agente) depende de #264; **#259** (vínculo incidente→solução)
   depende de 3.

As Issues 2–4 são criadas após o merge desta ADR.

### Por quê

Separar "o que é" de "quem afirmou" e de "quanto vale" mantém a regra de segurança central
(conclusão do agente não vira fato, e nenhuma confirmação reescreve quem afirmou) e para de
destruir informação semântica útil para busca, relações e retomada de contexto. Concentrar a
regra de confirmação no Domain impede que um adapter ou API nova a contorne. A opção C adia o
problema para cada consumidor; a opção A mantém o bloqueio observado na #147.

## AD-60 — Evidências do agente como DocumentoFonte com metadados de registro (#257)

Status: vigente.

Agentes registram evidências de testes e validações pelo MCP (`brain_registrar_evidencia`)
como `DocumentoFonte` Markdown, nunca como candidato ou conhecimento. A autoria é sempre
`Agente` (AD-59 P2/P3/I9) e não é argumento da ferramenta; o escopo é o do acesso da sessão.
Cada registro recebe origem `evidencia:<guid>` gerada pelo servidor, de modo que o índice
único (espaço, projeto, origem) e a reimportação por origem dos fluxos Telegram/CLI
permanecem inalterados. Título, tags, justificativa e proveniência de quem registrou
são colunas opcionais de `documentos_fonte`, fora do conteúdo e do hash; fontes
anteriores ficam com valores neutros, sem metadados ou autoria inventados.

Conteúdo repetido não é deduplicado: cada chamada cria uma fonte própria, preservando
proveniência e metadados de todas as ocorrências. A duplicidade é apenas informativa
(mesmo hash, somente fontes visíveis ao agente), apoiada por índice não único
(espaço, projeto, hash). O filtro de tags passa a valer para fontes; tipo, status e
natureza continuam exclusivos de conhecimento.

Por quê: o snapshot resumido e substituível perdia resultados detalhados (#147), e
metadados embutidos na origem ou no Markdown misturariam registro com evidência e
alterariam o hash. Edição/remoção pelo MCP e extração automática de candidatos ficam
fora. Guia: [Brain MCP](../development/brain-mcp.md#evidências-de-teste-como-fonte-bruta-257) e [fontes](../development/fontes.md).

## AD-61 — Intenção de vínculo entre candidatos, efetivada na consolidação (#259)

Status: vigente. Condição de efetivação aprovada formalmente pelo mantenedor em 04/11/2026
(revisão da #259): a relação nasce com os dois candidatos consolidados pela confirmação do
usuário no Telegram, sem exigir `Status = Confirmado`.

A intenção de `ResolvidoPor` entre dois candidatos pendentes é a entidade
`VinculoEntreCandidatos`, separada de `RelacaoDeConhecimento`, que continua existindo só
entre conhecimentos. O agente a registra por `brain_vincular_candidatos`; nenhuma relação é
criada nesse momento. Quando cada candidato é promovido pela confirmação do usuário no
Telegram, a mesma transação registra o lado consolidado; com os dois lados consolidados,
ativos, no escopo e com tipos Incidente → Solucao, cria a relação uma única vez
(`ObterEquivalenteAsync` e índice único) e marca a intenção como aplicada. Caso contrário,
a intenção é cancelada sem falhar a confirmação. Descartar ou rejeitar um lado cancela a
intenção na mesma transação. Cada consolidação grava a intenção, de modo que confirmações
concorrentes conflitam pelo token de concorrência em vez de deixá-la pendente para sempre.

A efetivação exige consolidação pelo usuário, não `Status = Confirmado`. A confirmação
operacional no Telegram autoriza o registro e a relação, mas não é confirmação epistêmica:
conclusões originadas pelo agente continuam `Inferido`, e a AD-59 permanece integral. Pela AD-59 (I1),
conclusões do agente nunca são confirmadas, e exigir o status impediria vincular incidente
e solução diagnosticados pelo agente (caso da #147). A relação não altera tipo, natureza
nem status de nenhum lado, como as relações já criadas por `brain_criar_relacao` entre
conhecimentos `Inferido`.

Alternativa rejeitada: expor `IdIncidente`/`IdSolucao` no MCP. Esses campos referenciam
conhecimentos já consolidados a partir de um terceiro candidato `Aprendizado` e criam
também `ProduziuAprendizado`; reaproveitá-los para candidatos mudaria a semântica
existente, que permanece inalterada. Guardar a intenção no histórico JSON do candidato
evitaria a tabela, mas tornaria frágeis a busca reversa e a concorrência.
Guia: [Brain MCP](../development/brain-mcp.md#vínculo-incidente--solução-entre-candidatos-259).

## AD-62 — Corte de relevância da busca híbrida, fail-closed e calibrado por modelo (#280)

Status: vigente (#280). Complementa a busca do Brain (#154), a busca híbrida medida na #258 e o
RRF existente, que continua sendo a ordem entre os aprovados.

A parte semântica da busca só é usada com uma similaridade cosseno mínima calibrada para o modelo
(`DANTE_BRAIN_EMBEDDING_MIN_SIMILARITY`, faixa aberta 0–1), vinculada à identidade exata desse modelo
(`DANTE_BRAIN_EMBEDDING_MIN_SIMILARITY_MODEL = provedor|nome|versão|dimensão`, comparada campo a
campo). Sem ela, ou com vínculo a outro modelo, a consulta não gera embedding e a busca é lexical. O corte é aplicado no SQL antes do RRF e da paginação. Cada lista é ranqueada só
entre os aprovados, e a busca pode retornar vazio. A correspondência lexical que casa com todos os
termos da mensagem (`plainto_tsquery`, sem operadores) nunca é cortada. Com o híbrido ativo, a correspondência só parcial dos termos em OR derivados
da mensagem (`TextoIntegral`, enviado pelo Context Builder) exige apoio semântico. O canal agente, a
conversa natural e o modo lexical não mudam. A expansão por relações parte só dos aprovados.

Por quê:

- o score RRF depende só da posição e não informa relevância;
- `semantico > 0` admitia praticamente todos os itens;
- o lexical em OR montava pacote por coincidência de uma palavra (#258, #280);
- o limiar depende do modelo, por isso não há padrão embutido e ele fica preso à identidade
  calibrada: um modelo não calibrado, ou trocado sem recalibrar, nunca injeta resultados fracos;
- `websearch_to_tsquery` interpretaria `or`, aspas e `-` da mensagem como operadores, e uma
  correspondência parcial passaria por integral.

Alternativas medidas e rejeitadas:

- margem relativa ao melhor score (R2) e z-score por consulta (R3): leave-one-out pior;
- embedding da mensagem natural no canal automático (variante A): reprovou no critério de abstenção
  com limiar único;
- manter o lexical parcial intacto: nenhum limiar zerava os pacotes sem resposta.

O limiar `0.6` fixo das fontes brutas não foi alterado, e a divergência fica para avaliação futura.
Com o `bge-m3`, a validação final reprovou no critério de paráfrases (50% contra 80%). Por isso a
instância do homelab permanece lexical. Relatório:
[corte de relevância](../brain/avaliacao-busca-hibrida/corte-280/README.md).


## AD-63 — Contenção nativa por sessão interativa, estrita por padrão

Status: implementação isolada para revisão independente, autorizada pelo mantenedor.
Substitui o lançamento e a garantia baseada em polling da AD-17. Não habilitada no
Worker em execução. Executores one-shot não mudam.

O contraexemplo reproduzido na PR #283 criou filho após EOF e encerrou o pai antes de
outro snapshot: Completion confirmou sucesso com filho vivo. Sincronizar a última
observação ou diminuir o intervalo não elimina essa janela. A contenção precisa
preceder a execução do agente e sobreviver à saída do principal.

Decisão vigente: infraestrutura inicia cada sessão estrita dentro de scope systemd/cgroup
v2 no Linux. Windows foi retirado desta entrega por decisão do mantenedor. Completion exige raiz
encerrada e confirmação nativa de grupo vazio. Falhas/timeout são explícitos.
O núcleo não recebe APIs ou tipos de plataforma.

Linux usa bootstrap shell **fixo** com handshake de identificador/PID, barreira STOP,
validação de identidade/cgroup e do PID fixado pelo pidfd antes da liberação. A igualdade
entre PID inicial e anunciado é comprovada pelo protocolo; divergência recusa o lançamento.
O exec conserva PID e recebe ArgumentList sem interpolação. Isto é exceção
interna documentada à descrição de lançamento sem shell da AD-17, não execução de
comando arbitrário. O gerenciador cria uma unidade exclusiva fora do cgroup do Worker;
não há alteração automática de serviço, drop-in ou Delegate no homelab.

O plano inicial de Job Objects Windows foi retirado antes da integração: o backend e
seu teste nativo foram removidos e o modo estrito recusa plataformas não Linux.
A prioridade é integrar a contenção Linux validada; eventual retomada Windows exige
nova autorização e homologação própria, fora desta PR. O legado de compatibilidade
permanece apenas por escolha explícita, com garantia inferior, sem fallback automático.

Estrito é o padrão: falta de suporte recusa execução. Compatibilidade só por opção
explícita (`DANTE_AGENT_CONTAINMENT_MODE=compatibility`) e com aviso de garantia inferior;
nunca fallback automático. A implantação ou migração operacional exige outra autorização.
Não se trata de sandbox contra brokers externos ou código malicioso com privilégios
do mesmo usuário. Detalhes, dependências e matriz de testes no
[contrato de contenção](../development/contencao-de-agentes.md).

## AD-64 — Proveniência por trecho na captura e snapshot sem recontagem (#147)

Status: vigente na branch da #147; a #147 permanece NO-GO.

Contexto: a análise de 15/11/2026 mostrou ~67% do pacote como texto citado, com o mesmo
incidente e solução na fonte, nos conhecimentos e no snapshot. A prova de consolidação
existente só aceitava um conhecimento por parte indexada contendo a parte inteira, o que a
captura pelo MCP nunca produzia (a proveniência era só a conversa).

Decisão:

* a proveniência de um conhecimento derivado de fonte usa os campos existentes:
  `ReferenciaDaFonte` = referência canônica do trecho com intervalo
  (`…/parte/<n>?hash=<documento>&inicio=<a>&fim=<b>&sha256=<texto citado>`),
  `RevisaoDaFonte` = hash do documento, `TrechoDaFonte` = texto citado; a relação
  conhecimento → trecho é essa referência. Sem migration;
* o servidor, não o agente, resolve a citação (linhas inteiras, única, numa parte, contida
  literalmente no conteúdo) e calcula intervalo e hashes;
* a prova de cobertura mantém remontagem contínua, igualdade com a fonte, redação e
  referência exata, e passa a aceitar a união de intervalos alinhados por linha; só
  espaço em branco fica fora. Linha cortada, hash, revisão ou conteúdo divergente invalidam;
* snapshot: Progresso e UltimoResultado podem ficar vazios; o já registrado é citado por
  referência tipada; omitir no MCP preserva. Deduplicação segue só por igualdade;
* composição C1: cabeçalho enxuto com as mesmas regras e título em chave própria quando
  `DadosEstruturados` é só `{"titulo"}`. A4 (relação por posição) rejeitada por
  instabilidade entre pacotes; A5–A7 fora.

Consequências: conhecimentos antigos continuam válidos e mantêm a fonte integral. Omitir
a fonte só compensa quando os conhecimentos citam as linhas em vez de parafraseá-las; a
duplicação deixa de existir, não é escondida. Limite: um intervalo contíguo por conhecimento.


## AD-65 — Legenda do pacote: cobertura implícita e fonte omitida por chave (#147)

Status: proposta em PR (#295), corrigida após a revisão independente; a #147 permanece NO-GO.

Contexto: depois da AD-64, o pacote repetia metadados fixos por item. O rótulo "Conclusão do agente, não confirmada"
saía em cada conclusão, a lista `ConsolidadaEm` repetia `Ref`s presentes no mesmo pacote e cada fonte omitida trazia
~165 B de explicação em `Conteudo`.

Decisão: só formas sem perda, cada uma com a regra declarada no cabeçalho.

* **Rótulo (O1) — não adotado:** levar o rótulo repetido ao cabeçalho contradiz `ContratoDoBrainParaAgentes`, que
  manda mostrar junto de cada item o rótulo retornado, e já houve regressão de rótulos (#285). O rótulo continua em
  cada linha, e um teste de contrato exige isso junto com Status, NaturezaDeOrigem, Natureza, autoria e
  sensibilidade. Reabrir O1 exige ensinar a legenda no contrato e validá-la com agente real, com autorização própria.
* **Fonte omitida (O3):** a omissão passa a ser a chave `Omitida:true` sem `Conteudo`, e a explicação vai uma vez ao
  cabeçalho. Chave é metadado escrito só pelo formatador, então conteúdo de fonte não imita a omissão.
* **Cobertura (O2):** `ConsolidadaEm` só sai quando a cobertura é exatamente o conjunto de todos os conhecimentos do
  pacote; o cabeçalho declara "Sem ConsolidadaEm: todos os conhecimentos deste pacote". Com qualquer outro
  conhecimento no pacote (p. ex. uma Decisão) ou com duas fontes omitidas, a lista fica explícita. Remover a lista em
  todos os casos foi rejeitado: o leitor deixaria de saber quais conhecimentos cobrem qual fonte.
* **Orçamento da fonte omitida:** a seleção custa uma fonte omissível (prova de cobertura com todos os
  conhecimentos entre os candidatos) pelo maior valor entre a íntegra e a forma omitida com `ConsolidadaEm`
  explícita. Na primeira fonte omissível admitida, soma também a linha de explicação do cabeçalho (com o sufixo
  implícito). Numa fonte curta, a forma omitida e a explicação custam mais que a íntegra. Sem essa conta, o pacote
  final passava do orçamento: 534 tokens estimados para um orçamento de 492, na revisão. O custo admitido é um
  limite superior do texto final, e as relações entram só se o texto final couber. O plano de forma só escolhe,
  entre formas equivalentes, a mais curta.

Consequências:

* a economia depende da forma do pacote, sobre os mesmos estados restaurados. Os números medidos antes da
  revisão incluíam O1; continuam válidos só onde O1 não atuava:
  - −16 B (−0,5%) no curto do roteiro `@2`;
  - 0 B nas retomadas de 15/11.

  No fundamento (−268 B), no médio (−3,3%) e no longo (−5,3%), parte da economia vinha de O1 e não vale mais. Para
  a forma do fundamento (uma fonte coberta por quatro conclusões), o teste determinístico dá −194 B só com O2 e O3.
  Não houve nova medição.
* não muda nenhum essencial;
* não muda o teto de 12 itens. A seleção fica mais conservadora só para a fonte omissível curta. Fontes longas
  continuam custadas pela íntegra e descartadas pelo orçamento quando não cabem; esse limite está registrado e fica
  fora daqui;
* o contrato dos agentes não muda: cada item traz o próprio rótulo.

## AD-66 — Ledger de execuções no PostgreSQL; memória continua sendo o runtime (#299)

Status: vigente, fase 1a (espelho) da Epic #296. Proposta aprovada na #297 (PR #298). Não substitui a AD-06: os
jobs continuam com fonte de verdade em memória enquanto a persistência não for obrigatória (fase 1c). Complementa
a AD-20 e decide explicitamente o uso do banco fora do Brain, sem editar a AD-34. Desligada por padrão; ativação no
serviço do homelab e migration no banco canônico exigem autorização operacional própria.

Contexto: jobs (AD-06) e sessões (AD-20) existiam só em memória, com códigos `J…`/`S…` que recomeçavam a cada
reinício. Fila durável, recuperação, delegação e acompanhamento (Epic #296) precisam de um registro de trabalho que
atravesse reinícios. O `DanteDbContext` exige `AutorizacaoDoBrain` e aplica `FiltrosDoBrain` e
`ValidacaoDeEscritaDoBrain`, que não fazem sentido para execuções.

Decisão:

* **Execução** é um terceiro conceito, distinto de sessão (AD-15/16) e de turno. O ledger persiste execuções,
  etapas, dependências entre etapas, eventos-chave e metadados de sessões de agente, com o vínculo execução → etapa
  → sessão → turno e a autoridade de origem (canal e usuário). Processos, drivers, fila de turnos, deltas e
  approvals da CLI continuam em memória.
* Mesmo PostgreSQL e mesma `ConnectionStrings:Dante` do Brain, **schema `orquestracao`** com histórico de
  migrations próprio (`orquestracao.__historico_de_migrations`) e **`OrquestracaoDbContext` próprio**, sem as
  políticas do Brain. Os mappings do ledger vivem em `Modulos/Execucoes` e só esse contexto os carrega;
  `Repository<T>`, `UnitOfWork`, `EntidadeConfiguration` e `xmin` como `Versao` (AD-43) são reutilizados.
* **Transições no domínio**: `Execucao`, `EtapaDaExecucao` e `SessaoDeAgente` mudam de estado só por métodos que
  recusam a origem inválida (`TransicaoInvalidaException`), conforme os diagramas de
  [PERSISTENCE_AND_EXECUTIONS](../architecture/multi-agent-platform/PERSISTENCE_AND_EXECUTIONS.md#estados). Toda
  gravação usa `xmin`: duas gravações concorrentes resultam em `ConflitoDeConcorrenciaException`, nunca em
  sobrescrita.
* **Códigos por sequência do banco** (`seq_numero_da_execucao`, `seq_numero_da_sessao`): `J…` para o job legado
  (execução `Avulsa`) e `S…` para sessões, no mesmo formato de hoje, sem repetir após reinício. Eventos têm
  sequência global (`seq_eventos`) reservada na ordem lógica em que são adicionados.
* **Integridade no banco**: FKs `Restrict`, dependência e evento ligados à etapa por FK composta com a execução (o
  banco recusa relação entre execuções diferentes), número e chave de idempotência únicos, check constraints.
* **Espelho best-effort (fase 1a)**: `JobRegistry` e `SessionRegistry` recebem um `EspelhoDoLedger` opcional. Só a
  reserva do número espera o banco (prazo de 3 s; após falha, 30 s sem consultar); os registros vão a uma fila em
  memória limitada, gravados em ordem por um único consumidor. Falha de gravação é logada e não afeta job nem
  sessão; banco indisponível volta ao contador local, sem espelho, e nunca repete um id já usado no processo. Cada
  reserva informa o maior número já usado localmente; a sequência atrás dele é avançada até ele, e o espelho volta a
  valer quando o banco retorna. Reservas de execução e de sessão são serializadas entre conexões por
  `pg_advisory_xact_lock`, então `nextval` e `setval` nunca se intercalam e a sequência nunca recua. A descarga do
  encerramento espera espaço na fila cheia em vez de retornar com registros pendentes.
* **Encerramento**: o hosted service `EncerramentoDoLedger`, registrado antes dos demais, para por último. No
  `StopAsync`, depois de o polling esperar os jobs, encerra as sessões vivas (`Interrompida`, não `Falhou`) e grava
  a fila enquanto o provider ainda cria escopos; o descarte do provider vem depois.
* **Política de eventos**: só metadados de turnos, ferramentas (tipo, servidor, nome), solicitações (código,
  decisão) e uso de tokens. Nunca deltas, texto de mensagem, saída de processo, descrição de comando ou caminho de
  arquivo. O texto do pedido do job não é registrado.
* **Ativação**: `Orquestracao:Habilitada=true` **e** banco configurado. A connection string sozinha não liga o
  ledger. Sem banco ou com a chave ausente/falsa, o Worker compõe exatamente o legado.
* **Migrations só por comando explícito**: `--orquestracao migrate`, separado de `--brain migrate`; migrar um nunca
  cria nem altera o schema do outro. Nenhum host migra na inicialização (AD-53).
* Execuções novas (`Tarefa`, `Roteiro`...) não existem nesta entrega: o único tipo é `Avulsa`, e nada aqui escreve
  no checkout principal por uma execução nova.

Consequências:

* com o ledger ligado, o histórico de jobs e sessões sobrevive ao reinício e é consultável por
  `ILedgerDeExecucoesAppService`; `/status`, `/cancel`, `/session` e os códigos visíveis não mudam;
* o espelho pode perder registros (fila cheia, banco fora); a fila durável e a recuperação (Issue 2 do roadmap)
  tornam a gravação transacional e obrigatória;
* a sequência de eventos é reservada antes do commit: com vários escritores concorrentes, um leitor por sequência
  poderia ver lacunas temporárias. Hoje há um único escritor (o consumidor do espelho); leitura incremental por
  sequência (SSE, AD-73) deve tratar isso;
* agente/versão, raiz/pai, profundidade e unidade de contenção da sessão ainda não são colunas: entram com as
  entregas que os usam.

## AD-67 — Fila durável no PostgreSQL com lease, heartbeat, fencing e recuperação após reinício (#300)

Status: vigente, Issue 2 da Epic #296. Proposta aprovada na #297 (PR #298). Substitui a AD-06 quando
`Orquestracao:Habilitada=true`; sem a chave, a AD-06 continua valendo e o Worker compõe exatamente o legado.
Complementa a AD-66 (o ledger passa a ser gravado sob posse do trabalho) e a AD-63 (varredura de scopes órfãos na
inicialização). Desligada por padrão; a migration no banco canônico e a ativação no serviço exigem autorização
operacional própria.

Contexto: com o ledger da AD-66, jobs e sessões eram espelhados de forma best-effort e nada era retomado depois de um
reinício. Faltava um caminho durável para enfileirar trabalho, impedir execução dupla entre processos, sobreviver à
morte do Worker no meio de uma CLI e decidir com segurança o que fazer com uma etapa interrompida.

Decisão:

* **Tabela `orquestracao.trabalhos_da_fila` como outbox transacional.** O produtor cria execução/etapa pelos métodos do
  domínio, chama `IFilaDeTrabalho.EnfileirarAsync` e confirma com `IUnitOfWorkDaOrquestracao.SalvarAlteracoesAsync`:
  execução, etapa, evento e trabalho entram numa transação. `chave_de_deduplicacao` é única: a mesma chave produz no
  máximo um trabalho e uma execução lógica (corrida → `ConflitoDeConcorrenciaException`, nada gravado). `dados` guarda
  só identificadores.
* **Reivindicação** por um único `UPDATE … FROM (SELECT … FOR UPDATE SKIP LOCKED LIMIT 1)`: pendente com
  `disponivel_em` vencido ou reivindicado com lease vencido; incrementa `geracao` (fencing token) e `tentativas`. Os
  instantes vêm do `TimeProvider` do processo (controlável nos testes), não de `now()`.
* **Lease 60 s, heartbeat 20 s, polling 5 s, 1 slot** por padrão (`Orquestracao:Fila:{LeaseSegundos, HeartbeatSegundos,
  PollingSegundos, Slots}`; o heartbeat precisa caber duas vezes no lease). O limite global de 1 processo de execução
  vale até a contenção estrita da AD-63 estar ativa no serviço.
* **Fencing**: toda gravação do processador passa por `GravarComPosseAsync`, numa transação que trava a linha do
  trabalho (`FOR UPDATE`) e confere geração, dono, boot, estado e lease não vencido antes de qualquer escrita; a
  conclusão da etapa, a transição da execução, os eventos, o próximo trabalho e o fim do trabalho atual são uma única
  transação. Posse perdida → `PosseDoTrabalhoPerdidaException`, nada gravado. Heartbeat recusado (ou sem renovar até o
  lease vencer) cancela a etapa localmente; o heartbeat nunca dorme além do lease, então a CLI não segue rodando depois
  de a posse poder ter passado a outro processador, e um prazo local independente (lease menos uma margem) cancela a
  etapa mesmo que a renovação fique presa no banco.
* **Nenhuma transação aberta durante a CLI**: reivindicar, renovar e gravar são transações curtas em escopos próprios; o
  executor roda o processo entre elas.
* **Sinal**: trigger `trg_trabalhos_da_fila_notificar` faz `pg_notify('dante_fila')` no commit de todo trabalho
  pendente; o processador escuta (`LISTEN`, conexão dedicada que se refaz) e também faz polling, e dorme até o próximo
  `disponivel_em` sem busy loop. Notificação perdida só atrasa.
* **Backoff** de erro transitório do D.A.N.T.E. e de falha do executor (inclusive depois de iniciar a etapa): 5 s, 10 s,
  20 s… até 10 min, `max_tentativas` 5.
* **Limite de tentativas** verificado antes de executar ou retomar: toda reivindicação conta (inclusive a que recupera o
  lease vencido de um processo morto por SIGKILL, que nunca devolve o trabalho); só o encerramento do Worker e a
  devolução a outro host que tem o pedido não contam. `tentativas` inclui a atual: a de número `max_tentativas` ainda
  roda, a seguinte não inicia executor nenhum. Esgotadas, o trabalho termina `Falhou` na mesma transação da transição
  terminal decidida pelo estado da etapa: não iniciada (nenhuma ação externa começou, o início é gravado antes do
  processo) ou `Automatica` em execução → execução `Falhou` com motivo; `ComConfirmacao` em execução → `Interrompida`;
  `Nunca` em execução → `Bloqueada` (resultado incerto nunca vira falha comum). As duas últimas só voltam por
  confirmação explícita, com trabalho novo.
* **Recuperação na inicialização**, antes da primeira reivindicação: lista `dante-agent-*.scope` e para **só** as
  unidades que o ledger registra em sessões de um boot anterior **do mesmo host** (`sessoes_de_agente`
  `unidade_de_contencao`, `boot_do_worker` e `maquina_do_worker`, colunas novas); desconhecidas são relatadas e nunca
  paradas, e sem listagem nada é parado. Sessões não terminais de boot anterior do mesmo host → `Interrompida`; com
  unidade registrada, só depois de confirmar que ela não está ativa (ausente de uma listagem bem-sucedida ou parada
  agora) — sem listagem ou com falha ao parar, a sessão continua não terminal (contada como pendente no relatório) e a
  próxima inicialização tenta de novo. Sessão sem unidade registrada vira `Interrompida` sem essa confirmação. De outro
  host ou sem boot registrado (anteriores à #300), intocadas. Trabalhos reivindicados por outro boot do mesmo host voltam à fila
  sem esperar o lease (o Worker é instância única no host; o fencing protege mesmo que não seja).
* **Política de retomada** ao reivindicar uma etapa que estava `EmExecucao`: `Automatica` → nova tentativa e execução
  `Pendente`; `ComConfirmacao` (escrita; o job legado) → `Interrompida`, só volta por
  `IFilaDeExecucoesAppService.ConfirmarRetomadaAsync` (chave por tentativa); `Nunca` (ação externa de resultado
  incerto) → `Bloqueada` com motivo, nunca repetida automaticamente. O dono recebe aviso no Telegram.
* **Jobs `/claude` e `/codex` pelo caminho da fila** quando habilitada: número reservado, execução `Avulsa` + trabalho
  gravados antes do "iniciado", chave `telegram:<chat>:<mensagem>`; o processador reivindica e chama o mesmo
  `RunJobAsync`, com início e resultado gravados sob posse antes da entrega. Respostas, `/status` e `/cancel`
  inalterados. O pedido continua só em memória: depois de um reinício, job não iniciado é cancelado com aviso e job em
  execução fica `Interrompida`. O trabalho do job registra a origem (`host/boot`): outro processo que não tem o pedido só
  o cancela se a origem for deste host (boot morto); de outro host, devolve sem contar tentativa e nunca cancela. Banco
  indisponível recusa o job ("Fila de execuções indisponível") em vez de rodá-lo
  sem registro.
* **Contrato para execuções novas** (`Tarefa`, #301): enfileirar com `TrabalhoDaFila.AvancarExecucao` no mesmo
  salvamento da execução e registrar um `IExecutorDeEtapa` para o `TipoDeExecucao` no Worker
  ([DURABLE_QUEUE](../architecture/multi-agent-platform/DURABLE_QUEUE.md#implementação-300)).
* **Diagnóstico**: logs estruturados (reivindicação, lease perdido, retentativa, deduplicação, interrupção, recuperação
  com unidades paradas e desconhecidas) e medidor `Dante.Orquestracao.Fila` com contadores, sem conteúdo.

Consequências:

* com a chave ligada, Postgres passa a ser requisito para jobs; a gravação deixa de ser best-effort para jobs (sessões
  continuam espelhadas pela AD-66);
* jobs passam a ser executados um por vez (1 slot) até a ativação da AD-63 no serviço;
* processo órfão de job one-shot (sem scope) depende do cgroup do serviço systemd para morrer junto com o Worker; o
  registro de PID para matar órfãos em modo compatibilidade não entra aqui;
* trabalhos concluídos não são apagados ainda (retenção de 7 dias proposta fica para entrega própria);
* a unidade de contenção é gravada pelo espelho best-effort: uma sessão iniciada imediatamente antes da morte do
  Worker pode não ter a unidade registrada e, então, é só relatada, nunca parada.

Código: `Dante.Domain/Execucoes/TrabalhoDaFila.cs`, `Dante.Application/Execucoes/{Portas/IFilaDeTrabalho.cs,
FilaDeExecucoesAppService.cs, RecuperacaoDeExecucoesAppService.cs}`, `Dante.Infrastructure/Fila`,
`Dante.Infrastructure/Agentes/Contencao/UnidadesDeContencaoSystemd.cs`, `Dante.Worker/Execucoes/ProcessadorDaFila.cs`,
`Dante.Worker/Jobs/ExecutorDeJobsDaFila.cs`; testes em `FilaDuravelPostgreSqlTests`, `FilaDuravelEndToEndTests` e
`FilaDuravelTests`.

## AD-68 — Agente registrado e versionado, separado de sessão e execução (#301)

Status: vigente (Epic #296, etapa 3 do roadmap). Proposta aprovada na #297 (PR #298). Complementa AD-24/25/26/27 e
a AD-66. Depende do mesmo banco e da mesma chave `Orquestracao:Habilitada` do ledger.
Complementada pela AD-71 (#307): a "tarefa somente leitura" abaixo continua sendo o padrão, e a escrita passa a existir
só pelo pedido explícito `escrever`, em worktrees, com driver confinado; a recusa de etapa de escrita no domínio virou a
recusa de escrita com retomada automática.

Contexto: `AgentKind` é o driver (CLI), não uma identidade. Papéis reutilizáveis (revisor, implementador) precisam
de configuração própria, reprodutível e com permissões fechadas, sem mudar `/claude`, `/codex` nem a conversa.

Decisão:

* **Agente** (`agentes`): handle único, minúsculo (`[a-z][a-z0-9-]{1,30}`), sem `@` (repositório, AD-27),
  imutável e nunca reutilizado — nem depois de arquivado; palavras que os comandos já interpretam na posição do
  agente (`claude`, `codex`, modos, `default`…) são reservadas. Estados `Ativo ⇄ Desativado → Arquivado`; arquivar
  exige desativado e nenhuma execução ou sessão ativa. Remoção física não existe. Nome e descrição mudam sem versão.
* **VersaoDoAgente** (`versoes_de_agente`): imutável, `(agente, número)` único. Driver, modelo e esforço (conferidos
  no catálogo da CLI ao salvar e de novo ao iniciar, AD-25/26), instruções, capacidades declaradas
  (`implementar|revisar|pesquisar|planejar`, sem conceder nada), perfil padrão e máximo (`plan < manual < auto`;
  sem escrita ⇒ máximo `plan`; `implementar` exige escrita), concessões de ferramentas do D.A.N.T.E.
  (`brain`, `planilhas`) e de repositórios (aliases ou `*`; vazio = só o workspace geral; vinculados também precisam
  de concessão), limites (`ExecucoesSimultaneas` aplicado às sessões; tokens, turnos e minutos por etapa
  declarados para as entregas que executam etapas). `HashDaConfiguracao` = SHA-256 do JSON canônico (chaves em ordem
  fixa, listas ordenadas, enums pelo nome, `formato` versionado). Editar cria a próxima versão; editar sem mudança é
  recusado. Triggers recusam UPDATE/DELETE de versão, troca de handle e DELETE de agente.
* **Embutidos** `claude`/`codex`: não editáveis e sem linha no banco; a configuração é materializada das
  preferências do usuário no início de cada sessão (modelo, esforço, modo; máximo `auto`, todas as ferramentas,
  qualquer repositório — exatamente o que esses caminhos já permitiam). `/claude`, `/codex`, `/agent set`, a
  conversa e `Avulsa` não mudam.
* **Resolução (extensão da AD-27)**: `/session start <token>` interpreta, nesta ordem, `claude|codex`, `@alias`,
  `model=`/`effort=`, modo e, por último, handle registrado. Handle inexistente, desativado, arquivado ou com versão
  cujo hash não confere é recusado sem fallback. Mensagem comum nunca resolve handle.
* **Sessão congela a versão**: `SessionStartRequest.RegisteredAgent` leva a versão; o `SessionRegistry` recusa,
  por conta própria, driver diferente, modelo/esforço diferentes dos da versão, perfil acima do máximo,
  repositório não concedido e limite de sessões simultâneas; `/mode session` também não passa do máximo. Instruções do agente entram em `ContextInstructions`
  depois do contrato da conversa e do contrato multirrepositório, declaradas como incapazes de revogá-los. Servidores
  de ferramentas são filtrados pelas concessões (desconhecido nunca é concedido); o contexto do Brain injetado no
  turno (AD-46) também depende da concessão `brain`. Modelo e esforço vêm só da versão (override recusado).
* **Ledger**: `sessoes_de_agente` ganha `handle_do_agente`, `hash_da_configuracao_do_agente` e
  `id_versao_do_agente` (FK); embutido registra handle e hash da configuração materializada. `execucoes` ganha
  `id_versao_do_agente` (FK) e o tipo `Tarefa`, que exige versão (check constraint).
* **Tarefa somente leitura**: `/tarefa <handle> [@alias] <texto>` monta `PlanoDaTarefa` com perfil efetivo `plan`
  (qualquer que seja a versão) e uma etapa `TurnoDeAgente` sem `Escrita`, retomada `Automatica`. Fail-closed em três
  camadas: o domínio recusa criar etapa de escrita em execução nova (`EscritaSemWorktreeException`),
  `GuardaDeEscrita.Exigir` recusa no executor escrita e perfil diferente de `plan`, e a sessão da tarefa tem teto
  `plan`. O checkout principal nunca recebe escrita de execução nova. Tarefa com agente embutido é recusada (use
  `/claude`/`/codex`).
* **Despacho pela fila (AD-67)**: `DespacharTarefaAsync` é o ponto único. Numa transação
  (`IFilaDeExecucoesAppService.EnfileirarTarefaAsync`): número reservado, `MontagemDaTarefa.CriarExecucao`, execução
  com a versão congelada, etapa, evento `ExecucaoCriada` (tipo, handle, número da versão) e trabalho `AvancarExecucao`
  com a chave `telegram:<chat>:<mensagem>` (mesma deduplicação dos jobs; corrida → `ConflitoDeConcorrenciaException`,
  nada criado). Só depois o usuário recebe "Tarefa E… enfileirada"; até essa confirmação o processador espera, e uma
  confirmação que falha termina a execução `Falhou` sem processo. O `ExecutorDeTarefasDaFila` (`TipoDeExecucao.Tarefa`)
  confere a cada tentativa, antes de qualquer processo: `GuardaDeEscrita.Exigir`; execução apontando para a versão do
  pedido e etapa com driver/modelo/esforço dela; agente ainda resolvível (desativado ou hash divergente → recusa); e
  modelo/esforço da versão de novo no catálogo da CLI (`ConferirVersaoNoCatalogoAsync`). Recusa definitiva → etapa e
  execução `Falhou` com aviso; catálogo ou banco indisponível → backoff da fila, contando tentativa. Então a etapa vai a
  `EmExecucao` sob posse, uma **sessão reservada** (`SessionStartRequest.Observer`) roda um turno com o pedido fora de
  transação e é encerrada, e o resultado é gravado sob posse **antes** da entrega (`DeliverJobAsync`, código `E…`;
  saída omitida se o ambiente do repositório tem segredos). Posse perdida ou encerramento cancelam a sessão: nada é
  gravado nem respondido, e a retomada `Automatica` refaz a leitura no mesmo processo.
* **Sessão reservada**: nunca vira a sessão ativa do dono (mensagem comum, `/session select|stop|close|mode`,
  respostas e `/steer` não a alcançam); seus eventos vão só ao executor, nunca ao Telegram; pedidos de aprovação são
  negados pelo executor (somente leitura). Sem observador, toda sessão continua exatamente como antes. Aparece em
  `/session list` e conta no limite `ExecucoesSimultaneas` da versão.
* **Reinício**: o pedido (texto exato e ambiente do repositório) fica só em memória, como o do job; o ledger guarda o
  objetivo cortado e redigido, insuficiente para reexecutar com fidelidade. Depois de um reinício, a tarefa que este
  processo não tem é cancelada com aviso (origem deste host) e nunca executada às cegas.
* **Segredos**: o cadastro não tem campo de credencial; handle, instruções, nome e descrição com aparência de
  segredo (`ProtecaoDeSegredos`) são recusados; listas da versão são somente leitura também em memória; o objetivo registrado da tarefa é redigido.
* **Telegram**: `/agentes [todos]`, `/agente criar|ver|editar|nome|descricao|desativar|reativar|arquivar`, `/tarefa`.
  Desativar lista as sessões que seguem ativas (códigos só do próprio usuário).

Consequências:

* identidade e permissões de agente viram dados versionados; uma execução antiga aponta para a versão exata;
* provider continua derivado do driver enquanto só houver CLIs;
* `PodeConsultar` e limites de delegação ficam para a delegação (etapa 7); `brain` não distingue leitura/escrita;
* `Avulsa` continua registrando agente/modelo/esforço na etapa, sem hash do embutido (o caminho do job passa pela
  fila na #300).

Código: `Dante.Domain/Agentes`, `Dante.Application/Agentes`, `Dante.Application/Execucoes/GuardaDeEscrita.cs`,
`Dante.Infrastructure/Modulos/Agentes`, migration `CadastroDeAgentes`, `Sessions/AgenteDaSessao.cs`,
`Sessions/SolicitacaoDaTarefa.cs`, `Telegram/TelegramAgentes.cs`, `Telegram/TelegramPollingService.Agentes.cs`,
`Worker/Execucoes/{ExecutorDeTarefasDaFila,ObservadorDaTarefa}.cs`.
Testes: `AgentesDominioTests`, `CadastroDeAgentesAppServiceTests`, `TelegramAgentesTests`,
`CadastroDeAgentesPostgreSqlTests`, `SessaoReservadaTests`, `TarefaPelaFilaEndToEndTests`.


## AD-69 — Grupos versionados de agentes com roteiro determinístico e ativação humana vinculada a hash (#308)

Status: vigente (etapa 6 do roadmap da Epic #296, entregue em três PRs da #308: cadastro, gate durável e executor do
roteiro com a fundação do `dante_coordenacao`). Proposta
aprovada na #297 (PR #298) e decisões de escopo da #308 (A–H). Complementa a AD-68 (grupos
referenciam agentes registrados) e a AD-71 (quem escreve precisa de confinamento de SO). Depende do mesmo banco e da
mesma chave `Orquestracao:Habilitada`; migration `GruposDeAgentes`, não aplicada no banco canônico.

Contexto: o pipeline implementar → revisar → corrigir → aprovação era feito à mão. Para virar unidade de execução, o
grupo precisa de identidade, papéis e permissões explícitos e reprodutíveis, decididos pelo sistema e não inferidos do
prompt, e uma execução iniciada não pode mudar quando o grupo ou um agente muda.

Decisão:

* **Grupo** (`grupos_de_agentes`): nome único, minúsculo (`[a-z][a-z0-9-]{1,30}`), imutável e nunca reutilizado; as
  ações de `/grupo` são nomes reservados. Estados `Ativo ⇄ Desativado → Arquivado`; remoção física não existe (trigger).
* **VersaoDoGrupo** (`versoes_de_grupo` + `participantes_de_grupo`): imutável (triggers recusam UPDATE e DELETE das duas
  tabelas), `(grupo, número)` único. Modo `Roteiro` (o coordenador por papel é da etapa 8). Participantes referenciam o
  **agente**, não a versão dele: papel único (`[a-z][a-z0-9-]{1,30}`), responsabilidades e `PodeEscrever`. A versão de
  cada agente é congelada só na execução.
* **Roteiro determinístico, forma fechada**: `implementar:<papel>`, `revisar:<papel>`, `corrigir:<papel>` opcional e
  `aprovar`, nesta ordem. As transições são do orquestrador, não do roteiro nem de um modelo: implementar → revisar;
  revisão aprovada → aprovação humana; revisão pedindo correção → corrigir → revisar enquanto houver rodadas; sem
  `corrigir` ou sem rodadas, bloqueia (nunca aprova implicitamente). Implementar e corrigir exigem papel com escrita;
  revisar exige papel sem escrita; todo participante executa algum passo; a aprovação não tem papel.
* **Política congelada na versão**: `RodadasDeRevisaoMaximas` (1–5 com correção, 0 sem), `ParalelismoMaximo` = 1
  (check no banco: estritamente sequencial enquanto o limite global for 1), repositórios permitidos (aliases; vazio = os
  do pedido, sempre dentro das concessões de cada agente) e duração máxima opcional.
* **Hash canônico**: SHA-256 do JSON canônico (formato versionado, participantes ordenados por papel, roteiro na ordem
  dos passos, listas ordenadas, enums pelo nome). Versão cujo conteúdo gravado não reproduz o hash, ou não passa mais
  na validação, não é íntegra: não é ativada nem resolvida.
* **Conferência contra os agentes** (Application, `ParticipacaoNoRoteiro`), ao salvar, ao ativar e ao resolver para
  execução, sempre com a versão atual de cada agente: agente registrado (embutido recusado), ativo e com versão íntegra;
  `PodeEscrever` do participante ≤ o da versão; capacidade `implementar` para implementar/corrigir e `revisar` para
  revisar; quem escreve precisa de driver com confinamento de escrita (`ConfinamentoDeEscrita`, só Codex) e quem revisa
  precisa de Codex, porque a revisão será uma sessão Codex com sandbox somente leitura (decisões A e D). Nada é
  substituído automaticamente: participante desativado faz a resolução recusar com o motivo.
* **Ativação = decisão humana vinculada ao hash** (decisão F): versão nova nasce não ativada; `/grupo ativar <nome> v<N>
  <hash>` exige o hash (ou o prefixo exibido, ≥ 12 caracteres) e grava uma `AtivacaoDeVersaoDoGrupo` imutável (versão,
  número, hash aprovado, autor, momento). O banco confere a decisão contra a versão gravada (trigger na inserção) e só
  aceita `id_versao_ativa` com decisão correspondente (constraint trigger adiada para o commit). Execuções novas usam
  só a versão ativa; editar não troca a ativa. Ativar não concede nada além das capacidades já dadas aos agentes.
* **Concorrência**: índice único `(grupo, número)` e `xmin` do grupo: edições ou ativações simultâneas gravam uma só,
  a outra recebe `ConflitoDeConcorrenciaException`. Nome único idem.
* **Interface**: `ICadastroDeGruposAppService` (criar, consultar, listar, editar, ativar, descrever, desativar,
  reativar, arquivar, `ResolverParaExecucaoAsync`). Telegram é adaptador (`TelegramGrupos`): `/grupos [todos]`,
  `/grupo criar|ver|editar|ativar|descricao|desativar|reativar|arquivar`, com participantes nas linhas seguintes
  (`<papel>=<handle> [escrita] [: responsabilidades]`). Nome, descrição, papéis e responsabilidades com aparência de
  segredo são recusados.

**Gate durável (segunda entrega da #308, decisões B, C, F e G)**, migration `GateDoRoteiro`, não aplicada no banco
canônico:

* **Execução `Roteiro`** (`TipoDeExecucao.Roteiro`): aponta para a versão ativa do grupo (`id_versao_do_grupo`, check:
  só o Roteiro e sempre ele) e nunca para um agente único. Cada etapa guarda passo, rodada, papel e a versão do agente
  participante (`EtapaDaExecucao.DoRoteiro`): implementar e corrigir escrevem (retomada com confirmação), revisar lê
  (automática) e a aprovação humana (`TipoDeEtapa.AprovacaoHumana`) não roda processo. As etapas são criadas uma a
  uma, numeradas depois das existentes e dependentes da anterior.
* **Snapshot imutável**: `participantes_da_execucao` (papel, handle, versão e hash de cada agente, escrita,
  responsabilidades) e `roteiros_da_execucao` (grupo, versão, hash da versão, passos, limite de rodadas e
  `hash_do_snapshot` = SHA-256 do grupo mais versão e hash de cada participante por papel). Triggers recusam alterar ou
  remover participantes e o snapshot; editar ou ativar outra versão do agente ou do grupo nunca alcança a execução.
  `ConferirIntegridadeAsync` recalcula o hash do snapshot pelas linhas e confere a versão do grupo e cada versão de
  agente (íntegras e com o hash congelado) antes de decidir ou continuar.
* **Objetivo protegido**: o objetivo bruto nunca é gravado. Antes de persistir, ele é inspecionado
  (`ProtecaoDeSegredos`); o ledger guarda o texto (ou `[conteúdo protegido]`), se a redação o alterou e o SHA-256 do
  original com a execução como sal. Objetivo acima de 8000 caracteres é recusado em vez de cortado. Quando a redação o
  alterou e uma etapa precisar dele sem o original em memória (reinício), `SolicitarObjetivoAsync` deixa a execução
  `AguardandoEntrada` sem iniciar a etapa, e `SubstituirObjetivoAsync` aceita um objetivo novo só se ele passar intacto
  pela redação (o trigger só permite trocar um objetivo protegido por um utilizável). Nada é reconstruído nem segue com
  contexto incompleto.
* **Progressão fixa** (`ProgressaoDoRoteiro`, função pura): início → implementar r0; implementar ou corrigir →
  revisar (mesma rodada); revisão aprovada → aprovação humana; revisão pedindo correção → corrigir (rodada + 1) se o
  roteiro tem o passo e há rodadas, senão bloqueia; revisão bloqueada → bloqueia. Esgotar as rodadas nunca aprova:
  a execução fica `Bloqueada` com o motivo. `ConcluirPassoAsync` grava, sob posse, a conclusão e a transição seguinte
  numa transação; falha e cancelamento (inclusive o pedido do dono) seguem a conclusão comum da fila.
* **Gate persistido** (`pedidos_de_aprovacao`): ao chegar à aprovação, a etapa de aprovação fica
  `AguardandoAprovacao`, o pedido grava a rodada, o hash do snapshot e o hash do artefato revisado (o diff), e a
  execução aguarda sem trabalho ativo. No máximo um pendente por execução (índice único parcial); um trigger recusa
  alterar um pedido decidido, trocar execução, etapa ou hashes, e remover. `DecidirAsync` é do proprietário (AD-74),
  trava trabalhos e execução na ordem da posse e confere a integridade antes de mudar qualquer coisa. Aprovar e
  corrigir exigem o hash do artefato exibido (≥ 12 caracteres); corrigir exige a instrução, recusada se parecer
  credencial; decisão duplicada ou simultânea encontra o pedido já decidido. Aprovar conclui a execução sem commit,
  push, PR nem merge; corrigir abre a próxima rodada dentro do limite (sem rodada, o gate continua aberto) e devolve a
  execução à fila; recusar cancela. `/cancel` no gate cancela também o pedido.
* **A decisão vale só para o artefato revisado** (review do PR #316): o hash digitado não basta. Aprovar e corrigir
  recalculam o hash canônico do conteúdo atual da worktree reservada (`IGerenciadorDeWorktrees.ResumirAlteracoesAsync`,
  o mesmo cálculo do revisor) **dentro da transação da decisão**, com a execução e os trabalhos travados: antes de
  aplicar a decisão e de novo depois de gravá-la, ainda sem commit. Divergência em qualquer das duas desfaz tudo e
  devolve `ArtefatoDivergente`; o gate continua pendente, a reserva continua aguardando a decisão e nenhuma correção é
  agendada. Recusar não aceita nada e não depende do conteúdo. A janela restante (entre a segunda conferência e o
  commit) é de uma alteração posterior à decisão; a entrega futura (gateway #311) confere o hash de novo antes de usar
  o artefato. O gate também só abre para o conteúdo presente: `ConcluirPassoAsync` recusa um diff revisado diferente
  da worktree. Com o conteúdo alterado, `/execucao revisar E…` (`RevisarNovamenteAsync`) substitui o pedido (estado
  `Substituido`, com autor e o hash atual), cancela a etapa de aprovação e enfileira uma revisão da mesma rodada
  (rever não consome rodada de correção); com o conteúdo ainda igual ao revisado, é recusado.
* **Slot × worktree no gate** (decisão C): ao abrir o gate, as reservas `Pronta` passam a `AguardandoDecisao`
  (complementa a AD-71): continuam ativas para os índices únicos (caminho, branch, alias), não são limpas nem
  reutilizadas, mas não contam na capacidade, então outra escrita pode usar o slot. A correção readquire o slot
  com `ReadquirirAsync`: sob a mesma trava transacional e os mesmos limites, as reservas da execução voltam a `Pronta`
  sem replanejar a worktree; sem capacidade, nada é gravado e o processador espera sem contar tentativa; reserva de
  outro host é recusada. Um estado de reserva, e não só o estado da execução, evita o impasse de duas correções
  pendentes contarem uma à outra. Aprovação, recusa e cancelamento liberam as reservas; a limpeza preserva a worktree
  alterada.
* **Arquivamento**: agente que participa de um roteiro não terminal e grupo com roteiro não terminal não são
  arquivados.
* **Telegram** (adaptador): `/execucao E…` mostra grupo, snapshot, participantes, passos e, no gate, os comandos com o
  hash; `/execucao aprovar|corrigir E… <hash> [instrução]`, `/execucao recusar E… [motivo]` e
  `/execucao objetivo E… <tarefa>`.

**Executor do roteiro (terceira entrega da #308, decisões A, D e E)**, migration `VereditosDeRevisao`, não aplicada no
banco canônico:

* **`/grupo <nome> @alias <tarefa>`**: resolve o grupo (versão ativa, participantes válidos com a versão atual de cada
  agente) e o repositório explícito, e enfileira o roteiro pela fila durável (`ExecutorDeRoteirosDaFila.CriarAsync`,
  deduplicado pela mensagem). Um repositório por roteiro nesta entrega (vinculados recusados: a revisão confinada lê uma
  worktree só). O objetivo original fica só na memória do processo que o recebeu.
* **`ExecutorDeRoteirosDaFila`** (`TipoDeExecucao.Roteiro`): um passo por trabalho, na ordem gravada. A cada passo:
  integridade do snapshot (`ConferirIntegridadeAsync`), versão congelada do agente (`ObterVersaoDoParticipanteAsync`,
  hash conferido) de novo no catálogo da CLI, e uma sessão Codex **reservada e separada** por passo (nunca a mesma
  sessão para implementar e revisar), encerrada sempre. Sem o objetivo original em memória e com o guardado protegido,
  a execução pede outro à pessoa (decisão B). Avisos ao dono: gate aberto (com o hash e os comandos), bloqueio, falha e
  pedido de objetivo.
  * implementar: planeja, reserva atomicamente, cria e valida a worktree (AD-71) e abre a escrita confinada nela, no
    perfil padrão da versão; o prompt diz que não há commit nem push;
  * corrigir: `ReadquirirAsync` (sem capacidade, espera sem contar tentativa), revalida a worktree e escreve a partir
    dos itens do veredito e da instrução humana;
  * revisar: lê as alterações da worktree (`IGerenciadorDeWorktrees.ResumirAlteracoesAsync`: `git diff
    --no-ext-diff --no-textconv --no-color --binary <base>` e `git ls-files --others --exclude-standard -z`, os dois
    acrescentados ao conjunto fechado; hash canônico do diff inteiro mais caminho e SHA-256 de cada arquivo novo, link
    pelo destino declarado) e abre a **revisão confinada**; o diff vai no prompt (cortado em 60 000 caracteres, o hash
    cobre tudo). Sem veredito ao fim do turno, um lembrete; ainda sem, a etapa falha (texto livre nunca vale). O hash é
    recalculado depois do turno: worktree alterada durante a revisão invalida o veredito.
* **Revisão confinada** (`AgenteDaSessao.LeituraConfinada`, `AgentSessionStartOptions.LeituraConfinada`; refeita no
  review do PR #317): só sessão reservada, em `plan`, num repositório, com escopo e com Codex (o `SessionRegistry`
  recusa o resto; o driver do Claude recusa antes de qualquer processo). O confinamento de **leitura** é do sandbox do
  SO, por um perfil de permissões do Codex (`default_permissions="dante_revisao"`,
  `[permissions.dante_revisao.filesystem]` com `":minimal" = "read"`, a worktree e o diretório real do executável do
  Codex, que o sandbox reexecuta, resolvido componente a componente): home, outros checkouts, `.env`, credenciais e o
  resto do host **não existem** dentro do sandbox (bubblewrap no Linux); escrita e rede negadas. Não basta `read-only`:
  ele lê o disco inteiro. A sessão também desliga o que age fora do sandbox ou amplia o alcance (`features.apps`,
  `features.view_image`, `features.multi_agent`, `features.goals`, `web_search="disabled"`), esvazia os servidores
  MCP do usuário antes de declarar só o `dante_coordenacao`, usa `approvalPolicy` granular que nunca escala o sandbox
  (só aprovações de ferramenta MCP chegam ao cliente) e não herda o ambiente do Worker (allowlist mínima do lançador).
  A sessão só é aceita se o `thread/start` reportar `activePermissionProfile = dante_revisao`, essa política, o cwd e
  um resumo `readOnly` sem rede; versão que não reporta o perfil é recusada. Sem troca de modo nem limpeza.
* **Ambiente dos comandos da revisão** (segunda revisão do PR #317): o processo do Codex mantém as credenciais de que o
  app-server precisa para falar com o provedor, mas os comandos shell não herdam nada dele. A sessão impõe por
  `-c` (que vence o `CODEX_HOME/config.toml` chave a chave) `shell_environment_policy` com `inherit="none"`,
  `exclude=["*"]`, `include_only` = `PATH`, `LANG`, `LC_ALL`, `LC_CTYPE` e `TERM`, `PATH` fixo, sem perfil do shell, e
  `allow_login_shell=false`. Tabelas se mesclam (o `set` do usuário sobrevive), por isso o `include_only`, aplicado
  depois do `set`, é o que descarta o que o usuário injetar. Antes da thread, o driver lê a configuração efetiva
  (`config/read`) e só segue com exatamente essa política: herança, filtros relaxados, shell de login, mecanismo de
  filtro desconhecido, valor do usuário para um nome permitido ou CLI sem `config/read` recusam a revisão, sem
  alternativa mais fraca. Sem a política, a configuração padrão do 0.166 já repassa as chaves ao shell (medido).
* **Autorização restrita do veredito**: `entregar_resultado` continua classificada como não somente leitura
  (`readOnlyHint=false`), mas não destrutiva e de mundo fechado; com essas anotações o Codex 0.166 a executa sem pedir
  aprovação. Se uma versão ou configuração pedir (`mcpServer/elicitation/request`), o driver, só na revisão confinada,
  aceita exatamente `dante_coordenacao`/`entregar_resultado` na thread da própria sessão e recusa qualquer outra
  ferramenta, servidor ou thread, sem levar o pedido a uma pessoa; fora dela, nada muda (`plan` recusa; as demais
  sessões perguntam). Perguntas ao usuário num passo de roteiro recebem resposta vazia (ninguém as responde).
* **Evidência opt-in com o Codex real** (0.166, `DANTE_LIVE_CODEX_SANDBOX=1`, sem chamada de modelo paga: provedor
  Responses local roteirizado e `CODEX_HOME` temporário): num turno real conduzido pelo driver, o shell da revisão não
  encontra `.env` de outro diretório nem do checkout, `~/.codex/auth.json` nem `/srv`; a execução escalonada é recusada
  pela política; escrever é negado e ler a worktree passa; o veredito chega ao caso de uso pelo `dante_coordenacao`
  real, sem nenhum pedido de aprovação. Com um servidor de teste que exige aprovação, a elicitação real chega e o driver
  aceita só `entregar_resultado`. `~/.codex/config.toml` intacto. Com chaves de provedor, proxy autenticado e token falsos no ambiente do
  Codex (o app-server autentica no provedor de teste com a chave), `env`, `printenv` e o `environ` de cada processo do
  sandbox não mostram nenhum valor secreto, tanto com o `CODEX_HOME` de teste quanto com um `config.toml` pessoal
  deliberadamente permissivo; o shell de login é recusado; o veredito chega. Controle negativo: sem a política, a chave
  aparece no `env` já com a configuração padrão.
* **Fundação do `dante_coordenacao`** (decisão E; a #309 a amplia, sem outro servidor nem outro IPC):
  * `ServidorMcpDeCoordenacao`: processo MCP stdio (`--mcp-coordenacao --pipe <nome>`) sem DI, banco nem rede; valida a
    forma das chamadas (ferramenta conhecida, só argumentos declarados, obrigatórios presentes) e encaminha ao Worker;
  * `FerramentasDeCoordenacao` (Worker): um canal por sessão, só para a revisão confinada com escopo; pipe local de nome
    aleatório, só do mesmo usuário, uma conexão, e um **token aleatório da sessão** apresentado antes de qualquer
    chamada (comparação em tempo constante); sem token, o canal fecha sem atender; até 50 chamadas por sessão. O
    **escopo** (execução, etapa de revisão, rodada e hash do diff) é fixado pelo orquestrador ao abrir a sessão; o agente
    nunca o informa. Nenhuma outra sessão recebe este servidor (`Granted` o filtra para todos os demais), exceto as
    sessões de delegação da #309, com perfis próprios no mesmo servidor (ver AD-70, PR 3);
  * ferramentas: `estado_da_execucao` (somente leitura: rodada, limite, hash revisado, vereditos anteriores, instrução
    humana) e `entregar_resultado` (veredito `aprovado | requer_correcoes | bloqueado`, resumo, itens).
* **Veredito estruturado** (`vereditos_de_revisao`, decisão D): `RegistrarVereditoAsync` só aceita a etapa de revisão
  em curso do escopo, na rodada e com o hash do escopo; recusa credencial no resumo ou nos itens e pedido de correção
  sem itens; um por etapa (índice único) e imutável (trigger); repetir a mesma entrega é idempotente (hash do
  conteúdo), outra é recusada. A conclusão do passo usa o veredito gravado, não o que o agente escreve no turno.

Consequências:

* uma versão de grupo é reprodutível pelo hash e por quem a ativou; o que a execução usa é a versão ativa no momento
  em que ela começa, com as versões de agente congeladas no snapshot;
* o gate sobrevive a reinício porque é linha do ledger; o slot de escrita não fica preso a uma decisão humana;
* implementar e revisar são processos Codex distintos com confinamentos distintos; nenhum agente faz commit, push, PR
  ou merge, e a entrega das alterações aprovadas (commit/PR por processo confiável) fica para depois, com o gateway
  #311;
* só a revisão tem confinamento de leitura: a leitura da `/tarefa` (`plan`) e a escrita confinada (#307) continuam
  lendo o disco inteiro, e passar a elas o mesmo perfil fica para uma entrega própria;
* o perfil de permissões depende do Codex 0.166 ou posterior (sem `activePermissionProfile` no `thread/start`, a
  revisão é recusada: o roteiro falha em vez de revisar sem confinamento);
* a forma do roteiro é fechada de propósito; outra forma exige mudar o domínio e esta AD.

Código: `Dante.Domain/Grupos`, `Dante.Application/Grupos`, `Dante.Infrastructure/Modulos/Grupos`, migration
`GruposDeAgentes`, `Dante.Worker/Telegram/TelegramGrupos.cs`; gate: `Dante.Domain/Execucoes/{RoteiroDaExecucao,
ParticipanteDaExecucao, PedidoDeAprovacao, ProgressaoDoRoteiro, VereditoDeRevisao}.cs`,
`Dante.Application/Execucoes/{RoteirosAppService, ReservasDeWorktreeAppService (ReadquirirAsync)}.cs`, migration
`GateDoRoteiro`, `Dante.Worker/Telegram/TelegramExecucoes.cs`; executor: `Dante.Domain/Execucoes/VereditoRegistrado.cs`,
`Dante.Worker/Execucoes/{ExecutorDeRoteirosDaFila, ObservadorDoRoteiro, ExecucaoSobPosse}.cs`,
`Dante.Worker/Coordenacao/{ServidorMcpDeCoordenacao, FerramentasDeCoordenacao}.cs`,
`Dante.Worker/Sessions/{CodexSessionDriver, ClaudeSessionDriver, SessionRegistry, AgenteDaSessao}.cs`,
`Dante.Infrastructure/Contextos/GerenciadorDeWorktreesGit.cs` (`ResumirAlteracoesAsync`), migration `VereditosDeRevisao`.
Testes: `GruposDominioTests`, `CadastroDeGruposAppServiceTests`, `GruposDeAgentesPostgreSqlTests`,
`TelegramAgentesTests` (`/grupo`), `GateDoRoteiroDominioTests`, `GateDoRoteiroPostgreSqlTests`,
`CoordenacaoDoRoteiroTests`, `RoteiroPelaFilaEndToEndTests` e a evidência opt-in
`LiveCodexSandboxEvidenceTests.RevisaoConfinadaDoCodexSoLeEEntregaOVereditoPeloCoordenacao` (`DANTE_LIVE_CODEX_SANDBOX=1`).

## AD-70 — Delegação rastreada entre execuções, com contratos tipados e hierarquia no ledger (#309)

Status: vigente no domínio e na persistência (PR 1 da #309), na Application, fila e executor (PR 2 da #309) e nas
ferramentas do `dante_coordenacao` (PR 3 da #309); etapa 7
do roadmap da Epic #296. Proposta aprovada na #297 (PR #298) e planejamento da #309. Complementa a AD-66 (as colunas de raiz, pai e profundidade adiadas lá entram
aqui), a AD-68 (concessões de delegação na versão do agente) e a AD-69 (a fundação do `dante_coordenacao`, que a #309
amplia sem outro servidor nem outro IPC). Depende do mesmo banco e da mesma chave `Orquestracao:Habilitada`; migration
`Delegacoes`, não aplicada no banco canônico. O PR 2 entrega os casos de uso internos, a fila, o executor, a espera e
a retomada da origem, os prazos e o cancelamento em cascata; o PR 3 expõe as ferramentas MCP no `dante_coordenacao`
existente (perfis por sessão no mesmo servidor, pipe e token) e troca a entrega do resultado pela ferramenta.

Contexto: a delegação precisa ser uma linha rastreada, ligada a uma execução subordinada comum, e não uma mensagem
entre agentes. Para isso o ledger precisa da hierarquia de execuções, de um contrato de entrada e saída que o servidor
valide e de regras que nunca ampliem permissões, aplicadas antes de qualquer processo.

Decisão:

* **Hierarquia no ledger**: `Execucao` ganha `IdExecucaoPai`, `IdExecucaoRaiz` e `Profundidade`, e o tipo `Delegada`.
  Toda execução criada pelo construtor é raiz (sem pai, profundidade 0, raiz = ela mesma); a `Delegada` nasce só pela
  `Delegacao`, filha da execução de origem, com a raiz dela, profundidade + 1, a mesma autoridade (canal e pessoa) e o
  mesmo contexto congelado (quem delega não escolhe repositório, diretório nem dono) e a versão do destinatário como
  responsável. O banco confere tudo isso na inserção (trigger) e recusa alterar hierarquia, tipo e autoridade depois:
  o pai sempre existe antes do filho e a ligação é fixa, então um ciclo na árvore é impossível. Backfill: toda
  execução existente vira raiz de profundidade 0 que aponta para si mesma; a FK da raiz é criada pela migration, fora
  do modelo do EF (uma FK para a própria linha faria o EF ver um ciclo). As colunas não têm default: o trigger completa
  raiz e profundidade só de uma raiz gravada sem elas (SQL escrito para o schema anterior continua válido).
* **Delegação** (`delegacoes`): origem e etapa de origem (FK composta), subordinada (única), raiz, agente e versão
  (com hash) do solicitante e do destinatário, contrato, entrada canônica e hash, objetivo redigido (o bruto nunca é
  gravado; o hash é do objetivo normalizado com a raiz como sal), critérios, prazo para iniciar e duração, cadeia de
  agentes da raiz até o solicitante, chave de deduplicação, estado, resultado canônico com hash, autor (a versão
  destinatária), sessão e hash do artefato lido. Estados `Solicitada → Aceita → EmAndamento → Respondida | Falhou |
  Expirada | Cancelada | Recusada`; Aceita é a validação e o enfileiramento, não o início. Só `Respondida` tem
  resultado, gravado uma vez pelo autor congelado; depois de terminal nada muda. O banco recusa transição inválida,
  alteração de identidade, de pedido ou de qualquer coisa numa delegação terminal, e exclusão.
* **Contratos tipados, catálogo fechado** (`ContratosDeDelegacao`): `consulta.v1` (pergunta e referências →
  resposta, confiança, evidências e pendências; capacidade `pesquisar` ou `revisar`) e `revisao.v1` (hash do artefato,
  foco e referências → veredito, resumo, itens e evidências; capacidade `revisar`; `requer_correcoes` exige itens; o
  resultado vale só para o artefato pedido). Campos declarados no código, schema JSON gerado deles, validação no
  servidor (tipo, tamanho, opções, campos desconhecidos e repetidos recusados) e forma canônica (chaves em ordem, todos
  os campos presentes, texto normalizado), gravada como texto, não jsonb, para o hash ser do conteúdo exato. Nenhum
  campo é caminho: referências são códigos de execução (`E123`, `E123#2`) ou hashes, e a única referência a arquivo
  (evidência) é relativa, sem `.`/`..`, sem barra inicial, `~` ou `\`. Um contrato novo é uma versão nova no catálogo
  e na constraint do banco; o agente nunca define schema.
* **Regras por interseção, no domínio, antes de qualquer gravação** (`Delegacao.Criar`; recusa =
  `DelegacaoRecusadaException` com motivo, nada criado): origem é a etapa de leitura em curso de uma `Tarefa` ou
  `Delegada` (nunca o job legado, um passo de roteiro nem uma etapa de escrita) e o solicitante é o agente congelado
  nela, com versão íntegra; sem escrita; contrato do catálogo; sem autodelegação nem ciclo (o destinatário não está na
  cadeia, cujo tamanho e último elemento são conferidos); profundidade, fan-out (filhas da origem) e orçamento (filhas
  da raiz) dentro dos limites; destinatário em `PodeConsultar` do solicitante, ativo, na versão atual e íntegra, com a
  capacidade do contrato; cada repositório do contexto da origem permitido aos dois agentes; prazo e duração dentro do
  limite do solicitante; entrada válida pelo contrato; objetivo e critérios dentro do tamanho.
* **Escrita proibida nesta etapa**: `Escrita` é sempre falso, recusado no domínio e por check no banco. Delegar
  escrita depende das worktrees (#307) **e** do gateway de aprovação (#311).
* **Concessões na versão do agente**: `FerramentaDoDante.Coordenacao`, `PodeConsultar` (handles, ordenados, sem o
  próprio, exige a concessão `coordenacao`) e `LimitesDeDelegacao` (profundidade, delegações por execução, minutos
  para iniciar, minutos de duração, paralelismo). Teto global aprovado (`LimitesDeDelegacao.Teto`): **profundidade 1,
  4 delegações por execução, 30 min para iniciar, 60 min de duração, paralelismo 1**; a versão só restringe, e nula
  usa o teto. Os checks do banco usam o mesmo teto: elevá-lo exige nova decisão e migration. Sem orçamento de tokens.
  Os embutidos `claude`/`codex` **não** recebem `coordenacao` (continuam com exatamente o que tinham). O cadastro ainda
  não edita a delegação; uma edição herda a da versão atual (nunca a perde em silêncio).
* **Formato do hash da versão do agente**: a delegação entra no JSON canônico só quando a versão a define
  (`PodeConsultar` não vazio ou limites próprios), com `formato` 2 e o objeto `delegacao`; sem ela, o JSON é byte a
  byte o do formato 1. Versões gravadas antes continuam reproduzindo o próprio hash (inclusive a do agente embutido) e
  os grupos não mudam (o hash do grupo não inclui a configuração dos agentes). Conceder só `coordenacao`, sem
  delegação, continua no formato 1. Uma versão adulterada que nem forma configuração válida é não íntegra (antes
  lançava).
* **Idempotência**: a identidade da unidade de trabalho é a chave de deduplicação, SHA-256 de raiz, número da etapa de
  origem, contrato, hash do objetivo normalizado (Unicode NFC, espaços colapsados, minúsculas) e hash da entrada
  canônica. Sem a tentativa (uma retomada da origem acha a mesma delegação) e **sem o destinatário**. A identidade vale
  **em qualquer estado** (revisão do PR #318): repetir para o mesmo agente devolve a delegação existente como está,
  inclusive terminal (o resultado de uma `Respondida`, o estado e o motivo das demais), sem reabri-la nem alterá-la; a
  mesma unidade para outro agente é recusada (`UnidadeJaAtribuida`), também depois de a original terminar. Um índice
  único **não parcial** `(raiz, chave)` garante isso no banco, inclusive sob gravações simultâneas: a segunda falha
  inteira (nem a subordinada fica) e quem perdeu relê a original pela chave. Reexecutar ou reatribuir uma unidade será
  uma operação explícita, auditada e com identidade própria, nunca efeito de a delegação ter terminado.

Decisão do PR 2 (Application, fila e executor):

* **Casos de uso** (`IDelegacoesAppService`): solicitar (com a aceitação na mesma transação), conferir, iniciar,
  registrar progresso, concluir, falhar, expirar, cancelar, consultar, listar por raiz, obter a retomada e retomar
  origens prontas. Neutros de interface: quem chama informa a origem (execução e etapa) e o proprietário; no PR 3 eles
  vêm da sessão autenticada, nunca do texto do agente. Do agente vêm só destinatário, contrato, entrada, objetivo e
  critérios. Toda regra continua no domínio (`Delegacao.Criar` e as transições); a Application não as contorna.
* **Serialização por raiz**: a solicitação trava a raiz e depois a origem (`TravarComAncestraisAsync`, `FOR UPDATE`)
  antes de ler a unidade, contar e inserir, numa transação que grava a subordinada (Delegada, Pendente, uma etapa de
  leitura retomável automaticamente), a delegação Solicitada, os eventos, a aceitação (Aceita = trabalho na fila, chave
  `delegacao:<id>`) e o trabalho. Duas chaves diferentes nunca consomem juntas as últimas vagas (teste com conexões
  simultâneas; com a trava removida, o mesmo teste grava cinco). Tudo ou nada: uma falha ao gravar o trabalho desfaz a
  subordinada e a delegação. O número da subordinada é reservado antes, na transação curta da sequência (uma recusa ou
  repetição deixa só uma lacuna), para não prender a trava global da sequência até o commit. A identidade da unidade
  e a recusa de outro destinatário continuam as do PR 1, agora conferidas sob a trava.
* **Ordem única das travas**: trabalho, depois a execução com os ancestrais (o pai antes da filha). Quem grava a
  subordinada sob posse trava a origem antes dela; a conclusão de etapa trava a execução com os ancestrais; o
  cancelamento pelo dono também. A cascata da origem para as filhas só acrescenta eventos, sem travar filhas. Assim
  pai e filha que terminam ao mesmo tempo nunca perdem a retomada nem entram em deadlock.
* **Filha na fila da #300**: a subordinada é uma execução comum na mesma fila (lease, heartbeat, geração como fencing,
  backoff, tentativas, devolução de boots anteriores), sem segunda fila. Toda gravação do executor é sob posse
  (`GravarComPosseAsync`): posse antiga não inicia, não registra progresso, não falha nem conclui. A etapa é de
  leitura com retomada automática: depois de lease vencido ou reinício, nova tentativa; a delegação continua
  EmAndamento e a duração conta do primeiro início.
* **Executor** (`ExecutorDeDelegacoesDaFila`, tipo Delegada): antes de qualquer processo confere destinatário ativo e
  ainda na versão congelada, versão íntegra com o hash gravado, concessão de coordenação do solicitante, contrato,
  entrada canônica com o hash, autoridade (canal e pessoa) e raiz, capacidade, repositórios na interseção, só leitura,
  modelo no catálogo e prazos; recusa termina a delegação (Falhou, ou Expirada por prazo) sem processo. Abre uma sessão
  reservada da versão destinatária em plan, no contexto congelado da origem, sem o ambiente do Worker e sem servidores
  de coordenação (no PR 3, com o `dante_coordenacao` só no perfil `delegacao`, para entregar o resultado); o pedido entra entre delimitadores de dado não confiável, com o schema de saída. A escrita continua
  impossível: a concessão de coordenação não a autoriza, e worktrees da #307 exigirão autorização própria.
* **Resultado** (substituído no PR 3 pela ferramenta `entregar_resultado`): até o PR 3, o JSON do contrato vinha num
  bloco da última mensagem do turno. O servidor valida pelo
  contrato (campos, opções, tamanhos, desconhecidos recusados), recusa aparência de credencial e confere que cada
  evidência de arquivo existe no repositório do contexto; inválido recebe um pedido de correção e, de novo inválido, a
  delegação falha. A gravação exige a sessão vinculada sob posse à etapa da subordinada (outra sessão, outra geração
  ou estado terminal são recusados), dentro da duração (depois dela, Expirada), e conclui a subordinada, os eventos e a
  retomada na transação que termina o trabalho. Repetir o mesmo conteúdo é idempotente; o original não muda.
* **Origem**: a origem pede durante o turno; ao concluir a etapa, com delegações dela abertas, passa a
  `AguardandoDelegacoes` (identidade, etapas e proveniência intactas); com todas terminais, é retomada na hora. A
  subordinada que termina por qualquer caminho (resultado, falha do executor, prazo, cancelamento, tentativas
  esgotadas) leva a delegação ao terminal correspondente e, se a origem espera e nenhuma delegação daquela etapa
  continua aberta, retoma a origem na mesma transação: etapa seguinte de leitura, origem Pendente, evento
  `OrigemRetomada` e trabalho `retomar-origem:<origem>:<etapa>` (idempotente pelo estado travado e pela chave). A
  recuperação na inicialização também retoma, pelo ledger, origens que esperam sem delegação aberta, sem executar a
  filha de novo. A retomada roda pelo executor da Tarefa a partir do ledger (versão e contexto congelados, objetivo
  redigido, desfechos como dado não confiável), numa sessão nova em plan, e a resposta vai ao dono depois de gravada.
* **Prazos**: o prazo para iniciar é conferido a cada reivindicação e no início sob posse; a duração máxima, por um
  temporizador local a partir do início gravado e de novo na gravação do resultado. Excedidos: delegação Expirada e
  subordinada Cancelada com o motivo.
* **Cancelamento**: o da delegação é o cancelamento durável da subordinada (#305), pelo ledger e pela fila: sem dono,
  na hora; com dono, quem tem a posse o honra. A origem que termina (cancelada, falha, concluída) pede o cancelamento
  de cada subordinada aberta com o evento durável; nada é encerrado por fora do ledger.
* **Eventos**: `DelegacaoSolicitada`, `DelegacaoTransitou`, `DelegacaoProgrediu` e `OrigemRetomada`, sempre na
  origem e só com metadados (delegação, subordinada, destinatário, contrato, estados, marco); nunca entrada, objetivo
  nem resultado.
* **Limitação**: `revisao.v1` é recusado na solicitação até existir um artefato que o executor confira pelo hash; a
  retomada usa o ambiente mínimo, não o do repositório; o prazo para iniciar de uma delegação na fila só é aplicado
  quando ela é reivindicada (com o Worker parado, nada expira até ele voltar).

Consequências:

* o ledger representa árvores de execução com proveniência raiz → origem → etapa → delegação → subordinada → versões,
  e o banco a protege mesmo de gravações por fora do domínio;
* o executor (PR 2) e as ferramentas (PR 3) só criam e conduzem delegações por estes métodos, e o resultado chega
  validado pelo contrato; o PR 3 trocou a fonte do resultado (a ferramenta no lugar do bloco da última mensagem) e a da
  identidade de quem pede (a sessão), sem mudar os casos de uso;
* o índice protege a mesma chave e a trava da raiz protege fan-out e orçamento: as contagens informadas ao domínio são
  lidas sob a trava;
* profundidade 1 e paralelismo 1 estão fixados no código e no banco; com eles, fan-out e orçamento da raiz coincidem
  (a origem de toda delegação é a raiz);
* `FerramentaDoDante.Coordenacao` concedida pelo cadastro, com alguém em `PodeConsultar`, dá à etapa de leitura da
  `/tarefa` daquele agente o perfil origem do `dante_coordenacao` (PR 3); sem ela, nada muda.

Decisão do PR 3 (ferramentas no `dante_coordenacao`):

* **Mesmo servidor, outro perfil**: nada de servidor, IPC ou autenticação novos. O `ServidorMcpDeCoordenacao` ganha
  catálogos por perfil (`revisao` da #308, `origem` e `delegacao`), escolhidos pelo Worker no argumento `--perfil` ao
  declarar o servidor à sessão (sem ele, a revisão, com os argumentos da #308). O perfil é do **canal** da sessão
  (`FerramentasDeCoordenacao`), fixado pelo orquestrador junto com o escopo (`EscopoDeCoordenacao`); cada chamada é
  validada de novo pelo perfil do canal, então o argumento só decide o que a CLI lista. Pipe exclusivo, token da sessão,
  uma conexão e 50 chamadas por sessão continuam os da AD-69.
* **Quem recebe**: só a sessão reservada (com observador) em plan. Perfil `origem`: a etapa de leitura da `/tarefa`
  cuja versão congelada concede `coordenacao` e tem alguém em `PodeConsultar` (`SolicitacaoDaTarefa` coloca o escopo
  com a posse da etapa e lista os agentes consultáveis nas instruções); escrita confinada, revisão confinada, conversa
  comum, retomada da origem e versões sem a concessão nunca. Perfil `delegacao`: a sessão da subordinada, aberta pelo
  executor (a concessão é de quem delega, não de quem responde). O `SessionRegistry` só concede o servidor quando o
  perfil conferido pelo provedor é o do escopo.
* **Ferramentas**, com os nomes do desenho aprovado (DELEGATION): na origem, `consultar_agente` (destinatário,
  contrato `consulta.v1`, objetivo, entrada do contrato e critérios; nunca execução, etapa, dono, repositório,
  permissões, prazos ou escrita) e `estado_da_execucao` (somente leitura: as delegações desta execução com estado,
  último marco de progresso, motivo e o resultado estruturado da `Respondida`); na subordinada, `entregar_resultado`
  (o objeto do schema de saída do contrato).
* **Autorização por sessão, dono e escopo**: `consultar_agente` usa `SolicitarSobPosseAsync`: a gravação é a da posse
  do trabalho da origem (`GravarComPosseAsync`, mesma ordem de travas; geração antiga, lease vencido ou etapa já
  concluída não criam nada) e só da sessão vinculada sob posse à etapa de origem, do mesmo dono e canal; sem vínculo
  ou com outra sessão, `SessaoNaoAutorizada`. As demais regras, a trava da raiz e a identidade da unidade são as dos
  PRs 1 e 2. `estado_da_execucao` usa `AcompanharDaOrigemAsync` com o dono e a execução do escopo (outro dono: vazio).
* **Resultado pela ferramenta**: o bloco JSON da última mensagem deixou de valer. `entregar_resultado` valida
  (contrato, credencial e evidências de arquivo existentes no repositório do contexto) e grava **na hora**, sob a posse
  da subordinada e pela sessão vinculada à etapa, com o marco `ResultadoRecebido`, a conclusão da subordinada e a
  retomada da origem (`ConcluirAsync`): o resultado é durável antes de o turno terminar. Recusado, o agente recebe o
  motivo e entrega de novo no mesmo turno. Repetir o mesmo conteúdo (forma canônica) é idempotente; outro, recusado.
  Entrega e encerramento pelo executor (prazo, falha) são serializados (`EntregaPelaFerramenta`): o primeiro grava e o
  outro encontra a etapa concluída. Sem entrega ao fim do turno, um lembrete; ainda sem, a delegação falha (texto
  livre nunca é resultado). Uma sessão parada perde o canal com a posse.
* **Aprovação**: `consultar_agente` e `entregar_resultado` são de mundo fechado e validadas pelo Worker contra o
  escopo, então rodam sem pedir aprovação também em plan: no Claude, entram no `--allowedTools` (`PreAuthorizedTools`
  do servidor); no Codex, a elicitação MCP em plan é aceita só para essas ferramentas do `dante_coordenacao`, na thread
  da própria sessão (outra ferramenta, servidor ou thread continuam recusados). As de leitura seguem a regra anterior.

Limitações do PR 3: só a primeira etapa da `/tarefa` delega (a retomada não recebe o servidor; o resultado já vai no
prompt dela, como dado); o aviso de conclusão da primeira etapa ao dono não distingue a espera pelas delegações; o
token do canal aparece no ambiente do processo MCP (como na #308), protegido pelo pipe de uma conexão só do mesmo
usuário; não há evidência opt-in com as CLIs reais para as ferramentas novas (os nomes e a aceitação seguem o formato
da elicitação do Codex 0.166 já medido na #308). Continua exigindo banco e `Orquestracao:Habilitada` (desligado no
homelab); migrations inalteradas.

Código do PR 3: `Dante.Worker/Coordenacao/{ServidorMcpDeCoordenacao, FerramentasDeCoordenacao, EscopoDeCoordenacao}.cs`,
`Dante.Worker/Execucoes/{EntregaPelaFerramenta, ExecutorDeDelegacoesDaFila, ExecucaoSobPosse}.cs`,
`Dante.Worker/Sessions/{AgentToolServer, AgenteDaSessao, SessionRegistry, SolicitacaoDaTarefa, ClaudeSessionDriver,
CodexSessionDriver}.cs`, `Dante.Application/Execucoes/{IDelegacoesAppService, DelegacoesAppService}.cs`
(`SolicitarSobPosseAsync`, `AcompanharDaOrigemAsync`). Testes: `CoordenacaoDaDelegacaoTests` (catálogo por perfil,
concessão, escopo, autorização, idempotência e serialização da entrega, elicitação do Codex e `--allowedTools` do
Claude), `DelegacoesExecucaoPostgreSqlTests` (sessão vinculada, fencing e acompanhamento) e
`DelegacaoPelaFilaEndToEndTests` (origem pede e filha entrega pelo canal real, com o Worker parado no meio).

Código do PR 2: `Dante.Application/Execucoes/{IDelegacoesAppService, DelegacoesAppService, CicloDaDelegacao}.cs`,
`Contratos/DelegacaoDto.cs`, as travas em `IExecucaoRepository.TravarComAncestraisAsync`, o ciclo em
`FilaDeExecucoesAppService` e `ControleDeExecucoesAppService`, a recuperação em `RecuperacaoDeExecucoesAppService`,
`Dante.Worker/Execucoes/{ExecutorDeDelegacoesDaFila, ExecutorDeTarefasDaFila, ExecucaoSobPosse}.cs`. Testes:
`DelegacoesExecucaoPostgreSqlTests` (regras, concorrência real, rollback, fencing, recuperação, retomada, prazos,
cancelamento) e `DelegacaoPelaFilaEndToEndTests` (ProcessadorDaFila real, interrompido no meio da filha e da retomada).

Código do PR 1: `Dante.Domain/Execucoes/{Delegacao, ContratoDeDelegacao, EstadoDaDelegacao, Execucao, TipoDeExecucao}.cs`,
`Dante.Domain/Agentes/{LimitesDeDelegacao, ConfiguracaoDoAgente, VersaoDoAgente, Agente, FerramentaDoDante}.cs`,
`Dante.Application/Execucoes/Portas/IDelegacaoRepository.cs`, `Dante.Application/Agentes/{AgentesEmbutidos,
CadastroDeAgentesAppService}.cs`, `Dante.Infrastructure/Modulos/Execucoes/{DelegacaoDbMapping, DelegacaoRepository,
ExecucaoDbMapping}.cs`, `Dante.Infrastructure/Modulos/Agentes/VersaoDoAgenteDbMapping.cs`, migration `Delegacoes`.
Testes: `DelegacoesDominioTests`, `CompatibilidadeDosHashesTests` (hashes calculados pelo código anterior à #309) e
`DelegacoesPostgreSqlTests` (migration sobre dados do schema anterior, constraints, triggers, imutabilidade, exclusão
e deduplicação concorrente).

## AD-71 — Worktree por execução de escrita, conjunto fechado de comandos Git e escrita só com sandbox de SO (#307)

Status: vigente, etapa 5 do roadmap da Epic #296. Proposta aprovada na #297 (PR #298); decisões de escopo revisadas na
#307. Complementa a AD-58 sem substituí-la para os fluxos existentes, a AD-67 (reserva e liberação sob posse) e a
AD-68 (a `/tarefa` passa a ter um pedido explícito de escrita). Altera o limite "worktrees automáticos" do
PROJECT_CONTEXT. Depende do mesmo banco e da mesma chave `Orquestracao:Habilitada`; migration `ReservasDeWorktree`,
não aplicada no banco canônico.

Contexto: execuções novas eram só de leitura porque não havia onde escrever com segurança. Escrever exige isolar o
checkout do usuário, impedir que duas execuções disputem o mesmo diretório, sobreviver à morte do Worker sem duplicar
nem reaproveitar diretório ocupado e, sobretudo, não confundir worktree com sandbox: uma worktree é só outro diretório.
A investigação mostrou que o Codex CLI (0.166) confina por mecanismo do SO (`workspace-write`) e reporta a política
efetiva no `thread/start`, e que o fluxo atual do Claude CLI não tem confinamento de filesystem (modos de permissão não
são sandbox).

Decisão:

* **Pedido explícito**: `/tarefa <handle> @alias escrever <texto>` é o único pedido de escrita (sem o marcador e sem o
  `@alias` explícito, nada muda: leitura em `plan`). `MontagemDaTarefa.MontarEscrita` exige versão com `implementar` e
  escrita, perfil efetivo = perfil padrão congelado da versão e acima de `plan` (sem escalonamento), modo repositório e
  driver com confinamento comprovado (`ConfinamentoDeEscrita`). A etapa é `Escrita`, retomada `ComConfirmacao`: escrita
  não é idempotente, e o domínio recusa etapa de escrita de execução nova com retomada automática.
* **Uma worktree por repositório envolvido**: o principal e cada vinculado explícito (AD-58), todos ligados à mesma
  execução e etapa. Layout `<raiz>/<alias>/E<n>` (raiz `Orquestracao:Worktrees:Raiz`, padrão `~/.dante/worktrees`),
  branch `dante/<handle>/e<n>` a partir do `HEAD` do checkout principal no momento do planejamento (SHA gravado). A
  sessão recebe só as worktrees: a principal como diretório de trabalho, as vinculadas como raízes graváveis. O
  checkout principal nunca é fallback.
* **`ReservaDeWorktree` no ledger** (`reservas_de_worktree`): execução, etapa, alias, principal, caminho canônico do
  checkout e da worktree, branch, SHA base, host e estado `Reservada → Pronta → Liberada → Removida | Preservada`, mais
  `Falhou`. É a única entidade do domínio com caminhos: ela é o registro de um recurso do host e precisa guardar os
  caminhos exatos, porque derivá-los de configuração mutável poderia dar como removida uma worktree que ainda existe
  (exceção explícita no teste de arquitetura). Eventos `Worktree*` levam alias, branch e estado, nunca caminho.
* **Reserva atômica sob posse**: `IReservasDeWorktreeAppService.ReservarAsync` grava todas as reservas da execução
  (todas ou nenhuma) dentro de `GravarComPosseAsync`, sob `pg_advisory_xact_lock` que serializa processos e Workers
  entre a contagem e a inserção; índices únicos parciais entre as ativas (caminho; repositório + branch; execução +
  alias) são a segunda barreira, e um check recusa caminho igual ao do checkout. Nova tentativa ou nova geração da
  mesma execução recebe as mesmas reservas (nunca uma segunda worktree); planejamento divergente ou caminho de reserva
  encerrada é recusado.
* **Limites no banco, não em memória**: global por host e por repositório (checkout), contando execuções com reserva
  ativa que podem escrever (interrompida e bloqueada mantêm a reserva, mas não contam). Excedido, nada é gravado e o
  processador devolve o trabalho **sem contar tentativa** (espera `EsperaPorCapacidade`). O código suporta limites
  maiores (exercitados em teste), mas a composição do Worker recusa `LimiteGlobal > 1`: elevar o limite exige alteração
  e autorização operacional próprias, depois da ativação da contenção estrita da AD-63, que trata do tempo de vida dos
  processos e não substitui nem a reserva atômica nem o sandbox de filesystem.
* **`GerenciadorDeWorktreesGit` (Infrastructure)**: conjunto fechado e conferido antes de cada processo —
  `worktree add|remove|list --porcelain` e consultas somente leitura (`rev-parse`, `check-ref-format`, `symbolic-ref`,
  `status`, `rev-list`) —, `ArgumentList`, sem shell, `core.fsmonitor=false`, `core.hooksPath=/dev/null`, prazo, nunca
  `--force`, sem `prune`. Antes de entregar ao agente: caminho real igual ao planejado (sem link em nenhum componente da
  raiz, do diretório do alias ou da worktree), raiz fora de qualquer checkout cadastrado, raiz Git = a worktree,
  diretório Git comum = o do repositório, branch da execução e worktree listada pelo próprio Git. Diretório ocupado,
  link, traversal, branch existente fora da base ou repositório sem commit recusam sem apagar nada. A worktree de uma
  tentativa anterior é adotada só se for exatamente a planejada.
* **Executor fail-closed**: depois das conferências da AD-68, o `ExecutorDeTarefasDaFila` planeja, reserva, cria ou
  adota e valida cada worktree, marca `Pronta` sob posse, valida de novo e só então confere `GuardaDeEscrita` com as
  worktrees (todas prontas, da própria execução, uma principal, nenhuma igual a um checkout). Qualquer falha marca a
  worktree culpada `Falhou` (diretório preservado para conferência), libera as demais e falha a etapa sem sessão.
* **Escrita só com sandbox de SO, por provider**: a sessão de escrita é reservada e confinada (`EscritaConfinada`).
  Codex: `sandbox=workspace-write` com as worktrees como únicas raízes graváveis, `/tmp` e `TMPDIR` excluídos, rede
  desligada, `approvalPolicy=never` (sem aprovação interativa nem escalonamento antes do gateway #311), nenhum servidor
  de ferramentas (eles rodam fora do sandbox; `mcp_servers={}` também esvazia os do usuário) e os checkouts principais
  declarados não confiáveis (senão o Codex grava a confiança deles em `~/.codex/config.toml` e carregaria configuração
  de projeto deles). O driver só aceita a sessão se o `thread/start` reportar exatamente essa política; troca de modo e
  limpeza de contexto são recusadas. O diretório Git comum fica fora das raízes: commit, alteração de refs e o arquivo
  `.git` da worktree são negados pelo sandbox, então o agente entrega alterações não commitadas. Claude: escrita
  recusada na montagem, no `SessionRegistry` e no driver, antes de qualquer processo, até existir sandbox de SO
  implementado e comprovado; instrução no prompt não é garantia.
* **Liberação e limpeza**: toda transição terminal da execução (conclusão, falha, cancelamento com ou sem posse,
  tentativas esgotadas, não iniciada) libera as reservas ativas na mesma transação. A limpeza física, depois de cada
  etapa de escrita e na recuperação da inicialização, só toca reservas liberadas do próprio host: remove (`worktree
  remove`, sem `--force`) a worktree limpa e sem commits além da base e preserva, com motivo no ledger, a que tem
  alterações, commits ou estado ilegível. Branch nunca é apagada. Diretório sob a raiz sem reserva é relatado, nunca
  removido; falha de remoção fica para a próxima passada.

Complemento (#308, AD-69): uma reserva pode ficar `AguardandoDecisao` enquanto um Roteiro espera a decisão humana:
ativa para os índices únicos e fora da limpeza, mas fora da capacidade; volta a `Pronta` só por `ReadquirirAsync`, sob
a mesma trava e os mesmos limites.

Complemento (#311, AD-72): o conjunto fechado ganhou a única escrita Git do D.A.N.T.E., o commit local do artefato
aprovado de um roteiro. Ele usa:
- `hash-object -w --no-filters --stdin` para os bytes lidos pelo D.A.N.T.E., confinado pelo kernel à worktree
  (`openat2` a partir de um handle da raiz, sem links; ver AD-72);
- `read-tree`, `update-index -z --index-info` e `write-tree` num index privado;
- `commit-tree` e `update-ref` da branch da execução com o valor antigo;
- `read-tree <árvore>` do index da worktree, só depois do commit.

Só leem: `ls-tree`, `ls-files --others`, `cat-file blob`, `diff` entre duas árvores, `diff-index --cached` e
`config --get-regexp` dos filtros de conteúdo. Nenhum desses comandos lê arquivo da worktree (`SemLeituraDaWorktree`).
Tudo isso só depois da aprovação do gateway, só na worktree e sem push. Nenhuma variável `GIT_*` herdada chega aos
processos Git.

Consequências:

* a `/tarefa` escreve de verdade, mas só com Codex, só em worktrees e só deixando alterações não commitadas; commit,
  push e PR a partir delas ficam para um processo confiável do D.A.N.T.E. sob política de aprovação, em entrega
  posterior;
* uma execução interrompida durante a escrita fica `Interrompida` com a worktree reservada; como o pedido da tarefa
  só existe em memória, ela só sai por cancelamento do dono, que libera a reserva;
* a limpeza física depois de um cancelamento sem posse (tarefa na fila ou interrompida) acontece na próxima passada
  (outra etapa de escrita ou reinício);
* o processo da CLI continua fora do sandbox e mantém o próprio estado em `~/.codex`; o sandbox confina os comandos
  do agente, não a CLI;
* arquivos ignorados pelo Git (por exemplo `.env` locais) não existem na worktree; a remoção apaga os ignorados que o
  agente tiver criado numa worktree sem alterações rastreadas;
* sem `prune`, metadados de worktree cujo diretório sumiu por fora são limpos pelo `worktree remove` da própria
  reserva ou pelo `gc` do Git.

Código: `Dante.Domain/Execucoes/{ReservaDeWorktree, EstadoDaReservaDeWorktree}.cs`,
`Dante.Application/Execucoes/{ReservasDeWorktreeAppService, LiberacaoDeWorktrees, GuardaDeEscrita}.cs`,
`Dante.Application/Execucoes/Portas/{IReservaDeWorktreeRepository, IGerenciadorDeWorktrees}.cs`,
`Dante.Application/Agentes/{ConfinamentoDeEscrita, MontagemDaTarefa, PlanoDaTarefa}.cs`,
`Dante.Infrastructure/Contextos/GerenciadorDeWorktreesGit.cs`, `Dante.Infrastructure/Modulos/Execucoes/ReservaDeWorktree*`,
migration `ReservasDeWorktree`, `Dante.Worker/Execucoes/{ExecutorDeTarefasDaFila, ProcessadorDaFila, ExecucaoSobPosse}.cs`,
`Dante.Worker/Sessions/{CodexSessionDriver, ClaudeSessionDriver, SessionRegistry, SolicitacaoDaTarefa, AgenteDaSessao}.cs`,
`Dante.Worker/Telegram/TelegramPollingService.Agentes.cs`.
Testes: `GerenciadorDeWorktreesGitTests`, `EscritaEmWorktreeTests`, `ReservasDeWorktreePostgreSqlTests`,
`TarefaPelaFilaEndToEndTests` (escrita), `CodexSessionDriverTests` (confinamento) e a evidência opt-in
`LiveCodexSandboxEvidenceTests` (`DANTE_LIVE_CODEX_SANDBOX=1`, Codex real, sem chamada de modelo).

## AD-72 — Aprovação de ação protegida vinculada ao hash do payload, com catálogo fechado e execução única (#311)

Status: vigente no domínio e na persistência (PR 1 da #311), no gateway na Application, com as ferramentas do
`dante_coordenacao` pelo catálogo e o adaptador de decisão do Telegram (PR 2), e na entrega do artefato do roteiro,
primeiro consumidor (PR 3); etapa 9 do roadmap da Epic #296. Proposta da AD-72 em
[ADRS_PROPOSTAS](../architecture/multi-agent-platform/ADRS_PROPOSTAS.md), refinada e aprovada na própria #311 (entidade
própria, primeiro consumidor por commit local, identidade Git explícita, dois passos humanos). Complementa a AD-22 (as
approvals da CLI continuam separadas) e a AD-69/AD-75 (o gate de etapa continua não sendo aprovação de ação). Depende do
mesmo banco e da mesma chave `Orquestracao:Habilitada`; migration `AprovacoesDeAcao`, não aplicada no banco canônico.
O PR 1 não expõe nada em tempo de execução; o PR 2 põe as ferramentas atuais no gateway, sem mudar comportamento, e
expõe `/acoes` com a chave ligada, sem nenhuma ação protegida pedida ainda; o PR 3 expõe a entrega
(`/execucao entregar`), atrás da mesma chave e da identidade Git configurada, sem migration nova.

Contexto: até a #310, nenhum fluxo de orquestração tinha ação protegida. O gate de etapa (`PedidoDeAprovacao`) decide um
artefato e, por decisão travada, não autoriza commit, push nem outra ação. A entrega do artefato aprovado de um roteiro
(e, depois, push, PR e as ações de agentes) precisa de uma aprovação que autorize **exatamente uma execução de
exatamente um payload**, sobreviva a reinício e não possa ser reaproveitada.

Decisão:

* **Entidade própria** (`AprovacaoDeAcao`, tabela `aprovacoes_de_acao`), separada do gate de etapa: não reutiliza
  `pedidos_de_aprovacao` nem os triggers dele.
* **Catálogo fechado** (`CatalogoDeAcoes`): cada ação tem nome (`servidor.ferramenta` ou `dante.acao`), classe de risco
  (`Leitura`, `EscritaInterna`, `EfeitoExterno`), política (`Nunca`, `ConformeModo`, `Sempre`), se exige um repositório
  alvo, e parâmetros tipados, todos obrigatórios. Ação fora do catálogo não existe: não há payload, pedido nem execução.
  As ferramentas atuais do `dante_coordenacao` (`estado_da_execucao`, `entregar_resultado`, `consultar_agente`,
  `propor_plano`, `entregar_sintese`) entram com política `Nunca`, sem mudar comportamento; a única ação protegida é
  `dante.entregar_artefato_do_roteiro` (`EscritaInterna`, `Sempre`), com artefato (hash do diff aprovado no gate),
  base, branch, mensagem e identidade explícita do autor. `ConformeModo` depende de quem conhece o modo da sessão
  (o gateway, PR 2); nenhuma ação usa essa política ainda.
* **Payload canônico com hash do servidor** (`PayloadDaAcao`): ação, alvo (`@alias`), capacidades (ordenadas e sem
  repetição), escopo, execução, etapa, formato, parâmetros e solicitante. O JSON tem as chaves em ordem ordinal e
  nenhum espaço, e o texto é normalizado (NFC, `\n`, sem pontas). Campo desconhecido, repetido, ausente ou fora da
  forma é recusado com o motivo, inclusive um hash informado. O hash é o SHA-256 dos bytes UTF-8, calculado no domínio
  e nunca recebido de fora. O solicitante é o dono da raiz (por um comando dele) ou a versão congelada de um agente
  (versão e hash da configuração), nunca um argumento.
* **Escopo `UmaVez` e validade obrigatória** (padrão de 24 h): nenhum escopo por execução, global ou permanente nesta
  fase.
* **Decisão única do dono da raiz.** Aprovar exige o hash exibido (ao menos 12 caracteres do início) e só vale antes
  de `expira_em`. Recusar não depende do hash nem do prazo. Expirar só vale depois do prazo. A revogação
  (`Cancelada`, com motivo) só ocorre antes de executar.
* **Execução única**:
  * quem executa remonta o payload com os fatos atuais (versão do agente, capacidades, conteúdo do artefato);
  * se o hash for diferente, a aprovação é cancelada sem executar; se o prazo venceu, ela expira;
  * a transição `Aprovada → EmExecucao` gravada sob o `xmin` da linha é o CAS: de duas tentativas concorrentes, só
    uma grava;
  * o desfecho é `Executada` (com resultado), `Falhou` (comprovadamente sem efeito) ou `Incerto` (interrompida sem
    saber se teve efeito, com o estado observado). `Incerto` é terminal: nunca é repetida automaticamente.
* **O banco confere de novo**:
  * `hash = encode(sha256(convert_to(payload, 'UTF8')), 'hex')`;
  * o payload preso às colunas: ação, execução, etapa, alvo, escopo, formato e solicitante (dono ou versão e hash),
    com tipo explícito: `dono` exige o canal e o usuário do dono, sem versão; `agente` exige a versão (chave ausente
    não passa pelo check);
  * a ação e o risco entre os protegidos do catálogo;
  * a etapa é da própria execução (chave composta), e a versão do solicitante existe;
  * no trigger `proteger_aprovacao_de_acao`:
    * nasce pendente e do dono da execução raiz;
    * o objeto `solicitante` do payload é exatamente o esperado, sem chave a mais: o dono da raiz (`canal`, `tipo =
      dono`, `usuario`) sem versão nem hash, ou o agente (`hash`, `tipo = agente`, `versao`) cuja versão referenciada
      tem em `versoes_de_agente.hash_da_configuracao` o mesmo hash (revisão do PR #324);
    * as transições da tabela do domínio, com todo UPDATE sendo uma transição;
    * o decisor é o dono;
    * aprovar e iniciar só antes do prazo, expirar só depois;
    * pedido, decisão e início da execução imutáveis; nenhum DELETE.
  * um índice único parcial impede um segundo pedido do mesmo payload enquanto ele está pendente, aprovado, em
    execução, executado ou incerto: replay por pedido novo é recusado.

* **Gateway das ferramentas de sessão** (`GatewayDeFerramentas`, PR 2), sem estado e sem banco, na Application. Cada
  chamada do `dante_coordenacao` passa por cinco passos:
  1. identidade só do canal (sessão, servidor e as ferramentas que o perfil da sessão concede, fixados ao abrir);
  2. catálogo fechado pelo nome `servidor.ferramenta`;
  3. concessão do perfil;
  4. schema da ferramenta (o do adaptador MCP, aplicado aqui);
  5. política.

  `Nunca` executa uma vez pelo caso de uso da própria ferramenta. Qualquer outra política é recusada: ação protegida
  só existe por pedido de aprovação, nunca por chamada direta. Desconhecida e não concedida têm a mesma recusa da
  validação anterior ("Ferramenta desconhecida."). Sem o gateway, o canal responde indisponível (fail-closed).

  A concessão é a do perfil fixado na abertura da sessão: as versões são imutáveis, e as ferramentas `Nunca` não
  releem o banco. Nenhum evento novo vai ao ledger por chamada: o efeito de cada ferramenta já é gravado pelo caso
  de uso dela, e a auditoria da chamada fica no log do Worker, sem argumentos.
* **Caso de uso das aprovações** (`IAprovacoesDeAcaoAppService`, PR 2), neutro de interface:
  * o pedido passa pelo catálogo e pela política;
  * o solicitante é o dono pelo próprio comando (conferido contra a raiz) ou o agente pela etapa da sessão (versão
    congelada da etapa ou, sem ela, da execução), nunca por argumento;
  * o mesmo payload vigente devolve o pedido existente;
  * decisão de outro usuário é tratada como inexistente; decisões simultâneas: uma grava (`xmin`), a outra vê o
    pedido decidido;
  * **revogação**: o agente solicitante deixou de estar ativo, ou a versão atual dele não é mais a que pediu (versão
    nova = concessões alteradas). Na decisão, ou antes de executar, cancela;
  * **execução** pelo consumidor da ação (`ExecucaoDaAcaoProtegida`: remontar o payload e executar), sem registro
    global de executores nesta entrega:
    * payload impossível de remontar cancela sem executar;
    * o início é gravado antes da ação (CAS); quem perde não executa;
    * exceção ou cancelamento durante a ação viram `Incerto`;
    * o desfecho é gravado sem o token de cancelamento;
  * motivos, resultados e parâmetros exibidos são redigidos (`ProtecaoDeSegredos`) e cortados; o payload bruto não
    sai da Application.
* **Expiração e recuperação** (PR 2): o serviço de manutenção do Worker roda na partida, antes do polling:
  `EmExecucao` vira `Incerto` (o processo que executava não existe mais) e os vencidos expiram. Depois, expira a cada
  minuto. O dono é avisado. Pressupõe um Worker por banco (paralelismo 1).
* **Adaptador de decisão do Telegram** (PR 2): `/acoes` lista os pedidos em aberto do ledger com resumo redigido,
  hash curto, prazo e botões. O botão (`ac:<id>:<hash curto>:a|r`) leva só o pedido e o hash exibido. Sem teclado,
  `/acoes aprovar|recusar <id>`. Nenhuma regra de autorização no adaptador.

* **Entrega do artefato aprovado do roteiro** (PR 3), primeiro consumidor, em dois passos humanos separados:
  1. o gate do roteiro aprova o artefato (AD-69), e só ele;
  2. `/execucao entregar E… [mensagem]` (`IEntregaDoArtefatoAppService`) pede a ação, e a pessoa aprova o hash dela
     em `/acoes`.

  O pedido:
  * só o dono pede, e só para um roteiro `Concluida` com o pedido do gate `Aprovado`;
  * a worktree principal precisa ser deste host, estar liberada ou preservada (nunca removida), com o HEAD na base, e
    ter o conteúdo atual igual ao hash aprovado no gate;
  * mensagem com aparência de credencial é recusada;
  * o payload leva `artefato` (o hash do gate), `base` (o HEAD atual da worktree), `branch`, `mensagem` (padrão:
    "Artefato aprovado do roteiro E…"), `autor_nome` e `autor_email`, com alvo `@alias`, a etapa do gate e as
    capacidades `git.add` e `git.commit`;
  * uma entrega por execução no caso de uso, com a execução travada (`FOR UPDATE`, na transação do ledger) da leitura
    dos pedidos existentes até a gravação do novo, então pedidos simultâneos nunca criam dois (revisão do PR #326):
    * a executada é informada;
    * a incerta, a em execução e o pedido em aberto com outra mensagem são recusados;
    * a pendente é devolvida;
    * a aprovada e ainda não executada é executada fora da transação. É o caminho depois de um reinício entre a
      aprovação e a execução.

    O gateway continua aceitando payloads diferentes para a mesma execução: a semântica da PR 2 não muda.

  A identidade Git é explícita e obrigatória (`Orquestracao:Entrega:AutorNome` e `AutorEmail`), nunca herdada do Git
  do host, do repositório ou do agente. Ausente ou inválida, a entrega é recusada com o motivo, sem pedido; o resto
  do Worker sobe normalmente.

  Depois da aprovação (o adaptador chama a execução logo após aprovar, e a Application decide se há o que executar),
  o gateway remonta o payload com os fatos de agora: o hash do conteúdo atual, o HEAD atual, o autor configurado, a
  reserva e o gate. Qualquer diferença cancela sem executar.
* **Nenhum filtro de conteúdo roda na entrega** (revisão 2 do PR #326). Filtros (`filter.<driver>.clean`, `smudge`
  ou `process`, inclusive o do Git LFS) são comandos externos. O Git os executa só quando converte um arquivo da
  worktree em objeto (`add`, `diff` e `status` contra a worktree, `hash-object` de um caminho) ou um objeto em
  arquivo (`checkout`).

  Uma consulta prévia da configuração seguida desses comandos teria uma janela TOCTOU. Cada processo relê a
  configuração, e um filtro configurado entre a consulta e o comando rodaria. Repetir a consulta só estreita a
  janela.

  Por isso a garantia não depende da configuração: **nenhum processo Git da entrega lê arquivo da worktree**.
  - O D.A.N.T.E. lê os bytes, **confinado pelo kernel à worktree** (abaixo).
  - O Git recebe os bytes pela entrada padrão (`hash-object -w --no-filters --stdin`).
  - A árvore é montada num index privado (`GIT_INDEX_FILE` num diretório temporário), a partir da base: `read-tree`,
    `update-index -z --index-info` e `write-tree`.
  - Todo o resto opera sobre refs e objetos.

  O alcance é o do `add --all`:
  - os rastreados da base: removidos, alterados, modo executável e links;
  - os não rastreados e não ignorados (`ls-files --others`, que lê só nomes de diretório e regras de ignore).

  Submódulos ficam como na base. O index e os arquivos da worktree não mudam até o commit.
* **A leitura dos bytes nunca sai da worktree** (`LeituraConfinadaDaWorktree`, revisão 3 do PR #326). Verificar um
  caminho (`LinkTarget`) e depois abri-lo pelo nome deixa uma janela TOCTOU. Um diretório ou o próprio arquivo
  trocado por um link entre as duas operações faria o D.A.N.T.E. ler, e gravar como blob solto, bytes de fora da
  worktree. A leitura agora não tem verificação separada da abertura:
  - a raiz é aberta uma vez como handle (`O_PATH`), sem link em nenhum componente;
  - cada caminho é resolvido pelo kernel a partir desse handle, numa só operação: `openat2` com `RESOLVE_BENEATH`,
    `RESOLVE_NO_SYMLINKS`, `RESOLVE_NO_MAGICLINKS` e `RESOLVE_NO_XDEV`. Assim, nunca acima da raiz, nenhum link em
    nenhum componente e nenhum outro sistema de arquivos no caminho;
  - o tipo, o modo e o destino de um link são lidos pelo handle aberto (`statx` e `readlinkat` com caminho vazio),
    nunca pelo nome de novo. Um link da worktree é gravado como link (o destino) e nunca é seguido;
  - o conteúdo de um arquivo comum vem de uma segunda abertura, com as mesmas restrições. Ela precisa ser o mesmo
    inode e dispositivo, ainda arquivo comum e com **um único link físico**, porque um hard link poderia apontar para
    um arquivo de fora;
  - um link num componente intermediário, já presente na abertura, é tratado como caminho ausente, como o Git faz.
    Uma troca entre a classificação e a leitura recusa a entrega com o motivo.

  **Nenhum blob é gravado antes de a leitura estar validada.** Todos os caminhos são lidos e conferidos primeiro, e
  só então os bytes vão ao `hash-object`. Uma recusa no meio não deixa blob nenhum, nem de bytes da própria worktree.

  Exige Linux 5.6+ (`openat2`), x86_64 ou arm64. Em outro sistema, a entrega é recusada.
* **A raiz lida é a worktree validada, do começo ao fim da entrega** (revisão 4 do PR #326). O confinamento acima
  vale dentro do handle da raiz. Se a raiz fosse aberta pelo caminho depois da validação, um processo com escrita no
  diretório pai poderia renomear a worktree e pôr outro diretório comum (sem link) no mesmo caminho. O kernel então
  confinaria a leitura ao diretório substituto, e os processos Git, que recebiam o caminho como diretório de trabalho,
  operariam sobre outro repositório. Agora:
  - **abre primeiro, valida depois, sobre o mesmo diretório.** A raiz é aberta como handle antes de qualquer
    validação. A identidade vem só de consultas no próprio handle (revisão 5 do PR #326): a validação pelo nome
    (`ValidarAsync`) não roda mais depois da abertura; roda só quando a abertura falha, para dar o motivo da recusa, e
    sempre termina em recusa. A identidade é conferida no próprio handle: o Git iniciado nele (diretório de trabalho `/proc/<pid>/fd/<raiz>`, que o kernel resolve pelo descritor,
    nunca pelo nome) precisa ver exatamente o caminho da worktree como raiz. Um diretório movido aparece com outro nome;
  - o diretório Git que ele descobre precisa ser o da worktree registrada para este caminho: dentro de
    `<diretório comum>/worktrees`, com `gitdir` apontando de volta para `<caminho>/.git`, no diretório comum do
    repositório cadastrado e na branch da execução;
  - **todo processo Git da entrega**, inclusive os que gravam (`hash-object`, `commit-tree`, `update-ref`,
    `read-tree`) e as consultas depois do commit, roda com o diretório de trabalho no handle e `GIT_DIR` fixo nesse
    diretório Git. Nenhum resolve o nome da worktree nem redescobre o repositório pelo `.git` dela. Trocar o diretório
    no caminho não muda o repositório em que a entrega lê e grava;
  - o nome ainda precisa apontar para o handle (mesmo inode e dispositivo, sem seguir link) depois da validação, antes
    do primeiro blob e imediatamente antes do `update-ref`. Uma troca detectada recusa sem efeito: antes dos blobs não
    fica objeto nenhum, e a branch nunca se move.

  Limite: um diretório posto no caminho **antes** da abertura é validado como a worktree que é. Sem o diretório Git
  da worktree registrada (um diretório comum, ou outra worktree do repositório), a entrega é recusada. Com uma cópia
  do `.git` da worktree, ele é indistinguível dela, como qualquer conteúdo escrito nela. Mesmo assim, só entra no
  commit se o hash for o aprovado.

  Todo comando da entrega passa por `SemLeituraDaWorktree`, um conjunto sem `add`, sem `status` e sem `diff` contra a
  worktree, e roda com `GIT_NO_LAZY_FETCH=1`, para que nenhum clone parcial busque objetos pelo transporte do remoto.

  **Configuração que continua ativa** nos processos da entrega: a do sistema, a global, a do repositório e a da
  worktree, como em qualquer Git. Por cima delas, `-c` força `core.fsmonitor=false`, `core.hooksPath=/dev/null`,
  `gc.auto=0` e `maintenance.auto=false`.

  Nenhuma chave dessa configuração faz os comandos do conjunto executarem processo:
  - `diff` entre árvores roda com `--no-ext-diff` e `--no-textconv`;
  - `commit-tree` roda com `--no-gpg-sign`;
  - hooks estão desligados;
  - nada converte arquivo.

  A configuração não é restringida porque não precisa ser. O Git não tem como ignorar a do repositório, e o desenho
  não depende dela.

  A consulta `config --get-regexp` dos filtros continua no começo do pedido, da remontagem e do commit. Ela é só uma
  recusa explicativa: num repositório com filtro, o hash aprovado no gate foi lido com o filtro aplicado e não
  coincidiria com os bytes. Conversões sem processo (`eol`, `ident`, `working-tree-encoding`) também não são
  aplicadas: os bytes vão como estão, e se o gate os viu convertidos o hash difere e a entrega fica sem efeito, com o
  motivo.
* **Commit pelo Git fechado** (`ICommitDeWorktree`, implementado pelo `GerenciadorDeWorktreesGit`):
  1. reconfere a worktree: validação da AD-71, a recusa explicativa de filtros e HEAD = base;
  2. monta a árvore dos bytes atuais, como acima, e recalcula o hash canônico **a partir dessa árvore**, que é um
     objeto imutável e exatamente o que o commit recebe. A árvore precisa ter o hash aprovado e não ser vazia.
     Uma alteração dos arquivos ou do index privado antes do `write-tree` muda a árvore e é recusada. Uma alteração
     depois não entra no commit (revisão do PR #326). O diff rastreado vem de `diff --diff-filter=a <base> <árvore>`
     e cada arquivo novo vem pelo conteúdo do blob (`cat-file blob`). Só objetos. A função de hash é a mesma da
     revisão, e o mesmo retrato dá o hash do pedido e da remontagem (`LerArtefatoAsync`);
  3. `commit-tree --no-gpg-sign -p <base> -m <mensagem> <árvore>`, com o autor e o committer configurados por
     `GIT_AUTHOR_*`/`GIT_COMMITTER_*`. Isso só cria o objeto;
  4. o único efeito: `update-ref -m <reflog> refs/heads/<branch da execução> <novo> <base>`. É um compare-and-swap
     do Git: a branch só sai da base uma vez, então duas aprovações executadas ao mesmo tempo nunca geram dois
     commits. A outra vê o HEAD já movido (payload alterado) ou perde o CAS e fica sem efeito;
  5. a branch é conferida de novo: commit novo, filho da base, com a árvore conferida e a worktree nele. Só então o
     index da worktree passa a ser a árvore gravada (`read-tree <árvore>`, sem `-u`: nenhum arquivo é lido nem
     escrito). Se isso falhar, o commit continua feito, e a worktree só aparece com alterações preparadas.

  O conjunto fechado aceita só essas formas: `update-ref` só numa branch `dante/<handle>/e<n>` e com o valor antigo;
  nunca push, reset, checkout, merge, criação ou remoção de branch. Hooks, gc e maintenance automáticos ficam
  desligados, e nenhuma variável `GIT_*` herdada chega a qualquer processo Git. O checkout principal nunca é tocado.

  Desfechos:
  * **antes do `update-ref`**, divergência ou falha é `Falhou`. Nada foi tocado: a árvore é montada num index
    privado, e o index da worktree e a branch ficam como estavam;
  * **depois do `update-ref`**, só o estado real decide. Se a branch está no commit conferido, `Executada` (com o SHA
    no resultado), mesmo que o Git tenha relatado erro. Se continua na base, `Falhou`. Qualquer outra coisa é
    `Incerto`.
* **Estado real num `Incerto`** (`IObservadorDeAcaoProtegida`; para a entrega, `ObservadorDaEntregaDoArtefato`): a
  recuperação depois do reinício e a falha inesperada no meio consultam o observador da ação antes de gravar o
  `Incerto`. Ele informa HEAD e branch, commits além da base e arquivos do index diferentes do HEAD
  (`diff-index --cached`); os arquivos da worktree não são lidos. O estado vai redigido ao
  ledger e ao aviso.
  A observação usa a mesma identidade da entrega (revisão 5 do PR #326): a raiz é aberta como handle, a identidade é
  conferida nele (raiz, diretório Git registrado para o caminho, diretório comum do repositório cadastrado), e todas
  as consultas rodam no handle com `GIT_DIR` fixo. A branch é relatada, e uma branch diferente da execução é dita.
  Sem identidade comprovada (outro diretório ou outra worktree no caminho, worktree movida), o estado é dito ilegível
  e nada é consultado no diretório que ocupa o caminho: o ledger nunca recebe HEAD, branch ou index de outro
  repositório. No caminho ainda em execução, o `Incerto` do commit é descrito pela âncora já aberta, sem reabrir pelo
  nome; se o nome já não aponta para ela, isso também é dito. Nada é repetido nem desfeito, e um pedido novo de entrega da execução é recusado com esse estado.

Fora desta decisão: push/PR (Issue própria), Brain e planilhas atrás do gateway (Issue própria, depois da ativação
operacional) e a auditoria das approvals nativas da CLI.

Código: `Dante.Domain/Execucoes/{AprovacaoDeAcao, CatalogoDeAcoes, PayloadDaAcao}.cs`,
`Dante.Application/Execucoes/Portas/IAprovacaoDeAcaoRepository.cs`, `Dante.Infrastructure/Modulos/Execucoes/AprovacaoDeAcao*`,
migration `AprovacoesDeAcao`; PR 2: `Dante.Application/Execucoes/{GatewayDeFerramentas, AprovacoesDeAcaoAppService}.cs`,
`Dante.Worker/Coordenacao/FerramentasDeCoordenacao.cs`, `Dante.Worker/Telegram/{TelegramAprovacoesDeAcao,
TelegramAcaoCallback}.cs`, `Dante.Worker/Execucoes/ManutencaoDasAprovacoesDeAcao.cs`; PR 3:
`Dante.Application/Execucoes/{EntregaDoArtefatoAppService, IEntregaDoArtefatoAppService,
ObservadorDaEntregaDoArtefato}.cs`, `Dante.Application/Execucoes/Portas/ICommitDeWorktree.cs`,
`Dante.Application/Execucoes/Contratos/EntregaDoArtefatoDtos.cs`, `Dante.Infrastructure/Contextos/{GerenciadorDeWorktreesGit,
LeituraConfinadaDaWorktree}.cs`,
`Dante.Worker/Telegram/TelegramExecucoes.cs` (`/execucao entregar`), `Dante.Worker/WorkerComposition.cs`
(`Orquestracao:Entrega`).
Testes: `AprovacaoDeAcaoDominioTests`, `AprovacoesDeAcaoPostgreSqlTests`, `GatewayDeFerramentasTests`,
`GatewayDeAprovacoesPostgreSqlTests`; PR 3: `CommitDeWorktreeGitTests` (Git real; o TOCTOU dos filtros em
`FiltroConfiguradoDepoisDaConferenciaNuncaRoda` e `EntregaNaoAbreComandoGitQueLeiaAWorktree`),
`LeituraConfinadaDaWorktreeTests` (troca por link para um sentinela externo antes da abertura e entre a classificação
e a leitura, hard link e link legítimo) e
`RoteiroPelaFilaEndToEndTests.Entrega` (PostgreSQL, Git real e Telegram falso).

## AD-74 — Acompanhamento e cancelamento de execuções na Application, neutros de interface (#305)

Status: vigente, etapa 4 do roadmap da Epic #296. Complementa a AD-66 (consultas sobre o ledger) e a AD-67 (o
cancelamento respeita posse e fencing da fila). Depende do mesmo banco e da mesma chave `Orquestracao:Habilitada`;
nenhuma migration nova.

Contexto: com o ledger e a fila, execuções sobrevivem ao reinício, mas não havia como o dono consultá-las nem cancelar
uma Tarefa (`E…`). Cancelar só o token do executor seria pior que nada: a etapa sem resultado voltaria à fila e a
retomada `Automatica` a reexecutaria. O registro de jobs em memória não guardava o dono, e `/status` e `/cancel` não
isolavam os usuários da allowlist. O Telegram é a primeira interface; um chat próprio ou a WebApi (etapa 10) virão
depois e não podem reimplementar autorização, estados nem cancelamento.

Decisão:

* **Casos de uso na Application, neutros de interface.** `IAcompanhamentoDeExecucoesAppService` (listar, detalhar,
  aguardando, painel) e `IControleDeExecucoesAppService` (cancelar) recebem um `ProprietarioDaExecucao` (canal +
  usuário, a mesma autoridade de origem gravada na execução) e devolvem contratos sem tipo, comando ou texto de
  interface. Execução de outro proprietário (ou de outro canal, ou com o prefixo do outro tipo) é indistinguível de
  inexistente. O adaptador Telegram (`TelegramExecucoes`) só interpreta, chama o caso de uso num escopo e formata.
* **Redaction na Application, antes de gravar e de devolver.** O contexto sai só como modo e aliases (o diretório
  gravado nunca). Objetivo, motivo, erro e o motivo do pedido de cancelamento seguem sempre a mesma ordem: o texto
  integral é inspecionado e substituído se parecer segredo (`ProtecaoDeSegredos`), e só então vira uma linha e é
  cortado. Cortar antes poderia partir uma credencial e deixar um fragmento irreconhecível pelo padrão; o texto
  protegido também respeita o limite. A saída do agente não é persistida e não é exibida.
* **Vínculo etapa → sessão sob posse.** Depois de abrir a sessão reservada e antes de enviar o turno, o executor
  descarrega o espelho do ledger (a criação da sessão é gravada em ordem) e grava `IdSessaoDeAgente` da etapa por
  `IFilaDeExecucoesAppService.VincularSessaoAsync`, sob posse (`GravarComPosseAsync`, trabalho continua reivindicado).
  Só etapa `EmExecucao` e sessão do mesmo dono e canal; posse perdida não grava nada, lança e a sessão reservada é
  encerrada normalmente. Sessão sem espelho (número do contador local, banco indisponível na reserva) segue sem
  vínculo, com aviso no log: o `S…` é informativo, quem decide é a posse. O detalhe mostra o `S…` da etapa.
* **Espera sem posição inventada.** `MotivoDaEspera`: na fila, aguardando nova tentativa (trabalho pendente com
  tentativa anterior e disponibilidade futura), interrompida, bloqueada, aguardando aprovação/entrada/delegações.
  Com `SKIP LOCKED`, prioridade, backoff e reivindicações concorrentes não há posição estável, então nenhuma é exibida.
* **Pedido de cancelamento durável = evento `CancelamentoSolicitado`** (append-only, `dados` com autor e motivo). Numa
  transação, o caso de uso trava os trabalhos ativos da execução (`FOR UPDATE`, sem `SKIP LOCKED`) e só depois a
  execução, na mesma ordem das gravações sob posse (sem deadlock):
  * **sem posse** (trabalho `Pendente`, ou execução interrompida/bloqueada sem trabalho ativo): execução e etapas não
    terminais `Cancelada` com o motivo, trabalho `Descartado`, pedido e transições como eventos. A reivindicação
    concorrente pula as linhas travadas; depois do commit, não há mais o que reivindicar;
  * **com posse** (trabalho `Reivindicado`): quem pede não altera execução, etapa nem trabalho. Grava só o evento (não
    toca a linha da execução, então não conflita com o `xmin` do dono) e, depois do commit, sinaliza o processo local;
  * já terminal: `JaEncerrada`; pedido já registrado: `JaSolicitada`. Pedidos simultâneos se serializam pela trava do
    trabalho: uma transição e um pedido no máximo.
* **Quem tem a posse honra o pedido.** `AvaliarAsync` devolve `Cancelar` (antes até do limite de tentativas), inclusive
  depois de um lease vencido, em vez de executar ou retomar; `IniciarEtapaAsync` recusa
  (`CancelamentoSolicitadoException`) sem gravar; `ConcluirEtapaAsync` grava `Cancelada` no lugar do resultado e devolve
  o estado gravado; `CancelarSolicitadaAsync` grava `Cancelada` sob posse e descarta o trabalho. Posse perdida não grava
  nada (fencing da AD-67).
* **Sinal em memória só como atalho.** `CanalDeCancelamento` (singleton da Application, sem estado durável): o
  `ProcessadorDaFila` registra um token por execução nova antes de conferir o pedido no ledger (fecha a corrida com um
  pedido gravado depois da avaliação), confere de novo a cada heartbeat e na conclusão. Um sinal perdido, ou um pedido
  gravado por outro processo, atrasa o cancelamento em até um heartbeat, nunca o perde. O job legado (`Avulsa`) não
  entra nesse caminho.
* **No máximo um aviso por transição.** O aviso sai só depois de a transição ser gravada, e só por quem a gravou
  (`ExecucaoInterrompidaDto.PorCancelamento`); repetição, reavaliação e concorrência não avisam de novo. Não há outbox de
  notificações: uma falha entre o commit e o envio perde o aviso. O estado no ledger continua consultável.
* **Jobs legados isolados por dono.** `JobRegistry` grava o dono na criação; a sobrecarga do usuário de `TryCancel` e
  `GetVisible` só alcança os jobs dele (job de outro, ou sem dono registrado, é "não encontrado"); a do sistema (posse
  perdida) continua sem dono. `/status`, `/cancel J…` e a lista de quem acessa um vinculado usam a do usuário.
  `/cancel J…` continua no registro em memória; um `J…` que o registro não tem (interrompido num boot anterior) vai ao
  caso de uso, que o cancela só sem posse.
* **Sem pausa nem retomada.** Não há estado `Pausada`, a CLI não tem checkpoint e o pedido de jobs e tarefas fica só em
  memória: depois de um reinício, retomar terminaria cancelado. `ConfirmarRetomadaAsync` não é exposto e passa a recusar
  execução com pedido de cancelamento.

Consequências:

* uma interface nova (chat próprio, WebApi autenticada) consome os mesmos casos de uso informando o próprio canal e
  identidade; nada de autorização ou estado é duplicado no adaptador;
* a Application ganha um terceiro singleton (`CanalDeCancelamento`), registrado por ela mesma;
* a etapa da Tarefa aponta para a sessão reservada que a executou; o vínculo custa uma descarga do espelho e uma
  gravação sob posse por etapa, antes do turno;
* a listagem por proprietário não tem índice próprio (`canal`, `id_do_usuario_no_canal`, `criada_em`); entra com uma
  migration quando o volume justificar;
* a conferência do pedido no heartbeat é uma leitura por heartbeat de execução nova em andamento.

Código: `Dante.Domain/Execucoes/{Execucao.cs (TentarInterpretarCodigo), TipoDeEventoDeOrquestracao.cs}`,
`Dante.Application/Execucoes/{AcompanhamentoDeExecucoesAppService, ControleDeExecucoesAppService, CanalDeCancelamento,
PedidoDeCancelamento, FilaDeExecucoesAppService}.cs`, `Dante.Infrastructure/Fila/FilaDeTrabalhoPostgreSql.cs`,
`Dante.Worker/Execucoes/ProcessadorDaFila.cs`, `Dante.Worker/Jobs/JobRegistry.cs`,
`Dante.Worker/Telegram/{TelegramExecucoes, TelegramPollingService.Execucoes, TelegramPollingService.Agentes}.cs`,
`Dante.Worker/Execucoes/ExecucaoSobPosse.cs`, `Dante.Worker/Sessions/SessionRegistry.cs` (`LedgerNumberWrittenAsync`).
Testes: `AcompanhamentoDeExecucoesTests`, `AcompanhamentoDeExecucoesPostgreSqlTests`, `ExecucoesNoTelegramTests`,
`TarefaPelaFilaEndToEndTests`.

## AD-75 — Coordenação por papel: plano validado, gate do plano e despacho só pelo snapshot (#310)

Status: vigente no domínio e na persistência (PR 1 da #310, #321), na Application, fila e executor (PR 2, #322) e nas
ferramentas do coordenador, síntese validada e interface (PR 3); etapa 8 do roadmap da Epic #296. Proposta da #310
aprovada com as decisões D1–D9 na própria Issue. Complementa a AD-69 (modo `Coordenador` na versão de grupo, gate humano
reutilizado) e a AD-70 (cada unidade é uma delegação `consulta.v1`), sem reescrevê-las. Depende do mesmo banco e da
mesma chave `Orquestracao:Habilitada`; migrations `CoordenacaoPorPapel` e `SinteseDaCoordenacao`, não aplicadas no banco
canônico.

Contexto: o roteiro da AD-69 não decompõe tarefas abertas. O coordenador precisa propor um plano que o sistema valide e
uma pessoa aprove antes de qualquer delegação, sem ganhar permissões por ser coordenador, sem ampliar a execução quando
o cadastro muda e sem apagar histórico ao replanejar. A escrita e qualquer ação protegida dependem do gateway da #311.

Decisão:

* **Papel, não agente especial**: `ModoDeCoordenacao.Coordenador` com `PapelDoCoordenador` (um participante). A versão do
  grupo coordenado não tem roteiro nem rodadas e nenhum participante escreve (domínio, check e trigger). O JSON canônico
  do grupo só muda no modo novo (`formato` 2 com `coordenador`); o do Roteiro continua o do formato 1.
* **Execução `Coordenada`** (tipo 4): raiz, aponta para a versão do grupo **e** para a versão congelada do coordenador
  (check). `CoordenacaoDaExecucao` (um por execução) guarda grupo, versão, hash, papel do coordenador, hash do snapshot
  (mesmo cálculo do Roteiro, sobre `participantes_da_execucao`) e o objetivo protegido como no Roteiro. É a autoridade
  congelada: plano e despacho são conferidos contra estas linhas e as versões que elas apontam, nunca contra o cadastro
  atual.
* **Plano validado, puro e determinístico** (`ValidacaoDoPlano`, D1/D3/D5): entrada é o texto da proposta; saída é a
  forma canônica (unidades em ordem topológica, empate pela proposta; chaves em ordem; NFC, `\n`, sem pontas; papel
  minúsculo; dependências únicas e ordenadas; entrada canônica do contrato) e o SHA-256 calculado aqui, ou todas as
  recusas com motivo e unidade. Recusa campo desconhecido (inclusive um hash enviado), papel fora do snapshot, unidade
  do coordenador, contrato fora de `consulta.v1`, capacidade ausente na versão congelada, entrada inválida, tamanhos,
  dependência inexistente, ciclo, plano vazio e mais unidades que o orçamento (teto global de 4 delegações por execução,
  descontadas as já criadas na raiz). Escrita é `RequerGateway`, sempre recusada, sem porta especulativa do gateway.
  Um plano inválido volta ao coordenador uma vez (`CorrecoesPermitidas`); o fluxo é do PR 2.
* **Plano persistido** (`planos_da_coordenacao`, D6): revisão imutável (conteúdo em texto, hash recalculado do conteúdo
  ao criar e conferido pelo banco), em cadeia (`revisao` 1–3, a substituta aponta para a anterior, uma vez). Revisão
  nova só por `UnidadeSemResposta` (anterior aprovada, nenhuma unidade em curso, ao menos uma `Falhou`/`Cancelada`) ou
  `PedidoHumano` (correção pedida no gate da anterior). A anterior fica `Substituido`; só as etapas pendentes dela viram
  `Ignorada` com motivo; nada é apagado (triggers recusam DELETE e alteração de conteúdo, hash e identidade).
* **Gate do plano = o gate existente com finalidade** (D4): `PedidoDeAprovacao.DoPlano` grava `finalidade =
  PlanoSomenteLeitura`, o plano, o hash do plano como artefato, a revisão como rodada e o hash do snapshot. O banco
  confere na inserção (plano aguardando, mesmo hash e revisão, snapshot da execução) e, no commit, só aceita plano
  `Aprovado`/`Recusado` com a decisão correspondente do gate sobre o mesmo hash. Essa aprovação significa só "autorizar
  este plano somente leitura": não é aprovação do gateway da #311 e não concede escrita, commit, push nem outra ação
  protegida. Um plano não é "substituído" no gate; outra versão tem pedido próprio.
* **Grafo no ledger** (D1): `PlanoDaCoordenacao.Materializar` cria etapas `UnidadeDoPlano` (sem processo próprio, de
  leitura, retomada automática) numeradas na ordem topológica, com papel e versão do participante no snapshot, e as
  `DependenciaDeEtapa`. O banco recusa unidade de plano não aprovado ou de papel fora do snapshot/do coordenador (no
  commit), a mesma unidade duas vezes, alterar o vínculo da etapa com o plano e dependência fora da ordem topológica.
* **Despacho só pelo plano e pelo snapshot** (D2): `Delegacao.CriarDoPlano` exige a unidade em curso de um plano
  aprovado da Coordenada; o solicitante é o coordenador congelado, o destinatário é o participante do papel da unidade
  na versão congelada (íntegra, agente ativo: desativar restringe, editar não amplia), contrato `consulta.v1`, entrada,
  objetivo e critérios do plano, repositórios na interseção das versões congeladas e o teto global. Não usa
  `PodeConsultar`. O trigger da delegação confere de novo, para origem `Coordenada`: etapa de unidade de plano aprovado,
  destinatário e solicitante no snapshot (versão e hash), contrato e capacidade.
* **Execução** (PR 2): `ICoordenacoesAppService` (neutro de interface) e `ExecutorDeCoordenacoesDaFila`.
  * **Etapas da Coordenada, todas no ledger:**
    * planejamento: turno do coordenador, `TurnoDeAgente` com o driver da versão congelada;
    * gate: `AprovacaoHumana` com o pedido do plano;
    * unidades;
    * avanço: etapa mecânica, `TurnoDeAgente` sem agente e sem sessão.
  * **Planejamento:**
    * sessão reservada da versão congelada do coordenador, em `plan`, no contexto congelado, sem o ambiente do Worker
      e vinculada à etapa (proveniência da sessão);
    * a proposta chega pela ferramenta `propor_plano` (PR 3; antes, o bloco JSON da última mensagem, que deixou de
      valer); o turno que termina sem ela conta como recusa;
    * o servidor revalida contra o snapshot sob posse, antes de gravar qualquer plano;
    * a correção única é contada no ledger (evento `PlanoRecusado` por etapa, só com os motivos e nunca com o
      conteúdo); inválido de novo, a etapa e a execução falham sem plano, filhos nem trabalhos.
  * **Gate:**
    * aprovar pelo hash materializa as unidades e enfileira o avanço;
    * corrigir abre um planejamento `PedidoHumano`, dentro de 3 versões;
    * recusar marca o plano `Recusado` e cancela a execução.
  * **Avanço** (sob posse, numa transação):
    * conclui as unidades em curso pelo desfecho da delegação;
    * despacha a próxima unidade pronta da ordem topológica, uma por vez (paralelismo 1), pelo `CriarDoPlano`, com a
      mesma montagem da #309; uma recusa do domínio faz a unidade falhar sem delegação;
    * com a origem `AguardandoDelegacoes`, a retomada do ciclo das delegações cria o próximo avanço;
    * sem nada a despachar, abre um replanejamento `UnidadeSemResposta` (se couber revisão e orçamento); senão, abre
      a síntese do coordenador (PR 3) se alguma unidade foi respondida, ou termina pelas unidades: `Concluida` (todas
      respondidas) ou `Falhou`, com as dependentes `Ignorada`.
  * **Desfechos no replanejamento `UnidadeSemResposta`:** entram entre os marcadores de dado não confiável, e a
    fronteira é garantida pela representação, não pela instrução ao modelo:
    * cada valor vindo de agente (plano anterior, unidade, papel, motivo, resultado) vira um literal de string JSON
      numa linha só;
    * aspas e barra invertida são escapadas; quebras de linha (inclusive U+0085, U+2028 e U+2029) e controles viram
      escape; `<` e `>` viram `<` e `>`;
    * assim nenhum valor fecha o literal, começa linha nova ou contém um marcador; o escape é reversível por um leitor
      JSON (revisão da PR #322).
  * **Plano anterior no replanejamento `PedidoHumano`:** a regra de confiança vale para todo replanejamento, não só
    para `UnidadeSemResposta`. Pedir correção no gate não torna confiável o plano anterior, que foi escrito pelo
    coordenador e pode carregar conteúdo adversarial (segunda revisão da PR #322):
    * a instrução da pessoa responsável é a instrução confiável e vai, sem escape, num bloco próprio entre
      `<<<INSTRUCAO_HUMANA` e `INSTRUCAO_HUMANA>>>`, antes e separado do plano;
    * o plano anterior entra depois, como o mesmo literal de string JSON (`LiteralNaoConfiavel`) numa linha só,
      dentro dos marcadores de dado não confiável;
    * como `<` e `>` são escapados no literal, o plano não fecha o bloco de dado nem falsifica o bloco da instrução.
  * **Conferência da subordinada de uma Coordenada:**
    * pela unidade de plano aprovado e pelo snapshot íntegro, com solicitante e destinatário nas versões e hashes
      congelados;
    * não pela concessão `PodeConsultar` nem pela versão atual do cadastro (editar não amplia; desativar restringe).
  * **Fim por qualquer caminho:** a revisão em espera vira `Cancelado`; as etapas abertas viram `Ignorada` (não
    iniciadas) ou `Cancelada`. A recuperação de origem pronta reconhece a unidade da delegação mais recente.
  * **Consolidação mecânica (D7):**
    * trilha gerada a partir do ledger, separada da síntese do coordenador (PR 3);
    * cobre todas as unidades de todas as revisões, com papel, agente, versão, etapa, delegação, resultado persistido
      com hash, sessão que entregou e motivo;
    * cada revisão aparece com a decisão do gate e a sessão do planejamento;
    * não avalia mérito nem infere consenso.
* **Ferramentas do coordenador** (PR 3): o mesmo `dante_coordenacao` da #308/#309 (servidor, pipe, token e limite), com
  dois perfis novos, um por etapa: `planejamento` (`propor_plano`) e `sintese` (`entregar_sintese`).
  * O perfil é o da etapa que o orquestrador abriu (`CoordenacaoDoCoordenador`), só em `plan`; cada perfil lista só a
    própria ferramenta, e o Worker confere o perfil de novo em cada chamada, pelo canal.
  * Não exige concessão no agente: é o único canal de saída da etapa, de mundo fechado (só grava o plano ou a síntese
    pelos casos de uso, sob a posse da etapa e só pela sessão vinculada), como o perfil `delegacao` da subordinada.
  * A chamada valida e grava na hora (`EntregaDoCoordenador`, serializada com o encerramento pelo executor); as
    recusas voltam na resposta, e a correção única continua contada no ledger.
* **Síntese do coordenador** (D7, PR 3), separada da trilha mecânica:
  * etapa própria, depois das unidades e só com ao menos uma respondida: turno do coordenador com o papel e a versão do
    snapshot (o planejamento não tem papel), na mesma sessão reservada em `plan`; a trilha entra com motivos e
    resultados de outros agentes como literal no bloco de dado não confiável;
  * `ValidacaoDaSintese`, pura, contra a trilha recalculada do ledger sob posse: `resumo`, `conclusoes`,
    `divergencias` e `inconclusivos`, cada item com texto e citações `{revisao, unidade, hash}`; toda unidade de toda
    revisão citada; conclusão e divergência só com unidade respondida e o hash do resultado persistido (ao menos 12
    caracteres; a forma canônica guarda o completo); divergência com ao menos duas unidades; unidade sem resposta só em
    inconclusivo e sem hash; campo desconhecido (inclusive um hash da síntese) recusado; credencial recusada;
  * forma canônica (NFC, `\n`, chaves em ordem, citações em ordem e sem repetição) e SHA-256 do servidor, gravadas em
    `sinteses_da_coordenacao`: uma por execução e por etapa; o trigger confere a etapa de síntese do coordenador
    congelado, a sessão vinculada e o hash do conteúdo, e recusa UPDATE e DELETE. Repete no banco as condições do
    domínio, mesmo por SQL direto: execução não terminal e etapa `EmExecucao`, lidas sob trava `FOR NO KEY UPDATE`
    (o nível do UPDATE que as encerra); sessão desta execução (canal e dono da execução, `id_execucao` nulo ou o desta,
    nenhuma etapa de outra execução vinculada a ela) e do coordenador congelado (handle, versão e hash da configuração
    do participante) — não basta o id coincidir com o vínculo da etapa. Por isso o caso de uso grava a síntese antes de
    concluir a etapa, na mesma transação da posse. A leitura relê a forma canônica contra a trilha e não exibe o que não
    confere;
  * uma correção (evento `SinteseRecusada` por etapa); inválida de novo, não entregue ou com falha do processo, a etapa
    de síntese falha e a execução termina pelas unidades, só com a trilha: uma síntese ruim não desfaz resultados;
  * validação estrutural: não avalia o mérito nem resolve divergência, e é sempre exibida com esse alcance, ao lado da
    trilha.
* **Interface** (PR 3), só adaptadores sobre os casos de uso:
  * cadastro `coordenador=<papel>` (o modo vem do papel; a edição mantém o modo; alguém além do coordenador precisa
    ter capacidade para `consulta.v1`, conferido ao salvar, ativar e resolver);
  * `/grupo <nome> @alias <tarefa>` cria a Coordenada quando a versão ativa é do modo coordenador;
  * `/execucao` mostra plano, unidades e gate, decide o plano pelo hash e expõe `consolidacao` (trilha e síntese);
  * avisos ao dono: plano aguardando decisão (unidades, hash e comandos), planejamento sem plano, fim e cancelamento.
* **Limites de provider** (D9), comportamento inicial documentado:
  * O limite chega como falha do turno, com o texto do provider preservado literalmente na delegação, na etapa e na
    trilha.
  * `Retry-After` não chega ao D.A.N.T.E.:
    * o driver Codex descarta o `codexErrorInfo`;
    * o driver Claude ignora o `rate_limit_event`.
  * A fila já sabe devolver sem consumir tentativa (`DevolverAsync(contarTentativa: false)`), mas falta o sinal. Um
    limite durante uma unidade a termina `Falhou` e consome uma vaga do orçamento.
  * Classificar nos drivers e devolver com `Retry-After` é mecanismo novo, pendente de decisão (registrada na #310).

Consequências:

* a proveniência raiz → grupo/versão → revisão do plano → etapa/unidade → delegação → versão do agente → resultado é
  consultável por SQL e protegida pelo banco; a sessão entra pelo executor do PR 2;
* paralelismo continua 1; com profundidade 1, o orçamento de 4 delegações vale para a execução inteira, também somando
  revisões;
* escrita em grupo coordenado e ações protegidas continuam impossíveis até a #311 (domínio, check e trigger);
* replanejar a pedido humano no meio da execução (fora do gate) não existe: o domínio só aceita `PedidoHumano` sobre o
  plano que aguarda decisão, e mudar isso é decisão pendente;
* a trilha mecânica (PR 2) e a síntese validada (PR 3) ficam separadas; sem síntese válida, vale a trilha;
* a interface só interpreta e formata: nenhuma regra de plano, gate, síntese ou permissão vive no Telegram.

Código: `Dante.Domain/Grupos/{ModoDeCoordenacao, ConfiguracaoDoGrupo, VersaoDoGrupo}.cs`,
`Dante.Domain/Execucoes/{CoordenacaoDaExecucao, ValidacaoDoPlano, PlanoDaCoordenacao, FinalidadeDaAprovacao,
PedidoDeAprovacao, Delegacao (CriarDoPlano), EtapaDaExecucao (DaUnidade), Execucao, TipoDeExecucao, TipoDeEtapa}.cs`,
`Dante.Application/Execucoes/Portas/{ICoordenacaoDaExecucaoRepository, IPlanoDaCoordenacaoRepository}.cs`,
`Dante.Infrastructure/Modulos/Execucoes/{CoordenacaoDaExecucao*, PlanoDaCoordenacao*}.cs`, migration
`CoordenacaoPorPapel`; PR 2: `Dante.Application/Execucoes/{ICoordenacoesAppService, CoordenacoesAppService,
EncerramentoDaCoordenacao, Contratos/CoordenacaoDtos}.cs`, conferência e recuperação em `DelegacoesAppService`,
`Dante.Worker/Execucoes/ExecutorDeCoordenacoesDaFila.cs`; PR 3: `Dante.Domain/Execucoes/{ValidacaoDaSintese,
SinteseDaCoordenacao}.cs`, `EtapaDaExecucao.DaSintese`, `Dante.Infrastructure/Modulos/Execucoes/SinteseDaCoordenacao*.cs`,
migration `SinteseDaCoordenacao`, `Dante.Worker/Coordenacao/{ServidorMcpDeCoordenacao, FerramentasDeCoordenacao,
EscopoDeCoordenacao}.cs`, `Dante.Worker/Execucoes/EntregaDoCoordenador.cs`, `Dante.Worker/Telegram/{TelegramGrupos,
TelegramExecucoes}.cs`, cadastro em `CadastroDeGruposAppService`. Testes: `CoordenacaoPelaFilaEndToEndTests`,
`ExecutorDeCoordenacoesTests`, `CoordenacaoDominioTests` (validação por tabela, canonicidade, revisões, gate,
materialização e despacho) e `CoordenacaoPostgreSqlTests` (proveniência, triggers e checks por fora do domínio,
replanejamento, revisões e materializações simultâneas, migration sobre dados anteriores); PR 3:
`SinteseDaCoordenacaoDominioTests`, `CoordenadorPeloMcpTests`, `CoordenacaoPeloTelegramEndToEndTests` e os de
síntese em `CoordenacaoPelaFilaEndToEndTests` (inclusive o trigger por fora do domínio).
