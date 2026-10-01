# D.A.N.T.E.

**Distributed Agent Network for Task Execution**

D.A.N.T.E. é um orquestrador local em .NET 10 que permite controlar **Claude Code** e **Codex CLI** pelo Telegram.

A proposta é simples: o Telegram funciona como interface remota, enquanto os agentes continuam executando localmente na sua máquina, usando as CLIs já instaladas e autenticadas.

```text
Telegram
   ↓
D.A.N.T.E.
   ↓
Command / Context Router
   ↓
General Workspace ou Repository Registry
   ↓
Claude Code / Codex CLI
   ↓
resultado
   ↓
Telegram
```

## Estado atual

Converse com o agente como no terminal: uma mensagem comum abre uma **sessão interativa** do
agente padrão e as mensagens seguintes continuam a mesma conversa, com a resposta chegando
enquanto o agente trabalha.

```text
Explique a autenticação deste projeto.
Agora mostre onde o token é validado.
```

Aprovações, perguntas do agente, interrupção e encerramento também são feitos pelo Telegram.
`/claude` e `/codex` continuam disponíveis como execução avulsa (one-shot).

O contexto de cada conversa ou execução segue um de dois modos.

### General Mode

Use o agente para perguntas, pesquisas, exemplos de código e outras tarefas que não precisam de um projeto específico.

```text
/claude explique a diferença entre JWT e cookie de sessão

/codex escreva um exemplo simples de Strategy Pattern em C#
```

Essas execuções usam um workspace neutro do D.A.N.T.E. e não entram automaticamente em nenhum repositório cadastrado.

### Repository Mode

Use um alias iniciado por `@` para executar o agente dentro de um repositório específico.

```text
/claude @fitness_backend analise a autenticação atual

/codex @dante revise a implementação do JobRegistry
```

O D.A.N.T.E. resolve o alias de forma determinística e inicia o agente diretamente no diretório cadastrado.

---

## Requisitos

- .NET 10 SDK
- Git
- Telegram
- Codex CLI e/ou Claude Code CLI disponíveis no `PATH`
- pelo menos uma das CLIs autenticada localmente para usar o respectivo agente

O D.A.N.T.E. **não exige uma API key da OpenAI ou Anthropic**.

Você pode utilizar a autenticação já existente da CLI, inclusive quando ela estiver associada à sua conta/assinatura. Se `OPENAI_API_KEY` ou `ANTHROPIC_API_KEY` existirem no ambiente, o D.A.N.T.E. preserva essas variáveis para a CLI, mas elas são opcionais.

Antes de iniciar o Worker, confirme que as CLIs funcionam diretamente no mesmo usuário/ambiente em que o D.A.N.T.E. será executado.

---

## Executando o projeto

Clone o repositório e execute:

```bash
dotnet build Dante.sln
dotnet test Dante.sln
dotnet run --project src/Dante.Worker
```

O Worker permanece em execução até receber um sinal de encerramento, como `Ctrl+C`.

---

## Configuração do Telegram

Crie um bot usando o **BotFather** e configure o token no ambiente.

### Bash / WSL

```bash
export Telegram__BotToken="<token>"
export Telegram__AllowedUserIds="123456789"
```

Para mais de um usuário:

```bash
export Telegram__AllowedUserIds="123456789,987654321"
```

### PowerShell

```powershell
$env:Telegram__BotToken = "<token>"
$env:Telegram__AllowedUserIds = "123456789"
```

A autorização utiliza `message.from.id`, e não username ou ID do chat.

Se `Telegram__AllowedUserIds` estiver ausente ou possuir uma entrada inválida, os comandos ficam bloqueados.

O Telegram usa **long polling**. Não é necessário webhook, domínio público ou porta exposta.

---

## Primeiro teste

Com o Worker rodando:

```text
/ping
```

Resposta esperada:

```text
pong
```

---

# Uso

## Consultas gerais

Sem `@alias`, o comando utiliza o **General Mode**.

```text
/claude qual a diferença entre RabbitMQ e Kafka?
```

```text
/codex mostre uma implementação simples de Result Pattern em C#
```

O workspace padrão é:

```text
~/.dante/workspaces/general
```

Você pode substituí-lo:

```bash
export DANTE_GENERAL_WORKSPACE="/caminho/absoluto/general"
```

O workspace geral não pode conter um repositório cadastrado nem ficar dentro dele. O D.A.N.T.E. bloqueia esse tipo de sobreposição para evitar que uma consulta geral modifique projetos reais.

No General Mode:

- nenhum repositório é escolhido por inferência;
- variáveis específicas de projetos não são carregadas;
- Codex executa com sandbox de workspace;
- Claude executa em perfil restrito de ferramentas de arquivo.

---

## Cadastrando repositórios

Os repositórios conhecidos pelo D.A.N.T.E. ficam em:

```text
~/.dante/repositories.json
```

### Adicionar

```text
/repo add @dante /mnt/c/Repos/dante juanverass/dante
```

Outro exemplo:

```text
/repo add @fitness_backend /mnt/c/Repos/fitnessapp_backend juanverass/fitnessapp_backend
```

O terceiro argumento, `owner/repo`, é opcional.

Para paths contendo espaços, utilize aspas:

```text
/repo add @meu_projeto "/mnt/c/Meus Projetos/app" usuario/repositorio
```

O D.A.N.T.E. valida:

- formato do alias;
- path absoluto;
- existência do diretório;
- se o diretório é a raiz de um repositório Git;
- compatibilidade do `origin` com `owner/repo`, quando informado.

Aliases são case-insensitive.

### Listar

```text
/repos
```

Exemplo:

```text
@dante: /mnt/c/Repos/dante (juanverass/dante)
@fitness_backend: /mnt/c/Repos/fitnessapp_backend (juanverass/fitnessapp_backend)
```

### Consultar

```text
/repo show @fitness_backend
```

### Remover

```text
/repo remove @fitness_backend
```

---

## Executando dentro de um repositório

Depois do cadastro:

```text
/claude @fitness_backend analise as entidades atuais do domínio
```

```text
/codex @dante implemente a próxima tarefa
```

O primeiro argumento após `/claude` ou `/codex` é tratado como alias somente quando começa com `@`.

Por exemplo:

```text
/codex explique o uso de @Transactional
```

continua sendo uma consulta geral, pois `@Transactional` não é o primeiro argumento.

Quando um alias é usado:

1. o D.A.N.T.E. resolve o cadastro;
2. fixa o working directory;
3. remove o `@alias` do prompt;
4. carrega o ambiente daquele repositório;
5. cria o job;
6. inicia a CLI no diretório resolvido.

O agente não é responsável por procurar o projeto no disco.

---

# Variáveis de ambiente por repositório

Cada repositório pode possuir seu próprio perfil de ambiente.

## Valor literal

Use apenas para valores não sensíveis:

```text
/repo env set @fitness_backend ASPNETCORE_ENVIRONMENT Development
```

Outro exemplo:

```text
/repo env set @fitness_backend API_BASE_URL https://localhost:5001
```

Não utilize `env set` para senhas, tokens, connection strings com credenciais ou outros segredos.

## Referência a uma variável do host

Para valores sensíveis, configure primeiro a variável no sistema operacional:

```bash
export FITNESS_DATABASE_PASSWORD="<valor>"
```

Depois faça apenas o vínculo:

```text
/repo env bind @fitness_backend DATABASE_PASSWORD FITNESS_DATABASE_PASSWORD
```

O valor real não é gravado no catálogo do D.A.N.T.E.

Ele é resolvido somente quando o job inicia.

Se a variável do host não existir, o agente não é iniciado.

## Listar configuração

```text
/repo env list @fitness_backend
```

Exemplo:

```text
ASPNETCORE_ENVIRONMENT (literal)
DATABASE_PASSWORD (host: FITNESS_DATABASE_PASSWORD)
```

Os valores não são exibidos.

## Remover

```text
/repo env remove @fitness_backend DATABASE_PASSWORD
```

Jobs que utilizam bindings sensíveis não retornam a saída do agente pelo Telegram, reduzindo o risco de um segredo aparecer acidentalmente na resposta.

---

# Agente padrão

O agente padrão fica salvo em `~/.dante/settings.json` e sobrevive a reinícios. Sem
configuração, é **Claude**.

Consultar:

```text
/agent
```

```text
Agente padrão: Claude
```

Alterar (aceita `claude` ou `codex`, sem diferenciar maiúsculas):

```text
/agent set codex
```

```text
Agente padrão alterado para Codex.
```

`/agent` não inicia job. `/claude` e `/codex` valem somente para a execução em que são
usados e não alteram o agente padrão.

O agente padrão vale para conversas e execuções novas. Uma sessão já aberta mantém o seu
agente, e a resposta avisa:

```text
Agente padrão alterado para Codex.
A sessão ativa S000001 (Claude, General) continua; envie /session start para conversar com Codex.
```

## Conversa (mensagens sem slash command)

Mensagens de texto que não começam com `/` são uma conversa com o agente padrão:

```text
Explique o padrão Strategy.
```

```text
E o padrão State?
```

Sem sessão ativa, a primeira mensagem abre uma sessão interativa do agente padrão no
contexto atual (repositório ativo ou General Mode), com o perfil escolhido em `/permissions`,
e vira o primeiro turno. As mensagens seguintes são novos turnos da mesma sessão, no mesmo
processo do agente, que mantém o contexto da conversa. Não é preciso `/session start`.

A resposta mostra essencialmente o texto do agente, com linhas curtas de progresso
(`→ dotnet test`, arquivos alterados, falha de ferramenta) e o indicador "digitando…"
enquanto o agente trabalha. A conversa não mostra `Job ID`, nem avisos de início e fim de
turno: IDs de sessão, turno e entrega ficam em `/status`, `/session list` e nos comandos
explícitos. Só pedidos que exigem ação — aprovação ou pergunta do agente — mostram IDs,
porque os comandos de resposta precisam deles. Uma mensagem enviada enquanto o agente ainda
responde entra na fila e é confirmada com um curto `Recebido`.

Um `@alias` no início da primeira mensagem abre a sessão naquele repositório, sem alterar o
repositório ativo:

```text
@dante revise o README
```

Com uma sessão ativa, a mensagem vai para ela como está: agente e contexto da sessão não
mudam.

A conversa continua até ser encerrada (`/session close`) ou trocada (`/session start`,
`/session select`). Depois de `/session close` ou `/session select none`, a próxima
mensagem abre uma sessão nova. Se a sessão falhar — por exemplo, o processo do agente
encerrar —, o D.A.N.T.E. avisa e recusa as mensagens seguintes até você iniciar outra com
`/session start`, em vez de abrir silenciosamente uma conversa nova sem o contexto anterior.
Erros na conversa são curtos; os detalhes ficam em `/status`.

`/claude` e `/codex` seguem disponíveis como execução avulsa (one-shot, com Job ID), sem
tocar na conversa. Mensagens iniciadas por `/` com comando desconhecido nunca são enviadas
ao agente: o D.A.N.T.E. responde `Comando desconhecido`.

## Repositório ativo

Cada usuário autorizado pode manter um repositório ativo para as próximas mensagens, sem
repetir `@alias`:

```text
/use @fitness_backend
```

```text
Implemente a issue 500.
```

A mensagem acima, assim como `/claude <prompt>` e `/codex <prompt>` sem alias, roda em
`@fitness_backend`. Um `@alias` explícito vale só para aquela execução:

```text
/codex @dante revise o README
```

Depois disso, o repositório ativo continua sendo `@fitness_backend`.

```text
/use            consulta o contexto ativo (General ou @alias)
/use general    volta ao General Mode
```

`/use @alias` só aceita repositórios cadastrados e não inicia job. O contexto é por
usuário e sobrevive a reinícios. Como no `/agent set`, uma sessão já aberta continua no
contexto em que começou, e a resposta indica `/session start` para conversar no novo
contexto. `/repo remove` limpa o repositório ativo de quem o usava;
se um repositório ativo deixar de existir por outro motivo, o D.A.N.T.E. recusa a
execução e pede `/use @alias` ou `/use general`, em vez de cair para General Mode.

---

# Jobs

Cada chamada a `/claude` ou `/codex` gera um job: uma execução one-shot, que inicia a CLI,
espera o resultado final e encerra. Mensagens comuns são conversa em sessão interativa e
não geram job.

Ao iniciar:

```text
Codex iniciado. Job ID: J000001 (General).
```

Ou:

```text
Claude iniciado. Job ID: J000002 (@fitness_backend).
```

## Consultar jobs

```text
/status
```

Exemplo:

```text
J000001 Codex General: Succeeded | criado 2026-09-28 12:00:00 UTC
J000002 Claude @fitness_backend: Running | criado 2026-09-28 12:03:00 UTC
```

Os estados possíveis são:

- `Queued`
- `Running`
- `Succeeded`
- `Failed`
- `Cancelled`

O histórico mantém os 20 jobs concluídos mais recentes em memória.

Ele é perdido quando o Worker reinicia.

## Cancelar

```text
/cancel J000002
```

O cancelamento é propagado ao processo e o D.A.N.T.E. encerra a árvore de processos iniciada pelo agente.

---

# Comandos disponíveis

| Comando | Descrição |
| --- | --- |
| `/ping` | Verifica se o bot está respondendo |
| `/claude <prompt>` | Executa Claude em General Mode |
| `/codex <prompt>` | Executa Codex em General Mode |
| `/claude @alias <prompt>` | Executa Claude em um repositório |
| `/codex @alias <prompt>` | Executa Codex em um repositório |
| `/repos` | Lista repositórios cadastrados |
| `/repo add @alias <path> [owner/repo]` | Cadastra um repositório |
| `/repo show @alias` | Exibe um repositório |
| `/repo remove @alias` | Remove um repositório |
| `/repo env set @alias KEY VALUE` | Define configuração literal não sensível |
| `/repo env bind @alias KEY HOST_ENV` | Vincula uma variável a uma variável do host |
| `/repo env list @alias` | Lista nomes e origens das variáveis |
| `/repo env remove @alias KEY` | Remove uma configuração de ambiente |
| `/agent` | Exibe o agente padrão |
| `/agent set claude\|codex` | Altera o agente padrão |
| `<mensagem>` | Conversa: novo turno da sessão ativa; sem sessão ativa, abre uma com o agente padrão no contexto ativo |
| `/use` | Exibe o contexto ativo do usuário |
| `/use @alias` | Define o repositório ativo |
| `/use general` | Volta ao General Mode |
| `/status` | Exibe jobs, sessões próprias e estado da entrega recente ao Telegram |
| `/cancel <jobId>` | Solicita cancelamento de um job |
| `/session start [claude\|codex] [@alias] [manual\|auto\|plan]` | Inicia e seleciona uma sessão interativa; usa agente, contexto e perfil selecionados se omitidos |
| `/session list` | Lista as suas sessões |
| `/session select <id\|none>` | Seleciona uma sessão; `none` faz a próxima mensagem abrir uma sessão nova |
| `/session stop [id]` | Interrompe o turno e descarta a fila, mantendo a sessão |
| `/session close [id]` | Encerra a sessão e seu processo |
| `/permissions` | Consulta o perfil para novas sessões (`manual` por padrão) |
| `/permissions manual\|auto\|plan` | Escolhe o perfil para novas sessões do usuário |
| `/approve <sessionId> <turnId> <requestId>` | Aprova a ação solicitada uma vez |
| `/approve-session <sessionId> <turnId> <requestId>` | Aprova para a sessão quando o agente oferece essa opção |
| `/deny <sessionId> <turnId> <requestId> [motivo]` | Nega a ação solicitada |
| `/input <sessionId> <turnId> <requestId> <resposta1> [ \| <resposta2> ...]` | Responde às perguntas na ordem exibida |
| `/steer <orientação>` | Orienta imediatamente o turno da sessão ativa; no Claude, interrompe o turno e prioriza a orientação |
| `/resend <jobId\|sessionId[/turnId]>` | Reenvia as partes pendentes da saída recente, sem executar o agente novamente |

Durante um turno interativo, mensagens comuns entram na fila. A sessão mantém o mesmo agente e
repositório até ser encerrada, mesmo que `/agent set` ou `/use` mudem depois. Eventos são
agrupados por cerca de 750 ms antes do envio, em mensagens de até 4000 caracteres; a saída da
sessão ativa chega sem prefixo e a de qualquer outra sessão é identificada por `[S…]` — inclusive
quando você troca de sessão no meio de um turno: a partir da troca, a anterior passa a aparecer
com `[S…]`. Partes de um mesmo turno saem com pelo menos 1,5 s entre si. Uma
falha de entrega aparece em `/status` separadamente do resultado da execução; `/resend` tenta
novamente as partes ainda não entregues. Resultados recentes ficam em memória enquanto o Worker
está vivo.

O perfil `manual` é o padrão recomendado. `auto` opera dentro dos limites da CLI com menos
interrupções; `plan` restringe alterações. O perfil escolhido por `/permissions` fica em memória
até o reinício do Worker e vale apenas para sessões novas; um perfil informado em `/session start`
vale somente para aquela sessão. O acesso `full` não é oferecido. Os drivers mantêm os mapeamentos
específicos de Claude e Codex; o fluxo one-shot continua independente dessas escolhas.

Pedidos de aprovação e input mostram os IDs da sessão, turno e solicitação, com comandos prontos
para responder. Uma solicitação pendente aparece em `/status` e expira após cinco minutos; a
aprovação é negada ao expirar e uma resposta de input vazia é enviada ao agente. Respostas de
outro usuário, duplicadas, tardias ou para outro turno são rejeitadas. Com segredos vinculados ao
ambiente do repositório, o bot omite detalhes da ação e das perguntas, mas mantém os IDs e os
comandos para permitir a decisão humana.

---

# Exemplo completo

Cadastro inicial:

```text
/repo add @dante /mnt/c/Repos/dante juanverass/dante
/repo add @fitness_backend /mnt/c/Repos/fitnessapp_backend juanverass/fitnessapp_backend
```

Confira:

```text
/repos
```

Conversa no repositório:

```text
/use @dante
```

```text
Leia a issue #68 e me diga por onde começar.
```

```text
Pode seguir com o primeiro passo.
```

Se o agente pedir aprovação, a mensagem traz os comandos prontos:

```text
Aprovação pendente S000001 T000002 R000001: git push origin feat/issue-68
/approve S000001 T000002 R000001
/deny S000001 T000002 R000001 [motivo]
```

Encerrar a conversa:

```text
/session close
```

Pergunta geral avulsa (one-shot):

```text
/claude explique arquitetura hexagonal de forma simples
```

Trabalho no D.A.N.T.E.:

```text
/codex @dante revise o RepositoryRegistry e identifique possíveis problemas
```

Trabalho no fitness backend:

```text
/claude @fitness_backend analise as issues abertas e me explique a próxima tarefa
```

Acompanhar:

```text
/status
```

Cancelar se necessário:

```text
/cancel J000004
```

---

# Segurança

O D.A.N.T.E. foi desenhado para controlar agentes, não para fornecer um shell remoto.

Principais proteções atuais:

- somente Telegram User IDs autorizados executam comandos;
- prompts são tratados como dados;
- argumentos são enviados usando `ProcessStartInfo.ArgumentList`;
- `UseShellExecute=false`;
- executáveis suportados são definidos pelo D.A.N.T.E.;
- o usuário não escolhe arbitrariamente executáveis ou argumentos internos;
- paths de repositório são validados;
- General Mode é isolado dos repositórios cadastrados;
- ambientes de repositórios são aplicados somente ao processo filho;
- bindings de secrets não armazenam o valor no catálogo;
- valores de `OPENAI_API_KEY` e `ANTHROPIC_API_KEY`, quando existentes, são mascarados nas respostas do Telegram,
  inclusive em cada evento de sessão interativa e quando o valor chega dividido entre partes do streaming;
- sessões e jobs com segredos vinculados ao ambiente do repositório omitem a saída do agente;
- aprovações e respostas de input só são aceitas do dono da sessão, no turno e na solicitação certos, uma vez;
- o perfil de acesso irrestrito (`full`) não é oferecido;
- cancelamento encerra a árvore do processo.

Evite enviar qualquer segredo diretamente pelo Telegram.

---

# Persistência

Atualmente:

| Informação | Persistência |
| --- | --- |
| Configurações do assistente (agente padrão, repositório ativo por usuário) | `~/.dante/settings.json` |
| Repositórios cadastrados | `~/.dante/repositories.json` |
| Perfis de ambiente | `~/.dante/repositories.json` |
| Valores de bindings secretos | não são persistidos |
| Workspace geral | `~/.dante/workspaces/general` |
| Jobs | somente memória |
| Histórico de jobs | somente memória |
| Sessões interativas, turnos, filas e solicitações pendentes | somente memória (perdidas ao reiniciar o Worker) |
| Perfil escolhido em `/permissions` | somente memória |
| Saídas recentes para `/resend` | somente memória |

`~/.dante/settings.json` guarda apenas preferências, nunca tokens ou segredos:

```json
{
  "DefaultAgent": "Claude",
  "ActiveRepositories": {
    "123456789": "@fitness_backend"
  }
}
```

Enquanto o arquivo não existe, o agente padrão é **Claude**; o arquivo é criado na
primeira alteração, com escrita atômica. `DefaultAgent` aceita somente `Claude` ou
`Codex`; `ActiveRepositories` associa Telegram User IDs a aliases. Arquivo corrompido ou com valor desconhecido impede o Worker de iniciar com
erro claro, em vez de escolher um agente por conta própria.

---

# Arquitetura atual

```text
Telegram
   ↓
TelegramPollingService
   ↓
Command Parser
   ├──────────────→ RepositoryRegistry
   │                   ↓
   │              Repository Mode
   │
   └──────────────→ GeneralWorkspace
                       ↓
                  General Mode

Context resolvido
   ├─ mensagem comum ──→ SessionRegistry
   │                        ↓
   │                   ClaudeSessionDriver (stream-json) / CodexSessionDriver (app-server)
   │                        ↓
   │                   InteractiveAgentProcess (um processo vivo por sessão)
   │                        ↓ eventos
   │                   TelegramDeliveryService → Telegram
   │
   └─ /claude, /codex ──→ JobRegistry
                            ↓
                       ClaudeRunner / CodexRunner
                            ↓
                       AgentProcessExecutor
                            ↓
                       Claude Code / Codex CLI
```

Responsabilidades principais:

- **TelegramPollingService** — recebe comandos e responde ao Telegram;
- **TelegramUserAuthorizer** — controla usuários permitidos;
- **RepositoryRegistry** — mantém aliases, paths e ambientes dos projetos;
- **GeneralWorkspace** — fornece o workspace neutro;
- **SessionRegistry** — sessões interativas por usuário: dono, contexto fixo, turnos, fila e solicitações;
- **ClaudeSessionDriver / CodexSessionDriver** — traduzem os protocolos estruturados das CLIs em eventos neutros;
- **TelegramDeliveryService** — agrupa, redige e entrega eventos e resultados, com retry e `/resend`;
- **JobRegistry** — controla estado, contexto e cancelamento dos jobs;
- **ClaudeRunner / CodexRunner** — definem como cada CLI é iniciada;
- **AgentProcessExecutor** — executa processos sem shell e captura stdout/stderr.

---

# Limitações atuais

Ainda não fazem parte do projeto:

- worktrees automáticos por issue;
- seleção automática de issues pelo D.A.N.T.E.;
- fluxo automático issue → implementação → review → merge;
- handoff automático entre Claude, Codex e Tech Lead;
- fila persistente;
- persistência de jobs em banco de dados;
- execução concorrente isolada por worktree;
- comando `/ask` com agente padrão;
- sessões interativas persistentes: reiniciar o Worker encerra as conversas;
- botões inline para aprovação (os comandos textuais estão disponíveis);
- perfil de acesso irrestrito (`full`);
- pergunta do Codex ao usuário (input) fora do perfil `plan`, por limitação do `app-server`.

Esses pontos são candidatos naturais para os próximos MVPs.

---

# Desenvolvimento

Build:

```bash
dotnet build Dante.sln
```

Testes:

```bash
dotnet test Dante.sln
```

Executar Worker:

```bash
dotnet run --project src/Dante.Worker
```

Configurações locais e segredos não devem ser versionados.

## Desenvolvimento com agentes

Claude Code, Codex e desenvolvedores humanos seguem o mesmo
[contrato de desenvolvimento](docs/development/agent-contract.md). `CLAUDE.md` e
`AGENTS.md` são adaptadores finos que apontam para ele. O contexto persistente do
projeto (visão, estado atual, decisões e histórico) está em [`docs/context/`](docs/context/).

### Agent Harness

O Agent Harness é o conjunto de regras e registros que permite a qualquer agente
iniciar, continuar ou revisar uma Issue sem depender do histórico de uma conversa. Tudo
o que o próximo worker precisa vem de fontes persistidas:

| Fonte | O que informa |
| --- | --- |
| Labels `status:*` da Issue | em que fase a Issue está ([backlog](docs/development/backlog.md)) |
| Comentários `## TURNO ASSUMIDO`, `## HANDOFF`, `## TURNO FINALIZADO` na Issue | quem está no turno, branch, checkpoint, próximos passos e Decision Locks ([turnos](docs/development/handoff.md)) |
| Comentários `## REVIEW` no PR | veredito e correções solicitadas ([review](docs/development/review.md)) |
| Git e testes | o estado real do código — vence qualquer registro divergente |

Regra fixa: `1 Issue → 1 branch → 1 PR`, quantos turnos e agentes forem necessários.

#### Iniciar trabalho

Trabalho novo só sai de Issues `status:ready` que não sejam Epic nem tenham dependência
aberta:

```bash
gh issue list --state open --label status:ready
gh issue edit <numero> --remove-label status:ready --add-label status:in-progress
git switch -c <prefixo>/issue-<numero>-<slug>
gh issue comment <numero> --body-file <claim>   # ## TURNO ASSUMIDO, antes de alterar arquivos
```

Depois: baseline (`dotnet build` + `dotnet test`), implementação dentro do escopo,
validação e staging seletivo, conforme o [contrato](docs/development/agent-contract.md).

#### Continuar trabalho

Para uma Issue `status:in-progress`, use a skill `$continuar-turno`: ela recupera o
[handoff](docs/development/handoff.md#continuar-turno) nos comentários da Issue,
confirma ownership, branch e PR, compara o checkpoint com o Git e publica um novo
`## TURNO ASSUMIDO` antes de qualquer alteração. Se não houver handoff confiável, siga
o [RECOVERY MODE](docs/development/handoff.md#recovery-mode) preservando o trabalho
recebido. Em `status:review`, só continue se o PR pedir correção; use a mesma branch e
o mesmo PR.

#### Encerrar trabalho

Use a skill `$encerrar-turno` e registre a validação real. Se o trabalho estiver
inacabado, faça checkpoint e push, depois publique `## HANDOFF` na Issue. Se a
implementação estiver concluída, abra o PR, mova a Issue para `status:review` e publique
`## TURNO FINALIZADO` na Issue. Os [registros de turno](docs/development/handoff.md#onde-vivem-os-registros)
ficam nos comentários da Issue, nunca em arquivos do repositório.
