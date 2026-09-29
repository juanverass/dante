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

Status: vigente (MVP 2, #21)

O repositório só é usado quando o comando traz `@alias` como primeiro argumento. Sem
alias, a execução é General Mode. Alias desconhecido é erro. O D.A.N.T.E. nunca deduz o
repositório a partir do texto do prompt.

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

---

## MVP 3

Nenhuma decisão do MVP 3 (Epic #32) está consolidada no código ainda. As regras de
precedência de agente e contexto propostas na Epic entram aqui quando forem
implementadas.
