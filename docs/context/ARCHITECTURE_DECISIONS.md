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
  sessão aparece para o usuário (`/status`, registry) é da #65;
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
