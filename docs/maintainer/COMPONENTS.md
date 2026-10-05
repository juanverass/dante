# Componentes do D.A.N.T.E.

Este documento desce da arquitetura para o código. A ideia é permitir que você encontre rapidamente **quem é responsável por cada comportamento**.

---

# 1. Composição do processo

## `Program.cs`

É o composition root do sistema.

Responsabilidades:

- criar o Generic Host;
- registrar dependências;
- definir lifetimes;
- conectar interfaces às implementações;
- registrar os hosted services.

Todos os serviços principais são `Singleton`, porque representam estado ou infraestrutura compartilhada durante a vida do Worker.

Composição atual:

```text
IAgentExecutableResolver       → AgentExecutableResolver
IAgentProcessExecutor          → AgentProcessExecutor
IInteractiveAgentProcessLauncher → InteractiveAgentProcessLauncher
IClaudeRunner                  → ClaudeRunner
ICodexRunner                   → CodexRunner
IAgentModelCatalog             → AgentModelCatalog
IAgentSessionDriverFactory     → AgentSessionDriverFactory
IAgentSessionEventSink         → TelegramDeliveryService
ITelegramBotApi                → TelegramBotApi

Singletons concretos:
GeneralWorkspace
JobRegistry
SessionRegistry
RepositoryRegistry
AssistantSettingsStore
TelegramDeliveryService
TelegramUserAuthorizer
HttpClient

Hosted services:
Worker
TelegramPollingService
```

### Por que isso importa no debugging?

Se uma dependência estiver sendo criada com configuração errada, `Program.cs` é o primeiro lugar a verificar.

---

## `Worker.cs`

É propositalmente simples.

Ele:

1. registra no log que o D.A.N.T.E. iniciou;
2. mantém o processo vivo;
3. registra encerramento gracioso.

Ele **não executa o Telegram polling**.

Quem recebe mensagens é `TelegramPollingService`.

Isso explica um diagnóstico importante:

> ver “DANTE iniciado” no console prova que o Host subiu, mas não prova que o Telegram está funcionando.

---

# 2. Módulo `Agents`

O módulo `Agents` cuida da execução local das CLIs e de capacidades que não pertencem a uma conversa específica.

```text
Agents/
├── AgentExecutableResolver
├── AgentProcessStartInfo
├── AgentProcessExecutor
├── InteractiveAgentProcessLauncher
├── InteractiveAgentProcess
├── ProcessTree
├── ClaudeRunner
├── CodexRunner
├── AgentModelCatalog
└── contratos/modelos auxiliares
```

---

## `AgentKind`

Enum que identifica os agentes suportados:

- Claude;
- Codex.

É a identidade neutra usada pelo D.A.N.T.E. antes de chegar a detalhes de CLI.

---

## `AgentExecutableResolver`

Descobre o executável real de Claude ou Codex.

Objetivo arquitetural:

> o usuário escolhe **qual agente**, nunca fornece um executável arbitrário.

Se o binário não puder ser localizado, a execução é recusada.

---

## `AgentProcessStartInfo`

Centraliza a construção segura de `ProcessStartInfo`.

É usado pelos dois caminhos:

- one-shot;
- interativo.

Regras importantes:

- working directory precisa ser absoluto;
- diretório precisa existir;
- executável vem do resolver;
- `UseShellExecute = false`;
- stdout e stderr são redirecionados;
- argumentos entram em `ArgumentList`;
- o prompt nunca é concatenado em uma shell command;
- ambiente pode ser filtrado.

### Filtragem de ambiente

Quando a execução é General Mode ou possui ambiente explícito de repositório, o processo não herda cegamente todas as variáveis do host.

São preservadas apenas variáveis necessárias, como:

- `PATH`;
- `HOME`;
- temporários;
- locale;
- proxy;
- autenticação opcional das CLIs.

Depois, variáveis específicas do repositório são aplicadas.

---

## `AgentProcessExecutor`

Executor do caminho **one-shot**.

Fluxo:

```text
AgentProcessRequest
      ↓
AgentProcessStartInfo
      ↓
Process.Start()
      ↓
ReadToEndAsync(stdout/stderr)
      ↓
WaitForExitAsync()
      ↓
AgentProcessResult
```

Características:

- captura saída completa;
- não faz streaming;
- mata a árvore do processo em cancelamento;
- transforma falhas de inicialização em `AgentProcessResult`.

Ele é apropriado para jobs curtos e independentes.

---

## `InteractiveAgentProcessLauncher`

Inicia o processo de longa duração usado por sessões.

Também usa `AgentProcessStartInfo`, portanto compartilha as regras de segurança do one-shot.

Sua saída é um `InteractiveAgentProcess`.

---

## `InteractiveAgentProcess`

Wrapper de baixo nível de um processo interativo.

Ele é um dos componentes mais importantes do sistema.

### Responsabilidades

- ler stdout incrementalmente;
- ler stderr incrementalmente;
- disponibilizar linhas por `Channel<AgentOutputLine>`;
- aceitar escrita serializada em stdin;
- fechar stdin;
- parar o processo graciosamente;
- matar a árvore quando necessário;
- rastrear descendentes para evitar processos órfãos.

### Estado próprio

```text
Running
   ↓
InputClosed
   ↓
Exited

ou

Running
   ↓
Stopping
   ↓
Exited
```

Esse estado é do **processo**, não da sessão.

### Backpressure

O channel de saída é bounded.

Se o consumidor não acompanhar:

```text
consumer lento
    ↓
Channel cheio
    ↓
pump de stdout espera
    ↓
pipe do processo pressiona o produtor
```

Assim o sistema evita crescimento ilimitado de memória.

### Escrita em stdin

`WriteLineAsync` usa `SemaphoreSlim`.

Duas escritas concorrentes não podem intercalar bytes e corromper JSONL/JSON-RPC.

---

## `ProcessTree`

Auxilia no gerenciamento de descendentes do processo.

Problema resolvido:

> matar somente o processo pai pode deixar subprocessos do agente vivos.

O sistema rastreia PID + horário de início para evitar também matar acidentalmente um PID reutilizado pelo SO.

---

## `ClaudeRunner`

Adapter one-shot do Claude.

Constrói os argumentos que o Claude Code precisa e entrega um `AgentProcessRequest` ao executor.

É usado por:

```text
/claude ...
```

Não é o componente usado para a conversa interativa.

---

## `CodexRunner`

Equivalente one-shot para o Codex.

Usado por:

```text
/codex ...
```

---

## `AgentModelCatalog`

Descobre dinamicamente os modelos oferecidos pelas CLIs instaladas.

Não existe uma lista hardcoded confiada pelo D.A.N.T.E.

### Claude

O catálogo inicia a CLI apenas o suficiente para obter informações do protocolo de inicialização.

### Codex

Consulta `model/list` no app-server.

### Cache

Resultados válidos ficam em cache por alguns minutos.

Isso evita consultar as CLIs em cada comando, mas permite perceber mudanças sem reiniciar o Worker.

---

# 3. Módulo `Jobs`

Jobs representam somente execuções one-shot.

```text
Jobs/
├── JobRegistry
├── JobSnapshot
└── JobStatus
```

---

## `JobExecutionContext`

Representa onde uma execução acontece. Desde a #166 vive em `Dante.Application/Contextos` (AD-38).

Dois modos:

```text
General
Repository
```

Carrega, entre outros dados:

- working directory;
- label do contexto;
- alias quando aplicável.

O mesmo tipo é reutilizado por sessões para descrever contexto, mas isso **não transforma sessão em job**.

---

## `AgentContextResolver`

Ponto único de decisão de agente e contexto (AD-27), usado por mensagens comuns que abrem sessão,
`/session start`, `/claude` e `/codex`. Desde a #166 vive em `Dante.Application/Contextos` e lê
workspace geral, catálogo e preferências pelas portas `IWorkspaceGeral`, `ICatalogoDeRepositorios`
e `IPreferenciasDoAssistente` (AD-38).

```text
Agente:       explícito → agente padrão
Repositório:  @alias explícito → repositório ativo → General Mode
```

Devolve agente, contexto, ambiente do repositório, prompt sem `@alias` e a origem de cada decisão. Recusas
(`ContextResolutionFailure`) nunca caem para outro contexto. Não grava nada em settings: overrides valem para uma
execução.

---

## `JobRegistry`

Catálogo em memória dos jobs.

Responsabilidades:

- criar ID;
- registrar job;
- fornecer CancellationToken;
- marcar início;
- solicitar cancelamento;
- concluir;
- produzir snapshots;
- manter somente histórico recente.

### Estados

Conceitualmente:

```text
Queued
  ↓
Running
  ↓
Succeeded / Failed / Cancelled
```

### Importante

O Registry não executa Claude/Codex.

Ele controla **estado e cancelamento**.

Quem executa é o runner + `AgentProcessExecutor`.

---

# 4. Módulo `Repositories`

```text
Repositories/
├── RepositoryDefinition
└── RepositoryRegistry
```

---

## `RepositoryDefinition`

Modelo persistido de um repositório cadastrado. A resolução de contexto (#166) o lê como
`RepositorioCadastrado` (alias e caminho) pela porta `ICatalogoDeRepositorios` (AD-38).

Representa:

- alias;
- path;
- referência GitHub opcional;
- configuração de ambiente.

---

## `RepositoryRegistry`

Catálogo explícito de repositórios que o D.A.N.T.E. pode utilizar.

### Alias

Formato:

```text
@dante
@fitness_backend
```

É validado por regex e normalizado.

### Adição

Ao cadastrar um repo, o registry valida aspectos como:

- path;
- raiz Git;
- remote, quando uma referência `owner/repo` é fornecida.

### Por que existe?

Sem ele, um prompt poderia tentar levar o agente a qualquer diretório do host.

O registry cria uma allowlist de contextos válidos.

---

## Ambiente por repositório

Há dois tipos.

### Literal

```text
KEY=value
```

Pode ser persistido no catálogo.

Nomes que parecem sensíveis não podem ser cadastrados como literal.

### Binding ao host

```text
KEY ← HOST_ENV
```

O valor real não é salvo.

Na execução:

```text
RepositoryRegistry
      ↓
Environment.GetEnvironmentVariable(HOST_ENV)
      ↓
process environment
```

Se a variável do host estiver ausente, a execução falha antes de iniciar o agente.

---

# 5. Módulo `Settings`

```text
Settings/
├── AssistantSettings
└── AssistantSettingsStore
```

---

## `AssistantSettings`

Configuração global simples do assistente, como agente padrão.

---

## `AssistantSettingsStore`

Persistência de preferências por usuário Telegram.

Arquivo padrão:

```text
~/.dante/settings.json
```

Pode armazenar:

- agente padrão;
- repositório ativo;
- modo de sessão;
- modelo por agente;
- esforço por agente.

### Escrita atômica

O processo é:

```text
serializa
   ↓
arquivo temporário
   ↓
File.Move(... overwrite)
```

Isso reduz risco de deixar um JSON pela metade.

### Fail-closed

Configuração corrompida ou inválida não é “corrigida por adivinhação”.

O Worker falha ao iniciar.

Essa postura evita operar com preferências diferentes das esperadas pelo usuário.

---

# 6. Módulo `Sessions`

Este é o núcleo do modo interativo.

```text
Sessions/
├── AgentSession
├── SessionRegistry
├── IAgentSessionDriver
├── AgentSessionDriverFactory
├── ClaudeSessionDriver
├── CodexSessionDriver
├── AgentEvent
├── AgentSessionSnapshot
├── AgentPermissionProfile
├── AgentSessionModes
└── MessageDelivery
```

---

## `AgentSession`

Máquina de estados pura da sessão.

Ela controla:

- estado;
- turno atual;
- fila;
- requests pendentes;
- expiração;
- decisões de submit;
- transições.

Ela **não chama o driver diretamente**.

Essa separação é proposital:

```text
AgentSession
   = decide o que deve acontecer

SessionRegistry
   = executa a operação no driver
```

---

## Turno

Uma sessão pode viver por vários turnos.

```text
Sessão S000001
├── Turno T000001
├── Turno T000002
└── Turno T000003
```

Um turno começa quando uma mensagem é submetida com a sessão ociosa.

---

## Fila

Se a sessão está rodando e chega outra mensagem comum:

```text
mensagem nova
   ↓
MessageDelivery.Queue
   ↓
fila da AgentSession
```

Quando o turno termina, o Registry inicia a próxima.

Essa é a política conservadora padrão.

---

## Steer

`MessageDelivery.Steer` pede para a nova mensagem influenciar o turno corrente.

Capacidade depende do agente.

### Codex

Possui steer nativo.

### Claude

Não possui steer nativo no protocolo usado.

O fallback é:

```text
interrompe turno atual
      ↓
mensagem steer ganha prioridade
      ↓
abre novo turno
```

---

## Requests

Approval ou input humano vira request correlacionado.

IDs:

```text
S... sessão
T... turno
R... request
```

O request possui timeout.

Depois de consumido ou expirado não pode ser respondido novamente.

---

## `SessionRegistry`

É o orquestrador das sessões.

Ele mantém:

```text
session id → Entry
```

Cada `Entry` contém:

- `AgentSession`;
- driver;
- perfil;
- modelo;
- ambiente;
- tasks de pump;
- primitivas de sincronização.

### Responsabilidades

- abrir sessão;
- selecionar sessão ativa;
- validar ownership;
- submeter mensagem;
- chamar `StartTurnAsync`;
- chamar steer;
- interromper;
- fechar;
- responder approval/input;
- consumir eventos do driver;
- aplicar evento na máquina de estados;
- publicar evento no sink;
- iniciar próximo item da fila;
- falhar sessão quando o driver quebra.

### Ownership

Toda operação valida o Telegram User ID.

Uma sessão de outro usuário é tratada como não encontrada.

---

## `IAgentSessionDriver`

Contrato que esconde as diferenças de Claude e Codex.

Operações principais:

- Start;
- StartTurn;
- Steer;
- InterruptTurn;
- Respond;
- ReadEvents;
- Close.

O restante do sistema não deveria precisar saber como essas operações viram JSON da CLI.

---

## `AgentDriverCapabilities`

Declara diferenças reais entre agentes.

Exemplos:

- steer nativo;
- approvals;
- input humano;
- modos suportados.

Isso evita fingir que Claude e Codex oferecem exatamente o mesmo protocolo.

---

## `ClaudeSessionDriver`

Adapter do protocolo bidirecional do Claude Code.

Base:

```text
claude --print
--input-format stream-json
--output-format stream-json
```

### Uma sessão

Um processo Claude permanece vivo.

### Turno

É enviado como mensagem estruturada em stdin.

### Approval/Input

Pedidos de controle do Claude são convertidos em:

- `ApprovalRequestedEvent`;
- `UserInputRequestedEvent`.

### Steer

Não há steer nativo no contrato adotado.

---

## `CodexSessionDriver`

Adapter de:

```text
codex app-server --listen stdio://
```

Usa JSON-RPC sobre stdin/stdout.

### Sessão

Um thread efêmero do Codex representa a sessão D.A.N.T.E.

### Turnos

Criados via requests do app-server.

### Steer

Codex possui `turn/steer`.

### Approval/Input

Requests enviados pelo servidor são registrados e respondidos pelo ID JSON-RPC correspondente.

---

## `AgentEvent`

Contrato neutro de eventos.

Principais eventos:

- `TurnStartedEvent`;
- `MessageDeltaEvent`;
- `MessageCompletedEvent`;
- `ToolStartedEvent`;
- `ToolCompletedEvent`;
- `FileChangeEvent`;
- `WarningEvent`;
- `ErrorEvent`;
- `ApprovalRequestedEvent`;
- `UserInputRequestedEvent`;
- `RequestResolvedEvent`;
- `RequestExpiredEvent`;
- `TurnCompletedEvent`.

Pense no driver como um tradutor:

```text
protocolo específico
      ↓
AgentEvent
      ↓
restante do D.A.N.T.E.
```

---

## `AgentSessionSnapshot`

Representação somente-leitura do estado atual.

É o que outras camadas devem consumir em vez de manipular internamente a sessão.

---

## `AgentSessionModes`

Centraliza nomes amigáveis e parsing de perfis.

Modos atuais:

- manual;
- auto;
- plan.

A tradução concreta para permissões fica nos drivers.

---

# 7. Módulo `Telegram`

É a camada mais próxima do usuário.

```text
Telegram/
├── TelegramPollingService
├── TelegramBotApi
├── ITelegramBotApi
├── TelegramUserAuthorizer
├── TelegramDeliveryService
├── TelegramMessageFormatter
├── TelegramApprovalCallback
├── TelegramUpdate
└── TelegramOptions
```

---

## `TelegramOptions`

Recebe configuração da seção:

```text
Telegram
```

As variáveis de ambiente usam a convenção do .NET:

```text
Telegram__BotToken
Telegram__AllowedUserIds
```

---

## `TelegramUserAuthorizer`

Parseia a allowlist e responde:

```text
esse message.from.id pode usar o bot?
```

A política é fail-closed.

Sem allowlist válida, ninguém é autorizado.

---

## `ITelegramBotApi`

Port da integração com a Bot API.

Mantém o restante do código testável sem HTTP real.

---

## `TelegramBotApi`

Adapter HTTP.

Responsável por chamadas como:

- `getUpdates`;
- `sendMessage`;
- `sendChatAction`;
- `editMessageText`;
- `answerCallbackQuery`.

### Long polling

`getUpdates` usa timeout e offset.

O offset avança conforme updates são consumidos.

### Formatação

Mensagens técnicas podem usar `parse_mode = HTML`.

Erros específicos de markup são transformados em `TelegramMarkupException` para permitir fallback em texto simples.

---

## `TelegramPollingService`

É o **controller/orchestrator de entrada** do sistema.

É grande porque concentra a superfície de comandos do bot.

### Loop principal

```text
GetUpdatesAsync(offset)
      ↓
para cada update
      ↓
avança offset
      ↓
callback ou message
      ↓
Handle...
```

Exceções do polling são registradas sem vazar o token e o loop tenta novamente.

### Tipos de entrada

Entre os comportamentos tratados:

- `/ping`;
- repositórios;
- agente padrão;
- contexto ativo;
- status;
- sessões;
- modos;
- modelos;
- esforço;
- approvals;
- input;
- steer;
- resend;
- cancel;
- one-shot;
- mensagens comuns.

### Mensagem comum

Vai para `ConverseAsync`.

Se não houver sessão ativa:

1. resolve contexto;
2. resolve agente padrão;
3. resolve modo;
4. resolve modelo/esforço;
5. abre sessão;
6. registra a sessão no delivery;
7. envia a mensagem como primeiro turno.

Se houver sessão ativa:

- envia para a mesma sessão.

### Comando desconhecido

Slash command não reconhecido **não vira prompt de IA**.

Isso é uma proteção deliberada.

---

## `TelegramMediaReceiver`

Recebimento de mídias (#94, AD-29). Para uma mensagem com mídia de usuário autorizado:

1. recusa áudio, voz, vídeo, video note, animação e documento não-imagem, sem baixar;
2. escolhe o maior `PhotoSize` dentro do limite (ou o documento `image/*`);
3. baixa por `ITelegramBotApi.DownloadFileAsync` com limite de 7 MB e timeout;
4. grava via `AttachmentStore`, que valida o conteúdo, e registra em `PendingAttachments`.

Álbuns (`media_group_id`) recebem uma única resposta após a janela de 1,5 s.

Com legenda (#95), a legenda é o pedido: depois de guardar as imagens, o receptor a despacha como o texto que as
consome, e a confirmação só traz recusas. O álbum é um lote: as imagens só entram nos pendentes quando ele termina,
junto com a legenda e no contexto em que chegou. Qualquer update posterior do mesmo usuário conclui antes os álbuns
abertos dele (`CompleteAlbumsAsync`), preservando a ordem dos pedidos; pela janela de 1,5 s, a conclusão passa pelo
mesmo semáforo do `TelegramPollingService`, que trata um update por vez. Se o contexto mudou por outro meio, imagens e
legenda são descartadas juntas.

O próximo texto que chega a um agente (conversa, `/steer`, `/claude`, `/codex`) faz `Take` dos pendentes do
contexto atual e os move para o diretório da sessão ou do job (`AttachmentStore.MoveTo`). A entrada vira um
`AgentInput` (texto + anexos), que o `SessionRegistry` valida (dono, tipo imagem, `ImageInput` do driver) e enfileira
como um item só. Cada driver traduz: Claude em blocos `image` base64 rotulados, antes do texto; Codex em itens
`localImage`, no `turn/start` e no `turn/steer`. Os runners one-shot recebem `--add-dir` + lista de paths (Claude) ou
`-i` por imagem (Codex). O diretório da sessão é apagado quando ela termina; o do job, quando ele termina.

## `AttachmentStore` / `PendingAttachments` / `ImageInspector`

- `AttachmentStore`: arquivos em `~/.dante/attachments/<usuário>/<escopo>/`, nomes gerados, `700`/`600`,
  exclusão restrita ao próprio diretório e limpeza de sobras com mais de 24 h;
- `PendingAttachments`: lote por usuário e chave de contexto, até 10 imagens e 20 MB, expiração em 10 min,
  descarte quando o contexto muda e `Take` para quem for executar o turno (#95);
- `AttachmentStore.MoveTo`/`DeleteScope`: mudam os anexos consumidos para `<usuário>/<sessão|job>/` e apagam esse
  diretório no fim; um diretório com id de execução anterior é apagado antes de ser reutilizado;
- `ImageInspector`: identifica JPEG, PNG, GIF e WebP e as dimensões pelos bytes.

---

## `ArtifactStore`

Arquivos que saem para o Telegram (#97, AD-29). `Capture(owner, path, root)` resolve links simbólicos, exige o caminho
real dentro da raiz do canal, recusa diretório, vazio, acima de 50 MB e nomes de credencial, e grava uma cópia privada
`F000001.<ext>` em `~/.dante/artifacts/<usuário>/`. Identifica imagem pelo conteúdo e decide se ela cabe no `sendPhoto`.

## `TelegramDeliveryService`

Responsável pela etapa:

```text
evento/resultado
      ↓
texto seguro e adequado ao Telegram
      ↓
entrega
```

É independente do resultado lógico da execução.

### Por que separar?

Um agente pode terminar com sucesso enquanto o Telegram falha.

Então:

```text
turno = Completed
entrega = Failed
```

é um estado válido.

### Responsabilidades

- agregar deltas;
- formatar progresso;
- controlar prefixos;
- batching;
- limitar tamanho;
- redigir segredos;
- indicar typing;
- retry;
- rate limit;
- fallback HTML → plain;
- armazenar partes para `/resend`;
- manter teclados de approval;
- editar mensagem após decisão/expiração.

### Limites

- mensagem: até 4000 caracteres reservando espaço de prefixo;
- registros recentes em memória;
- cadência para não bombardear o chat.

---

## `TelegramMessageFormatter`

Converte conteúdo técnico para HTML seguro.

Suporta fenced code:

````markdown
```csharp
var x = 1;
```
````

O conteúdo do modelo nunca é confiado como HTML.

Ele é escapado.

Também:

- preserva linguagem quando válida;
- mantém tags fechadas em cada parte;
- evita quebrar surrogate pairs;
- mantém estado de fence entre batches;
- formata comandos como bash sem interpretar fences do comando.

---

## `TelegramApprovalCallback`

Codifica callback data pequeno e correlacionável.

A callback contém IDs e decisão, não o comando sensível.

Na volta, o sistema valida:

- formato;
- usuário;
- chat;
- message ID;
- sessão;
- turno;
- request;
- capacidade.

---

# 8. Relação entre os módulos

## Sessão interativa

```text
Telegram
   ↓
TelegramPollingService
   ↓
Settings + RepositoryRegistry
   ↓
SessionRegistry
   ↓
AgentSession
   ↓
ClaudeSessionDriver / CodexSessionDriver
   ↓
InteractiveAgentProcess
   ↓
CLI

CLI
 ↓
driver
 ↓
AgentEvent
 ↓
SessionRegistry
 ↓
TelegramDeliveryService
 ↓
TelegramBotApi
 ↓
Telegram
```

## One-shot

```text
Telegram
 ↓
TelegramPollingService
 ↓
JobRegistry
 ↓
ClaudeRunner / CodexRunner
 ↓
AgentProcessExecutor
 ↓
CLI
 ↓
AgentProcessResult
 ↓
JobRegistry.Complete
 ↓
TelegramDeliveryService
 ↓
Telegram
```

---

# 9. Onde colocar código novo

Uma regra prática:

- **regra neutra da conversa** → `Sessions`;
- **detalhe específico de uma CLI** → driver correspondente;
- **execução de processo** → `Agents`;
- **estado one-shot** → `Jobs`;
- **path/alias/env** → `Repositories`;
- **preferência persistente** → `Settings`;
- **comando ou UX do Telegram** → `Telegram`.

Evite colocar regra de protocolo do Claude/Codex dentro de `TelegramPollingService`.

Evite colocar lógica de Telegram dentro de `AgentSession`.

Essas duas fronteiras são especialmente importantes para preservar a arquitetura.
